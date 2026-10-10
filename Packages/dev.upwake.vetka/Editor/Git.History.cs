using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upwake.Vetka
{
    internal partial class Git
    {
        public List<GitBranch> Branches() => ReadBranches().Branches;

        public (GitResult Result, List<GitBranch> Branches) ReadBranches()
        {
            var result = Run("for-each-ref",
                "--format=%(refname)%1f%(refname:lstrip=2)%1f%(upstream:short)%1f%(upstream:track)%1f%(HEAD)",
                "refs/heads", "refs/remotes");
            if (!result.IsSuccess)
            {
                return (result, new List<GitBranch>());
            }

            return (result, result.Output
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(new[] { LogFieldSeparator }, StringSplitOptions.None))
                .Where(fields => fields.Length == 5)
                .Where(fields => !(fields[0].StartsWith("refs/remotes/") && fields[0].EndsWith("/HEAD")))
                .Select(fields => new GitBranch(fields[1], fields[0].StartsWith("refs/remotes/"),
                    fields[4].Trim() == "*", fields[2], fields[3], fields[0]))
                .ToList());
        }

        public List<string> RecentBranches()
        {
            var result = Run("reflog", "--format=%gs", "-n", "300");
            var recent = new List<string>();
            if (!result.IsSuccess)
            {
                return recent;
            }

            const string prefix = "checkout: moving from ";
            foreach (var line in result.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith(prefix))
                {
                    continue;
                }

                var move = line.Substring(prefix.Length);
                var separator = move.LastIndexOf(" to ", StringComparison.Ordinal);
                if (separator < 0)
                {
                    continue;
                }

                foreach (var name in new[] { move.Substring(separator + " to ".Length), move.Substring(0, separator) })
                {
                    if (!recent.Contains(name))
                    {
                        recent.Add(name);
                    }
                }
            }

            return recent;
        }

        public List<GitFileChange> CommitFiles(string commit)
        {
            var files = new List<GitFileChange>();
            var result = Run("show", "--diff-merges=first-parent", "--format=", "--name-status", "-z", "--no-renames",
                commit);
            if (!result.IsSuccess)
            {
                return files;
            }

            string status = null;
            foreach (var token in result.Output.Split('\0'))
            {
                var value = token.Trim('\n', '\r');
                if (value.Length == 0)
                {
                    continue;
                }

                if (status == null)
                {
                    status = value;
                    continue;
                }

                files.Add(new GitFileChange(Git.ParseStatus(status.Substring(0, 1)), value));
                status = null;
            }

            files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
            return files;
        }

        public (GitResult Result, List<GitBlameLine> Lines) Blame(string path)
        {
            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return (top, new List<GitBlameLine>());
            }

            var tracked = NulSeparated(Run("ls-files", "-z", "--full-name", "--", ":(top,icase,literal)" + path).Output);
            if (tracked.Count == 1)
            {
                path = tracked[0];
            }

            var result = Run("blame", "--porcelain", "--", Path.Combine(top.Output, path));
            if (!result.IsSuccess)
            {
                return (result, new List<GitBlameLine>());
            }

            if (result.Output.IndexOf('\0') >= 0)
            {
                return (GitResult.Failure("Blame is not available for binary files"), new List<GitBlameLine>());
            }

            return (result, ParseBlame(result.Output));
        }

        internal static List<GitBlameLine> ParseBlame(string output)
        {
            var lines = new List<GitBlameLine>();
            var commits = new Dictionary<string, (string Author, DateTime Date, string Summary, string FileName)>();
            string hash = null;
            var number = 0;
            (string Author, DateTime Date, string Summary, string FileName) info = default;

            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.StartsWith("\t"))
                {
                    if (hash == null)
                    {
                        continue;
                    }

                    commits[hash] = info;
                    lines.Add(new GitBlameLine(hash, info.Author, info.Date, info.Summary, info.FileName, number,
                        line.Substring(1)));
                    hash = null;
                    continue;
                }

                if (hash == null)
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 3 && (parts[0].Length == 40 || parts[0].Length == 64) &&
                        int.TryParse(parts[2], out var final))
                    {
                        hash = parts[0];
                        number = final;
                        commits.TryGetValue(hash, out info);
                    }

                    continue;
                }

                var separator = line.IndexOf(' ');
                var key = separator < 0 ? line : line.Substring(0, separator);
                var value = separator < 0 ? "" : line.Substring(separator + 1);
                switch (key)
                {
                    case "author":
                        info.Author = value;
                        break;
                    case "author-time":
                        if (long.TryParse(value, out var seconds))
                        {
                            info.Date = DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
                        }

                        break;
                    case "summary":
                        info.Summary = value;
                        break;
                    case "filename":
                        info.FileName = value;
                        break;
                }
            }

            return lines;
        }

        public GitResult CommitFileDiff(string commit, string path) =>
            Run("show", "--diff-merges=first-parent", "--format=", "--no-color", "--no-ext-diff", commit, "--",
                ":(top,literal)" + path);

        public List<GitCommit> Log(int limit = 50) => ReadLog(limit).Commits;

        public (GitResult Result, List<GitCommit> Commits) ReadLog(int limit = 50)
        {
            var page = ReadLogPage(null, 0, limit);
            return (page.Result, page.Commits);
        }

        internal static int CommitsPerRead { get; set; } = 200;

        public (GitResult Result, List<string> Commits, List<string> Paths, HashSet<string> Unpushed) FileHistory(
            string head, string path, bool withMeta)
        {
            var found = new List<(string Hash, long Time)>();
            var listed = new HashSet<string>();
            var unpushed = new HashSet<string>();
            var paths = new List<string>();
            var target = PushTarget();
            var anyRemote = !string.IsNullOrWhiteSpace(RemoteRefs());
            var segments = new Queue<(string Name, string Tip)>();
            var visited = new HashSet<(string, string)>();
            var examined = new HashSet<(string, string)>();
            segments.Enqueue((path, head));
            while (segments.Count > 0 && visited.Count < MaxRenamesFollowed)
            {
                var (name, tip) = segments.Dequeue();
                if (!visited.Add((name, tip)))
                {
                    continue;
                }

                var names = withMeta ? new[] { name, name + ".meta" } : new[] { name };
                paths.AddRange(names);
                var walk = Run(new[]
                    {
                        "log", "--full-history", "--simplify-merges", "--no-renames", "--diff-merges=first-parent",
                        "--name-status", "-z", "--format=%x1e%H %ct %P", tip
                    }
                    .Concat(PathFilter(names))
                    .ToArray());
                if (!walk.IsSuccess)
                {
                    return (walk, new List<string>(), paths, unpushed);
                }

                var added = new List<string>();
                foreach (var record in HistoryRecords(walk.Output))
                {
                    if (record.Parents.Length > 1 && record.Changes.Count == 0)
                    {
                        continue;
                    }

                    if (listed.Add(record.Hash))
                    {
                        found.Add((record.Hash, record.Time));
                    }

                    if (record.Changes.Contains(("A", name)) && examined.Add((record.Hash, name)))
                    {
                        added.Add(record.Hash);
                    }
                }

                if (anyRemote)
                {
                    var outgoing = Run(new[] { "log", "--full-history", "--format=%H" }
                        .Concat(OutgoingRange(target, tip))
                        .Concat(PathFilter(names))
                        .ToArray());
                    if (outgoing.IsSuccess)
                    {
                        unpushed.UnionWith(Lines(outgoing.Output));
                    }
                }

                foreach (var hash in added)
                {
                    if (!Run("rev-parse", "-q", "--verify", hash + "^").IsSuccess ||
                        Run("rev-parse", "-q", "--verify", hash + "^2").IsSuccess)
                    {
                        continue;
                    }

                    var source = RenameSource(hash + "^", hash, name);
                    if (source != null)
                    {
                        segments.Enqueue((source, hash + "^"));
                    }
                }
            }

            if (!anyRemote)
            {
                unpushed.UnionWith(listed);
            }

            var commits = found
                .Select((commit, index) => (commit.Hash, commit.Time, Index: index))
                .OrderByDescending(commit => commit.Time)
                .ThenBy(commit => commit.Index)
                .Select(commit => commit.Hash)
                .ToList();
            return (GitResult.Success(""), commits, paths.Distinct().ToList(), unpushed);
        }

        private const int MaxRenamesFollowed = 100;

        private static IEnumerable<(string Hash, long Time, string[] Parents, List<(string Status, string Path)> Changes)>
            HistoryRecords(string output)
        {
            foreach (var record in output.Split(new[] { '\u001e' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = record.Split('\0').Select(field => field.Trim('\n', '\r')).ToList();
                var header = fields[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var changes = new List<(string, string)>();
                for (var i = 1; i + 1 < fields.Count; i += 2)
                {
                    changes.Add((fields[i], fields[i + 1]));
                }

                yield return (header[0], header.Length > 1 && long.TryParse(header[1], out var time) ? time : 0,
                    header.Skip(2).ToArray(), changes);
            }
        }

        private string RenameSource(string parent, string commit, string name)
        {
            var deleted = Run("diff", "--name-only", "-z", "--no-renames", "--diff-filter=D", parent, commit);
            var sources = deleted.IsSuccess
                ? deleted.Output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            if (sources.Length == 0)
            {
                return null;
            }

            var direct = RenamedFrom(parent, commit, name, sources);
            if (direct.SameGuid == true)
            {
                return direct.Old;
            }

            var meta = RenamedFrom(parent, commit, name + ".meta", sources);
            var metaSource = meta.Old != null && meta.Old.EndsWith(".meta")
                ? meta.Old.Substring(0, meta.Old.Length - ".meta".Length)
                : null;
            if (metaSource != null && meta.SameGuid == true)
            {
                return metaSource;
            }

            if (direct.Old != null && direct.SameGuid == null)
            {
                return direct.Old;
            }

            if (metaSource != null && meta.SameGuid == null)
            {
                return metaSource;
            }

            return direct.Status == "R100" ? direct.Old : null;
        }

        internal static int PathCharactersPerRead { get; set; } = 20000;

        private (string Status, string Old, bool? SameGuid) RenamedFrom(string parent, string commit, string path,
            IEnumerable<string> deleted)
        {
            (string Status, string Old, bool? SameGuid) best = (null, null, false);
            var candidates = deleted.Where(source => source.EndsWith(".meta") == path.EndsWith(".meta")).ToList();
            var parts = PathParts(candidates.Where(source => FileName(source) == FileName(path)))
                .Concat(PathParts(candidates.Where(source => FileName(source) != FileName(path))));
            foreach (var part in parts)
            {
                if (best.Status == "R100" || best.SameGuid == true)
                {
                    break;
                }

                var renames = Run(new[] { "diff", "-M", "-l1000", "--name-status", "-z", parent, commit }
                    .Concat(PathFilter(part.Prepend(path)))
                    .ToArray());
                if (!renames.IsSuccess)
                {
                    break;
                }

                var found = (best.Status, best.Old);
                var tokens = renames.Output.Split('\0');
                for (var i = 0; i < tokens.Length;)
                {
                    var status = tokens[i].Trim('\n', '\r');
                    if (!status.StartsWith("R") && !status.StartsWith("C"))
                    {
                        i += 2;
                        continue;
                    }

                    if (i + 2 < tokens.Length && status.StartsWith("R") && tokens[i + 2] == path &&
                        (found.Old == null || RenameScore(status) > RenameScore(found.Status)))
                    {
                        found = (status, tokens[i + 1]);
                    }

                    i += 3;
                }

                if (found.Old != best.Old)
                {
                    best = (found.Status, found.Old, SameGuid(parent, found.Old, commit, path));
                }
            }

            return best;
        }

        private static int RenameScore(string status) =>
            int.TryParse(status.Substring(1), out var score) ? score : 0;

        private static string FileName(string path) => path.Substring(path.LastIndexOf('/') + 1);

        private static IEnumerable<List<string>> PathParts(IEnumerable<string> paths)
        {
            var part = new List<string>();
            var length = 0;
            foreach (var path in paths)
            {
                if (part.Count > 0 && length + path.Length > PathCharactersPerRead)
                {
                    yield return part;
                    part = new List<string>();
                    length = 0;
                }

                part.Add(path);
                length += path.Length + 20;
            }

            if (part.Count > 0)
            {
                yield return part;
            }
        }

        private bool? SameGuid(string parent, string old, string commit, string renamed)
        {
            var before = MetaGuid(parent, old);
            var after = MetaGuid(commit, renamed);
            return before == null || after == null ? (bool?)null : before == after;
        }

        private string MetaGuid(string revision, string path)
        {
            var meta = path.EndsWith(".meta") ? path : path + ".meta";
            var content = Run("show", $"{revision}:{meta}");
            return content.IsSuccess
                ? Lines(content.Output)
                    .Where(line => line.StartsWith("guid:"))
                    .Select(line => line.Substring("guid:".Length).Trim())
                    .FirstOrDefault()
                : null;
        }

        private static IEnumerable<string> Lines(string output) =>
            output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        public (GitResult Result, List<GitCommit> Commits) ReadCommits(IReadOnlyList<string> hashes,
            ISet<string> unpushed)
        {
            var result = GitResult.Success("");
            var commits = new List<GitCommit>();
            for (var start = 0; start < hashes.Count; start += CommitsPerRead)
            {
                result = Run(new[]
                    {
                        "log", "--no-walk=unsorted", "--pretty=format:%H%x1f%h%x1f%an%x1f%ad%x1f%D%x1f%s",
                        "--decorate=short", "--decorate-refs=refs/tags/", "--date=short"
                    }
                    .Concat(hashes.Skip(start).Take(CommitsPerRead))
                    .Append("--")
                    .ToArray());
                if (!result.IsSuccess)
                {
                    return (result, new List<GitCommit>());
                }

                commits.AddRange(Lines(result.Output)
                    .Select(line => line.Split(new[] { LogFieldSeparator }, 6, StringSplitOptions.None))
                    .Where(fields => fields.Length == 6)
                    .Select(fields => new GitCommit(fields[1], fields[2], fields[3], fields[5],
                        !unpushed.Contains(fields[0]), TagNames(fields[4]))));
            }

            return (result, commits);
        }

        public string RemoteRefs()
        {
            var refs = Run("for-each-ref", "--format=%(objectname) %(refname)", "refs/remotes");
            return refs.IsSuccess ? refs.Output : null;
        }

        public (GitResult Result, List<GitCommit> Commits, string Head) ReadLogPage(string head, int skip, int count)
        {
            var none = new List<GitCommit>();
            if (head == null)
            {
                var resolved = Run("rev-parse", "-q", "--verify", "HEAD^{commit}");
                if (!resolved.IsSuccess)
                {
                    var repository = Run("rev-parse", "--git-dir");
                    return (repository.IsSuccess ? GitResult.Success("") : repository, none, null);
                }

                head = resolved.Output;
            }

            var result = Run("log", "--skip=" + skip, "-n", count.ToString(),
                "--pretty=format:%h%x1f%an%x1f%ad%x1f%D%x1f%s", "--decorate=short", "--decorate-refs=refs/tags/",
                "--date=short", head, "--");
            if (!result.IsSuccess)
            {
                return (result, none, head);
            }

            var outgoing = new HashSet<string>(OutgoingCommitHashes(head, skip + count));

            return (result, result.Output
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(new[] { LogFieldSeparator }, 5, StringSplitOptions.None))
                .Where(fields => fields.Length == 5)
                .Select(fields => new GitCommit(fields[0], fields[1], fields[2], fields[4],
                    !outgoing.Contains(fields[0]), TagNames(fields[3])))
                .ToList(), head);
        }

        private static List<string> TagNames(string decoration) =>
            decoration.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)
                .Where(name => name.StartsWith("tag: "))
                .Select(name => name.Substring("tag: ".Length))
                .ToList();

        public string LastCommitMessage()
        {
            var result = Run("log", "-1", "--pretty=format:%B");
            return result.IsSuccess ? result.Output : "";
        }

        public bool IsHeadPushed()
        {
            var unpushed = Run("rev-list", "-n", "1", "HEAD", "--not", "--remotes");
            return unpushed.IsSuccess && unpushed.Output.Length == 0;
        }

        public string HeadCommit()
        {
            var head = Run("rev-parse", "HEAD");
            return head.IsSuccess ? head.Output : null;
        }

        public GitResult UndoLastCommit(string expectedHead)
        {
            var refusal = RefuseDuringOperation("Undoing the last commit");
            if (refusal.HasValue)
            {
                return refusal.Value;
            }

            var moved = HeadMoved(expectedHead);
            if (moved.HasValue)
            {
                return moved.Value;
            }

            if (Run("rev-parse", "-q", "--verify", "HEAD^2").IsSuccess)
            {
                return GitResult.Failure(
                    "The last commit is a merge, undoing it would stage the merged changes as your own");
            }

            if (!Run("rev-parse", "-q", "--verify", "HEAD~1").IsSuccess)
            {
                if (Run("rev-parse", "--is-shallow-repository").Output == "true")
                {
                    return GitResult.Failure(
                        "The parent of the last commit is not in this shallow clone, fetch more history first");
                }

                var branch = Run("symbolic-ref", "-q", "HEAD");
                if (!branch.IsSuccess)
                {
                    return GitResult.Failure(
                        "The first commit can be undone only on a branch, check out a branch first");
                }

                var head = Run("rev-parse", "HEAD");
                var root = head.IsSuccess ? Run("update-ref", "-d", branch.Output, head.Output) : head;
                return root.IsSuccess ? GitResult.Success("First commit undone, its changes are staged") : root;
            }

            var result = Run("reset", "--soft", "HEAD~1");
            return result.IsSuccess
                ? GitResult.Success("Last commit undone, its changes are staged")
                : result;
        }

        public GitResult Amend(string message, string expectedHead)
        {
            var moved = HeadMoved(expectedHead);
            return moved.HasValue ? moved.Value : Commit(null, message, amend: true);
        }

        private GitResult? HeadMoved(string expected)
        {
            var heads = Run("rev-parse", "HEAD", $"{expected}^{{commit}}");
            var lines = heads.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            return heads.IsSuccess && lines.Length == 2 && lines[0] == lines[1]
                ? (GitResult?)null
                : GitResult.Failure($"The last commit is not {expected} any more, refresh and try again");
        }

        public List<string> OutgoingCommits(int limit = 50)
        {
            var result = OutgoingLog("--pretty=format:%h %s", limit, "HEAD");
            return !result.IsSuccess || string.IsNullOrWhiteSpace(result.Output)
                ? new List<string>()
                : result.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        private IEnumerable<string> OutgoingCommitHashes(string tip, int limit)
        {
            var result = OutgoingLog("--pretty=format:%h", limit, tip);
            return !result.IsSuccess || string.IsNullOrWhiteSpace(result.Output)
                ? Enumerable.Empty<string>()
                : result.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private GitResult OutgoingLog(string format, int limit, string tip) =>
            Run(new[] { "log" }.Concat(OutgoingRange(PushTarget(), tip))
                .Concat(new[] { format, "-n", limit.ToString(), "--" })
                .ToArray());

        private static IEnumerable<string> PathFilter(IEnumerable<string> paths) =>
            new[] { "--" }.Concat(paths.Select(path => ":(top,literal)" + path));

        private static IEnumerable<string> OutgoingRange(GitPushTarget? target, string tip)
        {
            if (!target.HasValue)
            {
                return new[] { tip, "--not", "--remotes" };
            }

            return target.Value.Exists
                ? new[] { $"{target.Value.TrackingRef}..{tip}" }
                : new[] { tip, "--not", $"--remotes={target.Value.Remote}" };
        }
    }
}
