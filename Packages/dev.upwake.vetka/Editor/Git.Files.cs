using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Upwake.Vetka
{
    internal partial class Git
    {
        public GitResult Commit(IEnumerable<string> files, string message, bool amend = false)
        {
            var paths = Selected(files);
            if (paths.Count == 0 && !amend)
            {
                return GitResult.Failure("No files selected");
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return GitResult.Failure("Commit message is empty");
            }

            var refusal = amend
                ? RefuseDuringOperation("Amending the last commit")
                : RefuseDuringOperation("Committing", "a revert");
            if (refusal.HasValue)
            {
                return refusal.Value;
            }

            string messageFile = null;
            List<string> markedNew = null;
            try
            {
                messageFile = WriteTempFile(message);

                var arguments = new List<string> { "commit" };
                if (amend)
                {
                    arguments.Add("--amend");
                }

                arguments.Add("--only");
                arguments.Add("--cleanup=whitespace");
                arguments.Add("--file");
                arguments.Add(messageFile);

                if (paths.Count > 0)
                {
                    var targets = CommitTargets(paths);
                    if (!targets.Result.IsSuccess)
                    {
                        return targets.Result;
                    }

                    var unresolved = Unresolved(targets.Unmerged);
                    if (unresolved.Count > 0)
                    {
                        return GitResult.Failure(FileList(
                            "Committing is not possible, these files still have conflicts", unresolved));
                    }

                    if (!amend && targets.KeptOnDisk.Count > 0)
                    {
                        return CommitWithTemporaryIndex(paths, targets.KeptOnDisk, targets.Unmerged, messageFile,
                            message);
                    }

                    if (targets.Untracked.Count > 0)
                    {
                        var intentToAdd = RunWithPathspecs(null, targets.Untracked, "add", "--intent-to-add");
                        if (!intentToAdd.IsSuccess)
                        {
                            return intentToAdd;
                        }

                        markedNew = targets.Untracked;
                    }
                }

                var commit = paths.Count > 0
                    ? RunWithPathspecs(null, paths, arguments.ToArray())
                    : Run(arguments.ToArray());
                return commit.IsSuccess
                    ? GitResult.Success(CommitSummary(message, amend))
                    : WithNewFilesUnmarked(commit, markedNew);
            }
            catch (Exception exception)
            {
                return WithNewFilesUnmarked(GitResult.Failure($"Commit failed: {exception.Message}"), markedNew);
            }
            finally
            {
                DeleteTempFile(messageFile);
            }
        }

        internal static List<string> WithOldPaths(IEnumerable<GitFileChange> changes) =>
            changes.SelectMany(change => string.IsNullOrEmpty(change.OldPath)
                    ? new[] { change.Path }
                    : new[] { change.Path, change.OldPath })
                .Distinct()
                .ToList();

        private static List<string> Selected(IEnumerable<string> files) =>
            (files ?? Enumerable.Empty<string>()).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct().ToList();

        public GitResult CommitAndPush(IEnumerable<string> files, string message, out bool committed,
            PushTags? tags = null)
        {
            var commit = Commit(files, message);
            committed = commit.IsDone;
            if (!commit.IsSuccess)
            {
                return committed ? GitResult.Done($"{commit.Message}\nNot pushed") : commit;
            }

            var destination = PushDestination();
            var pushedTags = 0;
            var push = destination.Target.HasValue
                ? PushTo(destination.Target.Value, null, null, tags, out pushedTags)
                : GitResult.Failure(destination.Problem);
            if (!push.IsDone)
            {
                return NotPushed(CommitSummary(message, false), push);
            }

            var pushedTo = destination.Target?.Name ?? "the remote";
            var summary = CommitSummary(message, false,
                pushedTags > 0 ? $"{pushedTo} with {Texts.Counted(pushedTags, "tag")}" : pushedTo);
            return push.IsSuccess ? GitResult.Success(summary) : GitResult.Done($"{summary}\n{push.Message}");
        }

        private string CommitSummary(string message, bool amend, string pushedTo = null)
        {
            var subject = Subject(message);
            if (amend)
            {
                return $"Commit amended: {subject}";
            }

            var files = Run("show", "--format=", "--name-only", "-z", "--find-renames", "HEAD");
            if (!files.IsSuccess)
            {
                return $"Committed: {subject}";
            }

            var count = files.Output.Split('\0').Count(path => path.Trim('\n', '\r').Length > 0);
            var done = pushedTo == null ? "committed" : $"committed and pushed to {pushedTo}";
            return $"{Texts.Counted(count, "file")} {done}: {subject}";
        }

        private (GitResult Result, List<string> Untracked, List<string> KeptOnDisk, List<string> Unmerged) CommitTargets(
            List<string> paths)
        {
            var none = new List<string>();
            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return (top, none, none, none);
            }

            var root = top.Output;
            var existing = paths.Where(path => File.Exists(Path.Combine(root, path))).ToList();
            if (existing.Count == 0)
            {
                return (GitResult.Success(string.Empty), none, none, none);
            }

            var status = Run(PlainStatus);
            if (!status.IsSuccess)
            {
                return (status, none, none, none);
            }

            var entries = StatusEntries(status.Output).ToList();
            var removed = new HashSet<string>(entries
                .Where(entry => entry.Index == 'D' && entry.WorkTree == ' ')
                .Select(entry => entry.Path));
            var untracked = new HashSet<string>(entries.Where(entry => entry.Index == '?').Select(entry => entry.Path));
            var unmerged = new HashSet<string>(entries
                .Where(entry => ParseStatus(entry.Index, entry.WorkTree) == GitStatus.Unmerged)
                .Select(entry => entry.Path));
            return (status,
                existing.Where(path => untracked.Contains(path) && !removed.Contains(path)).ToList(),
                existing.Where(removed.Contains).ToList(),
                paths.Where(unmerged.Contains).ToList());
        }

        private List<string> Unresolved(List<string> unmerged)
        {
            if (unmerged.Count == 0)
            {
                return unmerged;
            }

            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return unmerged;
            }

            var root = top.Output;
            return unmerged.Where(path => HasConflictMarkers(Path.Combine(root, path))).ToList();
        }

        private static bool HasConflictMarkers(string file)
        {
            if (!File.Exists(file))
            {
                return false;
            }

            try
            {
                using var stream = File.OpenRead(file);
                var head = new byte[8000];
                var read = stream.Read(head, 0, head.Length);
                if (Array.IndexOf(head, (byte)0, 0, read) >= 0)
                {
                    return true;
                }

                stream.Position = 0;
                using var reader = new StreamReader(stream, Utf8, true);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("<<<<<<<") || line.StartsWith(">>>>>>>"))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private GitResult CommitWithTemporaryIndex(List<string> paths, List<string> keptOnDisk, List<string> unmerged,
            string messageFile, string message)
        {
            if (unmerged.Count > 0)
            {
                return GitResult.Failure(FileList("Committing is not possible, these files have conflicts", unmerged));
            }

            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return top;
            }

            var root = top.Output;
            var kept = new HashSet<string>(keptOnDisk);
            var present = new HashSet<string>(paths.Where(path => !kept.Contains(path) &&
                                                                  (File.Exists(Path.Combine(root, path)) ||
                                                                   Directory.Exists(Path.Combine(root, path)))));
            var indexFile = Path.Combine(Path.GetTempPath(), $"Vetka-{Guid.NewGuid():N}.index");
            var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = indexFile };
            try
            {
                var prepared = RunWithEnvironment(environment, "read-tree", "HEAD");
                if (prepared.IsSuccess)
                {
                    prepared = RunWithPathspecs(environment, present.ToList(), "add");
                }

                if (prepared.IsSuccess)
                {
                    prepared = RunWithPathspecs(environment, paths.Where(path => !present.Contains(path)).ToList(),
                        "-C", top.Output, "rm", "--cached", "-q", "--ignore-unmatch");
                }

                if (!prepared.IsSuccess)
                {
                    return prepared;
                }

                var commit = RunWithEnvironment(environment, "commit", "--cleanup=whitespace", "--file", messageFile);
                if (!commit.IsSuccess)
                {
                    return commit;
                }

                var summary = CommitSummary(message, false);
                var synced = RunWithPathspecs(null, paths, "reset", "-q", "HEAD");
                return synced.IsSuccess
                    ? GitResult.Success(summary)
                    : GitResult.Done($"{summary}, but the staged changes could not be updated\n{synced.Message}");
            }
            finally
            {
                DeleteTempFile(indexFile);
            }
        }

        private GitResult WithNewFilesUnmarked(GitResult failure, List<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return failure;
            }

            var top = TopLevel();
            var result = top.IsSuccess
                ? RunWithPathspecs(null, paths, "-C", top.Output, "rm", "--cached", "-q", "--ignore-unmatch")
                : top;
            return result.IsSuccess
                ? failure
                : GitResult.Failure($"{failure.Message}\n" +
                                    $"{FileList("These new files are still marked as added and could not be returned to unversioned", paths)}\n" +
                                    result.Message);
        }

        public GitResult Rollback(IEnumerable<GitFileChange> changes, bool deleteAdded, int? shownCount = null)
        {
            var selected = (changes ?? Enumerable.Empty<GitFileChange>())
                .Where(change => !string.IsNullOrWhiteSpace(change.Path))
                .ToList();

            if (selected.Count == 0)
            {
                return GitResult.Failure("No files selected");
            }

            var paths = WithOldPaths(selected);
            var delete = new HashSet<string>(selected
                .Where(change => deleteAdded || !string.IsNullOrEmpty(change.OldPath))
                .Select(change => change.Path));

            var status = Run(PlainStatus);
            if (!status.IsSuccess)
            {
                return status;
            }

            var entries = StatusEntries(status.Output).ToList();
            var tracked = new HashSet<string>(entries.Where(entry => entry.Index != '?').Select(entry => entry.Path));
            var unmerged = new HashSet<string>(entries
                .Where(entry => ParseStatus(entry.Index, entry.WorkTree) == GitStatus.Unmerged)
                .Select(entry => entry.Path));
            var added = new HashSet<string>(entries
                .Where(entry => (entry.Index == 'A' || entry.WorkTree == 'A') && !unmerged.Contains(entry.Path))
                .Select(entry => entry.Path));
            var untracked = new HashSet<string>(entries
                .Where(entry => entry.Index == '?' && !tracked.Contains(entry.Path))
                .Select(entry => entry.Path));

            var unstage = paths.Where(path => added.Contains(path) && !delete.Contains(path)).ToList();
            var conflicts = paths.Where(unmerged.Contains).ToList();
            var restore = paths.Where(path => !untracked.Contains(path) && !unstage.Contains(path) &&
                                              !unmerged.Contains(path)).ToList();
            var remove = paths.Where(path => untracked.Contains(path) && delete.Contains(path)).ToList();
            var rolledBack =
                $"{Texts.Counted(shownCount ?? selected.Select(change => change.Path).Distinct().Count(), "file")} rolled back";

            try
            {
                if (!HasHead())
                {
                    var top = TopLevel();
                    var unstaged = top.IsSuccess
                        ? RunWithPathspecs(null, paths.Where(tracked.Contains).ToList(),
                            "-C", top.Output, "rm", "--cached", "-f", "-q", "--ignore-unmatch")
                        : top;
                    if (!unstaged.IsSuccess)
                    {
                        return unstaged;
                    }

                    DeleteCreatedFiles(paths.Where(delete.Contains).ToList());
                    return GitResult.Success(rolledBack);
                }

                var failed = RestoreFromHead(unstage, "--staged") ??
                             RestoreFromHead(restore, "--staged", "--worktree") ??
                             ResetToHead(conflicts, delete.Contains);
                if (failed.HasValue)
                {
                    return failed.Value;
                }

                DeleteCreatedFiles(remove);
                return GitResult.Success(rolledBack);
            }
            catch (Exception exception)
            {
                return GitResult.Failure($"Rollback failed: {exception.Message}");
            }
        }

        private bool HasHead() => Run("rev-parse", "-q", "--verify", "HEAD").IsSuccess;

        private GitResult? RestoreFromHead(List<string> paths, params string[] targets) =>
            Restore("HEAD", paths, targets);

        private GitResult? Restore(string source, List<string> paths, params string[] targets)
        {
            var result = RunWithPathspecs(null, paths, new[] { "restore", "--source=" + source }.Concat(targets).ToArray());
            return result.IsSuccess || Matches(source, paths, targets) ? (GitResult?)null : result;
        }

        private bool Matches(string source, List<string> paths, string[] targets)
        {
            var wanted = new HashSet<string>(paths, StringComparer.Ordinal);
            var checks = new List<string[]>();
            if (targets.Contains("--staged"))
            {
                checks.Add(new[] { "--cached", source });
            }

            if (targets.Contains("--worktree"))
            {
                checks.Add(new[] { source });
            }

            foreach (var check in checks)
            {
                var changed = ChangedPaths(check);
                if (changed == null || changed.Any(wanted.Contains))
                {
                    return false;
                }
            }

            return checks.Count > 0;
        }

        private GitResult? ResetToHead(List<string> paths, Func<string, bool> deleteCreated)
        {
            if (paths.Count == 0)
            {
                return null;
            }

            var reset = RunWithPathspecs(null, paths, "reset", "-q", "HEAD");
            if (!reset.IsSuccess)
            {
                return reset;
            }

            var now = ReadTree();
            if (now == null)
            {
                return GitResult.Failure("The changed files cannot be read");
            }

            var restored = RestoreFromHead(paths.Where(now.Changed.Contains).ToList(), "--staged", "--worktree");
            if (restored.HasValue)
            {
                return restored;
            }

            DeleteCreatedFiles(paths.Where(path => now.Untracked.Contains(path) && deleteCreated(path)).ToList());
            return null;
        }

        public GitResult Add(IEnumerable<string> files)
        {
            var paths = Selected(files);
            return paths.Count == 0 ? GitResult.Failure("No files selected") : RunWithPathspecs(null, paths, "add");
        }

        public GitResult CreatePatch(IEnumerable<string> files, string patchPath)
        {
            var paths = Selected(files);
            if (paths.Count == 0)
            {
                return GitResult.Failure("No files selected");
            }

            var indexFile = Path.Combine(Path.GetTempPath(), $"Vetka-{Guid.NewGuid():N}.index");
            var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = indexFile };
            try
            {
                var hasHead = HasHead();
                var readTree = RunWithEnvironment(environment, "read-tree", hasHead ? "HEAD" : "--empty");
                if (!readTree.IsSuccess)
                {
                    return readTree;
                }

                var add = RunWithPathspecs(environment, paths, "add");
                if (!add.IsSuccess)
                {
                    return add;
                }

                var arguments = new List<string> { "diff", "--cached", "--binary", "--no-color", "--no-ext-diff", "--no-textconv" };
                if (hasHead)
                {
                    arguments.Add("HEAD");
                }

                arguments.Add("--output=" + patchPath);
                var diff = RunWithEnvironment(environment, arguments.ToArray());
                if (diff.IsCancelled)
                {
                    DeleteTempFile(patchPath);
                }

                return diff.IsSuccess ? GitResult.Success($"Patch saved to {patchPath}") : diff;
            }
            catch (Exception exception)
            {
                return GitResult.Failure($"Patch not created: {exception.Message}");
            }
            finally
            {
                DeleteTempFile(indexFile);
            }
        }

        public GitResult FileDiff(string path, string oldPath = null)
        {
            if (!string.IsNullOrEmpty(oldPath))
            {
                return CompareWithLocal("HEAD", oldPath, path);
            }

            if (HasHead())
            {
                var tracked = Run("diff", "HEAD", "--no-color", "--no-ext-diff", "--", ":(top,literal)" + path);
                if (!tracked.IsSuccess || tracked.Output.Length > 0 ||
                    Run("ls-files", "--error-unmatch", "--", ":(top,literal)" + path).IsSuccess)
                {
                    return tracked;
                }
            }

            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return top;
            }

            var diff = Run("-C", top.Output, "diff", "--no-index", "--no-color", "--no-ext-diff", "--",
                "/dev/null", path);
            return diff.ExitCode == 1 && diff.Output.Length > 0 ? new GitResult(0, diff.Output, diff.Error) : diff;
        }

        public GitResult CompareWithLocal(string commit, string pathAtCommit, string localPath)
        {
            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return top;
            }

            var moved = pathAtCommit != localPath;
            var paths = moved ? new[] { pathAtCommit, localPath } : new[] { localPath };
            var arguments = new[] { "diff", commit, "--find-renames", "--no-color", "--no-ext-diff" }
                .Concat(PathFilter(paths))
                .ToArray();
            var exists = File.Exists(Path.Combine(top.Output, localPath));
            if (!exists && !moved)
            {
                return Run(arguments);
            }

            var diff = WithTemporaryIndex(environment =>
            {
                if (moved && !RunWithEnvironment(environment, "-C", top.Output, "update-index", "--force-remove", "--",
                        pathAtCommit).IsSuccess)
                {
                    return null;
                }

                return exists && !RunWithPathspecs(environment, new[] { localPath }, "add", "-f", "--intent-to-add")
                    .IsSuccess
                    ? null
                    : (GitResult?)RunWithEnvironment(environment, arguments);
            });
            return diff ?? GitResult.Failure($"Cannot compare {localPath} with its version in {commit}");
        }

        public GitResult GetVersion(string commit, string pathAtCommit, string localPath)
        {
            var entry = Run("ls-tree", "-z", "--full-name", commit, "--", ":(top,literal)" + pathAtCommit);
            var fields = entry.IsSuccess
                ? entry.Output.Split('\0')
                    .FirstOrDefault(line => line.EndsWith("\t" + pathAtCommit, StringComparison.Ordinal))
                    ?.Split(new[] { ' ', '\t' }, 4)
                : null;
            if (fields == null || fields.Length < 4 || fields[1] != "blob")
            {
                return GitResult.Failure($"{pathAtCommit} is not a file in {commit}");
            }

            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return top;
            }

            var root = top.Output;
            if (Directory.Exists(Path.Combine(root, localPath)))
            {
                return GitResult.Failure($"Cannot write {localPath}: a folder with that name is in the way");
            }

            for (var slash = localPath.IndexOf('/'); slash > 0; slash = localPath.IndexOf('/', slash + 1))
            {
                var parent = localPath.Substring(0, slash);
                if (File.Exists(Path.Combine(root, parent)))
                {
                    return GitResult.Failure($"Cannot write {localPath}: {parent} is a file");
                }
            }

            var written = WithTemporaryIndex(environment =>
            {
                var added = RunWithEnvironment(environment, "-C", top.Output, "update-index", "--add", "--cacheinfo",
                    $"{fields[0]},{fields[2]},{localPath}");
                return (GitResult?)(added.IsSuccess
                    ? RunWithEnvironment(environment, "-C", top.Output, "checkout-index", "-f", "--", localPath)
                    : added);
            });
            if (!written.HasValue)
            {
                return GitResult.Failure($"Cannot write the version of {localPath} from {commit}");
            }

            return written.Value.IsSuccess
                ? GitResult.Success($"{localPath} now has its content from {commit}")
                : written.Value;
        }

        public GitResult ApplyPatch(string patchPath)
        {
            var name = Path.GetFileName(patchPath);
            var unchanged = $"Patch {name} not applied, the project is unchanged";
            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return top;
            }

            var stats = Run("-C", top.Output, "apply", "--numstat", "-z", patchPath);
            if (!stats.IsSuccess)
            {
                return GitResult.Failure($"{unchanged}\n{stats.Message}");
            }

            using var backup = new FileBackup(top.Output);
            var saved = backup.Save(PatchedPaths(stats.Output, patchPath));
            if (saved != null)
            {
                return GitResult.Failure($"{unchanged}\n{saved}");
            }

            var apply = Run("-C", top.Output, "apply", patchPath);
            if (apply.IsSuccess)
            {
                return GitResult.Success($"Patch {name} applied");
            }

            var lost = backup.Restore();
            return lost.Count == 0
                ? GitResult.Failure($"{unchanged}\n{apply.Message}")
                : GitResult.Failure($"{FileList($"Patch {name} not applied, these files could not be put back", lost)}\n" +
                                    apply.Message);
        }

        internal static List<string> PatchedPaths(string numstat, string patchPath)
        {
            var paths = new List<string>();
            foreach (var entry in numstat.Split('\0'))
            {
                var fields = entry.Split(new[] { '\t' }, 3);
                if (fields.Length == 3 && fields[2].Length > 0)
                {
                    paths.Add(fields[2]);
                }
            }

            const string renameFrom = "rename from ";
            foreach (var line in File.ReadLines(patchPath, Utf8))
            {
                if (line.StartsWith(renameFrom))
                {
                    paths.Add(Unquoted(line.Substring(renameFrom.Length).TrimEnd('\r')));
                }
            }

            return paths.Distinct().ToList();
        }

        private static string Unquoted(string path)
        {
            if (path.Length < 2 || path[0] != '"' || path[path.Length - 1] != '"')
            {
                return path;
            }

            var bytes = new List<byte>();
            for (var i = 1; i < path.Length - 1; i++)
            {
                var c = path[i];
                if (c != '\\' || i + 1 >= path.Length - 1)
                {
                    var length = char.IsHighSurrogate(c) && i + 1 < path.Length - 1 ? 2 : 1;
                    bytes.AddRange(Utf8.GetBytes(path.Substring(i, length)));
                    i += length - 1;
                    continue;
                }

                var next = path[++i];
                if (next >= '0' && next <= '7' && i + 2 < path.Length - 1)
                {
                    bytes.Add(Convert.ToByte(path.Substring(i, 3), 8));
                    i += 2;
                    continue;
                }

                int value = next switch
                {
                    'a' => 7,
                    'b' => 8,
                    't' => 9,
                    'n' => 10,
                    'v' => 11,
                    'f' => 12,
                    'r' => 13,
                    _ => next
                };
                bytes.Add((byte)value);
            }

            return Utf8.GetString(bytes.ToArray());
        }

        private void DeleteCreatedFiles(List<string> paths)
        {
            if (paths.Count == 0)
            {
                return;
            }

            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return;
            }

            var root = top.Output;
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var file = Path.GetFullPath(Path.Combine(root, path));
                try
                {
                    File.Delete(file);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Vetka: cannot delete {file}: {exception.Message}");
                }

                for (var directory = Path.GetDirectoryName(file);
                     !string.IsNullOrEmpty(directory) && directory.Length > root.Length;
                     directory = Path.GetDirectoryName(directory))
                {
                    directories.Add(directory);
                }
            }

            FileBackup.DeleteEmptyFolders(directories, true);
        }
    }
}
