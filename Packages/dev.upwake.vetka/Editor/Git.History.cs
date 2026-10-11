using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upwake.Vetka
{
    internal partial class Git
    {
        public (GitResult Result, List<GitFileChange> Files) CommitFiles(string commit)
        {
            var result = Run("show", "--diff-merges=first-parent", "--format=", "--name-status", "-z", "--no-renames",
                commit);
            return (result, result.IsSuccess ? FileChanges(result.Output) : new List<GitFileChange>());
        }

        public (GitResult Result, List<GitBlameLine> Lines) Blame(string path, string revision = null)
        {
            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return (top, new List<GitBlameLine>());
            }

            if (revision == null)
            {
                var tracked = NulSeparated(Run("ls-files", "-z", "--full-name", "--", ":(top,icase,literal)" + path)
                    .Output);
                if (tracked.Count == 1)
                {
                    path = tracked[0];
                }
            }

            var result = revision == null
                ? Run("blame", "--porcelain", "--", Path.Combine(top.Output, path))
                : Run("blame", "--porcelain", revision, "--", Path.Combine(top.Output, path));
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
            var commits =
                new Dictionary<string, (string Author, DateTime Date, string Summary, string FileName, string Previous,
                    string PreviousFileName)>();
            string hash = null;
            var number = 0;
            (string Author, DateTime Date, string Summary, string FileName, string Previous, string PreviousFileName) info =
                default;

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
                        line.Substring(1).Replace("\t", "    "), info.Previous, info.PreviousFileName));
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
                    case "previous":
                        var space = value.IndexOf(' ');
                        if (space > 0)
                        {
                            info.Previous = value.Substring(0, space);
                            info.PreviousFileName = value.Substring(space + 1);
                        }

                        break;
                }
            }

            return lines;
        }

        public GitResult CommitFileDiff(string commit, string path) =>
            Run("show", "--diff-merges=first-parent", "--format=", "--no-color", "--no-ext-diff", commit, "--",
                ":(top,literal)" + path);

        internal int CommitsPerRead { get; set; } = 200;

        public (GitResult Result, List<string> Commits, List<string> Paths, HashSet<string> Unpushed,
            Dictionary<string, HashSet<string>> Names) FileHistory(string head, string path, bool withMeta,
                bool followRenames = true)
        {
            var found = new List<(string Hash, long Time)>();
            var nameAt = new Dictionary<string, HashSet<string>>();
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
                    return (walk, new List<string>(), paths, unpushed, nameAt);
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
                        nameAt[record.Hash] = new HashSet<string>();
                    }

                    nameAt[record.Hash].Add(name);

                    if (followRenames && record.Changes.Contains(("A", name)) && examined.Add((record.Hash, name)))
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
            return (GitResult.Success(""), commits, paths.Distinct().ToList(), unpushed, nameAt);
        }

        private const int MaxRenamesFollowed = 100;

        private static IEnumerable<(string Hash, long Time, string[] Parents, List<(string Status, string Path)> Changes)>
            HistoryRecords(string output)
        {
            foreach (var record in output.Split(new[] { '\u001e' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = record.Split('\0');
                var header = fields[0].Trim('\n', '\r').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var changes = NameStatus(fields, 1).Select(entry => (entry.Status, entry.Path)).ToList();
                yield return (header[0], header.Length > 1 && long.TryParse(header[1], out var time) ? time : 0,
                    header.Skip(2).ToArray(), changes);
            }
        }

        private string RenameSource(string parent, string commit, string name)
        {
            var sources = ChangedPaths("--diff-filter=D", parent, commit);
            if (sources == null || sources.Count == 0)
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

        internal int PathCharactersPerRead { get; set; } = 20000;

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
                foreach (var entry in NameStatus(renames.Output.Split('\0')))
                {
                    if (entry.Status[0] == 'R' && entry.OldPath != null && entry.Path == path &&
                        (found.Old == null || RenameScore(entry.Status) > RenameScore(found.Status)))
                    {
                        found = (entry.Status, entry.OldPath);
                    }
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

        internal static string FileName(string path) => path.Substring(path.LastIndexOf('/') + 1);

        private IEnumerable<List<string>> PathParts(IEnumerable<string> paths)
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

        public (GitResult Result, List<GitCommit> Commits) ReadCommits(IReadOnlyList<string> hashes,
            ISet<string> unpushed)
        {
            var result = GitResult.Success("");
            var commits = new List<GitCommit>();
            for (var start = 0; start < hashes.Count; start += CommitsPerRead)
            {
                result = Run(new[] { "log", "--no-walk=unsorted" }
                    .Concat(CommitFormat)
                    .Concat(hashes.Skip(start).Take(CommitsPerRead))
                    .Append("--")
                    .ToArray());
                if (!result.IsSuccess)
                {
                    return (result, new List<GitCommit>());
                }

                commits.AddRange(ParseCommits(result.Output, unpushed));
            }

            return (result, commits);
        }

        public string RemoteRefs()
        {
            var refs = Run("for-each-ref", "--format=%(objectname) %(refname)", "refs/remotes");
            return refs.IsSuccess ? refs.Output : null;
        }

        public (GitResult Result, List<GitCommit> Commits, string Head) ReadLogPage(string head, int skip, int count) =>
            ReadLogPage(head, skip, count, PushTarget());

        public (GitResult Result, List<GitCommit> Commits, string Head) ReadLogPage(string head, int skip, int count,
            GitPushTarget? target)
        {
            var none = new List<GitCommit>();
            if (head == null)
            {
                head = HeadCommit();
                if (head == null)
                {
                    var repository = Run("rev-parse", "--git-dir");
                    return (repository.IsSuccess ? GitResult.Success("") : repository, none, null);
                }
            }

            var result = Run(new[] { "log", "--skip=" + skip, "-n", count.ToString() }
                .Concat(CommitFormat)
                .Concat(new[] { head, "--" })
                .ToArray());
            if (!result.IsSuccess)
            {
                return (result, none, head);
            }

            var outgoing = new HashSet<string>(OutgoingCommitHashes(target, head, skip + count));
            return (result, ParseCommits(result.Output, outgoing), head);
        }

        public GitPushTarget? PushTargetOf(string branchRef)
        {
            if (branchRef == null)
            {
                return PushTarget();
            }

            return branchRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? PushTarget(branchRef.Substring("refs/heads/".Length), out _)
                : null;
        }

        private static readonly string[] CommitFormat =
        {
            "--pretty=format:%H%x1f%h%x1f%an%x1f%ad%x1f%D%x1f%s", "--decorate=short", "--decorate-refs=refs/tags/",
            "--date=short"
        };

        private static List<GitCommit> ParseCommits(string output, ICollection<string> unpushed) =>
            Lines(output)
                .Select(line => line.Split(new[] { LogFieldSeparator }, 6, StringSplitOptions.None))
                .Where(fields => fields.Length == 6)
                .Select(fields => new GitCommit(fields[1], fields[2], fields[3], fields[5],
                    !unpushed.Contains(fields[0]), TagNames(fields[4])))
                .ToList();

        public (GitResult Result, int Count, List<GitCommit> Commits) CommitsOnlyIn(string include, string exclude,
            int limit)
        {
            var range = exclude != null ? new[] { include, "--not", exclude, "--" } : new[] { include, "--" };
            var count = Run(new[] { "rev-list", "--count" }.Concat(range).ToArray());
            if (!count.IsSuccess)
            {
                return (count, 0, new List<GitCommit>());
            }

            var log = Run(new[] { "log", "-n", limit.ToString() }
                .Concat(CommitFormat)
                .Concat(range)
                .ToArray());
            return log.IsSuccess
                ? (log, int.TryParse(count.Output, out var total) ? total : 0,
                    ParseCommits(log.Output, Array.Empty<string>()))
                : (log, 0, new List<GitCommit>());
        }

        public GitComparison CompareWithWorkingTree(string reference, string name, int limit)
        {
            var comparison = new GitComparison { Current = CurrentBranch() ?? "HEAD", Tip = CommitOf(reference) };
            if (comparison.Tip == null)
            {
                comparison.Result = GitResult.Failure($"{name} cannot be found");
                return comparison;
            }

            var head = HeadCommit();
            var onlyHere = CommitsOnlyIn(comparison.Tip, head, limit);
            var onlyCurrent = head != null
                ? CommitsOnlyIn(head, comparison.Tip, limit)
                : (Result: GitResult.Success(""), Count: 0, Commits: new List<GitCommit>());
            var files = DifferencesFromWorkingTree(comparison.Tip);
            comparison.Result = !onlyHere.Result.IsSuccess ? onlyHere.Result
                : !onlyCurrent.Result.IsSuccess ? onlyCurrent.Result
                : files.Result;
            comparison.OnlyHereCount = onlyHere.Count;
            comparison.OnlyHere = onlyHere.Commits;
            comparison.OnlyCurrentCount = onlyCurrent.Count;
            comparison.OnlyCurrent = onlyCurrent.Commits;
            comparison.Files = files.Files;
            return comparison;
        }

        public (GitResult Result, List<GitFileChange> Files) DifferencesFromWorkingTree(string revision)
        {
            var diff = Run("diff", "--name-status", "-z", "--find-renames", revision, "--");
            return (diff, diff.IsSuccess ? FileChanges(diff.Output) : new List<GitFileChange>());
        }

        private static List<string> TagNames(string decoration) =>
            decoration.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)
                .Where(name => name.StartsWith("tag: "))
                .Select(name => name.Substring("tag: ".Length))
                .ToList();

        public string LastCommitMessage()
        {
            var result = Run("log", "-1", "--pretty=format:%B");
            return result.IsSuccess ? result.Output : null;
        }

        public bool IsHeadPushed()
        {
            var unpushed = OutgoingLog("--format=%h", 1, "HEAD", PushTarget());
            return unpushed.IsSuccess && unpushed.Output.Length == 0;
        }

        public HashSet<string> CommitsMissingFromHead(string tip, int limit)
        {
            var log = Run(new[] { "log", "--format=%h", "-n", limit.ToString(), tip }
                .Concat(HasHead() ? new[] { "--not", "HEAD" } : Array.Empty<string>())
                .Append("--")
                .ToArray());
            return new HashSet<string>(log.IsSuccess ? Lines(log.Output) : Enumerable.Empty<string>());
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
            var lines = Lines(heads.Output);
            return heads.IsSuccess && lines.Count == 2 && lines[0] == lines[1]
                ? (GitResult?)null
                : GitResult.Failure($"The last commit is not {expected} any more, refresh and try again");
        }

        public (GitResult Result, int Count, List<string> Commits) OutgoingCommits(GitPushTarget? target, int limit = 50)
        {
            var log = OutgoingLog("--pretty=format:%h %s", limit, "HEAD", target);
            if (!log.IsSuccess)
            {
                return (HasHead() ? log : GitResult.Success(""), 0, new List<string>());
            }

            var commits = Lines(log.Output);
            var count = commits.Count < limit ? commits.Count
                : CountCommits(OutgoingRange(target, "HEAD").ToArray()) ?? commits.Count;
            return (log, count, commits);
        }

        private IEnumerable<string> OutgoingCommitHashes(GitPushTarget? target, string tip, int limit)
        {
            var result = OutgoingLog("--pretty=format:%H", limit, tip, target);
            return !result.IsSuccess || string.IsNullOrWhiteSpace(result.Output)
                ? Enumerable.Empty<string>()
                : Lines(result.Output);
        }

        private GitResult OutgoingLog(string format, int limit, string tip, GitPushTarget? target) =>
            Run(new[] { "log" }.Concat(OutgoingRange(target, tip))
                .Concat(new[] { format, "-n", limit.ToString(), "--" })
                .ToArray());

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
