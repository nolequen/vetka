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

        public (GitResult Result, List<string> Paths) HistoryPaths(string path, bool withMeta)
        {
            var names = Run("log", "--follow", "--name-only", "-z", "--format=", "--", ":(top,literal)" + path);
            if (!names.IsSuccess)
            {
                return (names, new List<string>());
            }

            var paths = new[] { path }
                .Concat(NulSeparated(names.Output).Select(name => name.Trim('\n', '\r')))
                .Where(name => name.Length > 0)
                .Distinct()
                .ToList();
            return (names, withMeta ? paths.SelectMany(name => new[] { name, name + ".meta" }).Distinct().ToList() : paths);
        }

        public (GitResult Result, List<GitCommit> Commits, string Head) ReadLogPage(string head, int skip, int count,
            IReadOnlyCollection<string> paths = null)
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

            var result = Run(new[]
                {
                    "log", "--skip=" + skip, "-n", count.ToString(), "--pretty=format:%h%x1f%an%x1f%ad%x1f%s",
                    "--date=short", head
                }
                .Concat(PathFilter(paths))
                .ToArray());
            if (!result.IsSuccess)
            {
                return (result, none, head);
            }

            var outgoing = new HashSet<string>(OutgoingCommitHashes(head, skip + count, paths));

            return (result, result.Output
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(new[] { LogFieldSeparator }, StringSplitOptions.None))
                .Where(fields => fields.Length == 4)
                .Select(fields => new GitCommit(fields[0], fields[1], fields[2], fields[3],
                    !outgoing.Contains(fields[0])))
                .ToList(), head);
        }

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

        private IEnumerable<string> OutgoingCommitHashes(string tip, int limit, IReadOnlyCollection<string> paths)
        {
            var result = OutgoingLog("--pretty=format:%h", limit, tip, paths);
            return !result.IsSuccess || string.IsNullOrWhiteSpace(result.Output)
                ? Enumerable.Empty<string>()
                : result.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private GitResult OutgoingLog(string format, int limit, string tip, IReadOnlyCollection<string> paths = null) =>
            Run(new[] { "log" }.Concat(OutgoingRange(PushTarget(), tip))
                .Concat(new[] { format, "-n", limit.ToString() })
                .Concat(PathFilter(paths))
                .ToArray());

        private static IEnumerable<string> PathFilter(IReadOnlyCollection<string> paths) =>
            new[] { "--" }.Concat(paths?.Select(path => ":(top,literal)" + path) ?? Enumerable.Empty<string>());

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
