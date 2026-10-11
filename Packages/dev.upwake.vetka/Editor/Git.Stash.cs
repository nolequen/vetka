using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System;
using UnityEngine;

namespace Upwake.Vetka
{
    internal partial class Git
    {
        public List<GitStash> Stashes() => StashList().Stashes;

        public (GitResult Result, List<GitStash> Stashes) StashList()
        {
            var result = Run("stash", "list", "--format=%gd%x1f%ci%x1f%gs%x1f%H");
            if (!result.IsSuccess)
            {
                return (result, new List<GitStash>());
            }

            return (result, Lines(result.Output)
                .Select(line => line.Split(new[] { LogFieldSeparator }, StringSplitOptions.None))
                .Where(fields => fields.Length == 4)
                .Select(fields => new GitStash(fields[0],
                    fields[1].Length > 16 ? fields[1].Substring(0, 16) : fields[1], fields[2], fields[3]))
                .ToList());
        }

        private string StashReference(string hash) => Stashes().FirstOrDefault(current => current.Hash == hash).Reference;

        private static GitResult StashGone(GitStash stash, string action) =>
            GitResult.Failure($"\"{stash.Message}\" is no longer in the stash list, nothing was {action}");

        public GitResult Stash(string message, bool includeUntracked)
        {
            var refusal = RefuseDuringOperation("Stashing");
            if (refusal.HasValue)
            {
                return refusal.Value;
            }

            var text = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
            var arguments = new List<string> { "stash", "push" };
            if (includeUntracked)
            {
                arguments.Add("--include-untracked");
            }

            if (text != null)
            {
                arguments.Add("-m");
                arguments.Add(text);
            }

            var before = StashHead();
            var stash = Run(arguments.ToArray());
            var pushed = DateTime.UtcNow;
            var own = OwnStash(before, text, HeadCommit());
            if (!stash.IsSuccess)
            {
                return StashNotMade(stash, own, includeUntracked, pushed, "Changes not stashed");
            }

            if (own == null)
            {
                return GitResult.Success("No local changes to stash");
            }

            var done = text == null ? "Changes stashed" : $"Changes stashed: {Subject(text)}";
            var now = ReadTree();
            var stuck = now == null || now.Changed.Count == 0 ? new List<string>() : Stuck(own, now.Changed, pushed);
            return stuck.Count == 0
                ? GitResult.Success(done)
                : GitResult.Done($"{done}\n{FileList("These files stay modified after stashing", stuck)}\n{ModifiedHint}");
        }

        public GitResult ApplyStash(GitStash stash, bool drop)
        {
            var reference = StashReference(stash.Hash);
            if (reference == null)
            {
                return StashGone(stash, "applied");
            }

            var refusal = RefuseDuringOperation($"Applying {reference}");
            if (refusal.HasValue)
            {
                return refusal.Value;
            }

            var snapshot = StashCreate();
            if (!snapshot.IsSuccess)
            {
                return snapshot;
            }

            var before = ReadTree();
            var top = TopLevel();
            if (before == null || !top.IsSuccess)
            {
                return GitResult.Failure($"Applying {reference} is not possible, the local changes cannot be read");
            }

            var root = top.Output;
            var created = StashUntrackedFiles(stash.Hash)
                .Where(path => !File.Exists(Path.Combine(root, path)))
                .ToList();

            var withoutStaging = HasStagedChanges() && HasStagedPart(stash.Hash);
            var apply = withoutStaging
                ? Run("stash", "apply", stash.Hash)
                : Run("stash", "apply", "--index", stash.Hash);
            if (!apply.IsSuccess && !withoutStaging && Untouched(snapshot.Output, created, root))
            {
                apply = Run("stash", "apply", stash.Hash);
                withoutStaging = true;
            }

            if (!apply.IsSuccess)
            {
                return Untouched(snapshot.Output, created, root)
                    ? GitResult.Failure($"{reference} not applied, the project and the stash are unchanged\n" +
                                        apply.Message)
                    : RollbackStash(reference, stash.Hash, stash.Message, apply, Conflicts(), snapshot.Output, created,
                        before);
            }

            if (withoutStaging)
            {
                return drop
                    ? GitResult.Done($"{reference} applied without the staging and not dropped\n{StagingKept}")
                    : GitResult.Success($"{reference} applied without the staging");
            }

            if (!drop)
            {
                return GitResult.Success($"{reference} applied");
            }

            var dropResult = DropStashEntry(stash.Hash);
            return dropResult.IsSuccess
                ? GitResult.Success($"{reference} applied and dropped")
                : GitResult.Done($"{reference} applied, but not dropped\n{dropResult.Message}");
        }

        private GitResult StashCreate(params string[] message)
        {
            var arguments = new[] { "stash", "create" }.Concat(message).ToArray();
            var created = Run(arguments);
            if (created.ExitCode != 1 || created.Output.Length > 0 || created.Error.Length > 0)
            {
                return created;
            }

            created = Run(arguments);
            return created.ExitCode == 1 && created.Output.Length == 0 && created.Error.Length == 0
                ? GitResult.Success("")
                : created;
        }

        private bool HasStagedChanges() => Run("diff", "--cached", "--quiet").ExitCode == 1;

        private bool HasStagedPart(string stash)
        {
            var trees = Run("rev-parse", $"{stash}^1^{{tree}}", $"{stash}^2^{{tree}}");
            var lines = Lines(trees.Output);
            return trees.IsSuccess && lines.Count == 2 && lines[0] != lines[1];
        }

        private GitResult StashNotMade(GitResult push, string own, bool includeUntracked, DateTime pushed,
            string headline)
        {
            var reason = string.IsNullOrEmpty(push.Error)
                ? push.Message
                : new GitResult(push.ExitCode, "", push.Error).Message;
            if (own == null)
            {
                return GitResult.Failure($"{headline}\n{reason}");
            }

            var problem = PutBack(own, includeUntracked, pushed);
            return GitResult.Failure(problem.HasValue
                ? $"{headline}\n{reason}\n{StashKept(own)}\n{problem.Value.Message}"
                : $"{headline}, the project is unchanged\n{reason}\n{InUseHint}");
        }

        private GitResult? PutBack(string stash, bool includeUntracked, DateTime pushed)
        {
            var top = TopLevel();
            var staged = Run("diff", "--cached", "--name-only", "-z", "--no-renames", $"{stash}^2");
            var changed = Run("diff", "--name-only", "-z", "--no-renames", stash);
            var removed = Run("diff", "--name-only", "-z", "--no-renames", "--diff-filter=D", $"{stash}^1", stash);
            foreach (var read in new[] { top, staged, changed, removed })
            {
                if (!read.IsSuccess)
                {
                    return read;
                }
            }

            var inStash = StashPaths(stash);
            if (inStash == null)
            {
                return GitResult.Failure("The stashed files cannot be read");
            }

            var stashed = new HashSet<string>(inStash, StringComparer.Ordinal);
            var worktree = NulSeparated(changed.Output).Where(stashed.Contains).ToList();
            var later = SavedAfter(worktree, pushed);
            var restored = Restore($"{stash}^2", NulSeparated(staged.Output), "--staged") ??
                           Restore(stash, worktree.Where(path => !later.Contains(path)).ToList(), "--worktree");
            if (restored.HasValue)
            {
                return restored;
            }

            var root = top.Output;
            DeleteCreatedFiles(NulSeparated(removed.Output)
                .Where(path => File.Exists(Path.Combine(root, path)))
                .ToList());

            var untracked = includeUntracked ? StashUntrackedFiles(stash) : new List<string>();
            restored = Restore($"{stash}^3", untracked.Where(path => !File.Exists(Path.Combine(root, path))).ToList(),
                "--worktree");
            if (restored.HasValue)
            {
                return restored;
            }

            if (!SameTrees(stash, StashCreate()) ||
                untracked.Any(path => !File.Exists(Path.Combine(root, path))))
            {
                return GitResult.Failure("The local changes could not be put back");
            }

            var drop = DropStashEntry(stash);
            return drop.IsSuccess ? (GitResult?)null : drop;
        }

        private bool SameTrees(string stash, GitResult now)
        {
            if (!now.IsSuccess)
            {
                return false;
            }

            var expected = Trees(stash);
            var actual = Trees(now.Output);
            return expected != null && expected == actual;
        }

        private string Trees(string stash)
        {
            var trees = string.IsNullOrEmpty(stash)
                ? Run("rev-parse", "HEAD^{tree}", "HEAD^{tree}")
                : Run("rev-parse", $"{stash}^{{tree}}", $"{stash}^2^{{tree}}");
            return trees.IsSuccess ? trees.Output : null;
        }

        private bool Untouched(string snapshot, List<string> created, string root)
        {
            if (Conflicts().Count > 0 || created.Any(path => File.Exists(Path.Combine(root, path))))
            {
                return false;
            }

            return SameTrees(snapshot, StashCreate());
        }

        private List<string> StashPaths(string reference)
        {
            var worktree = ChangedPaths($"{reference}^1", reference);
            var index = ChangedPaths($"{reference}^1", $"{reference}^2");
            return worktree != null && index != null ? worktree.Concat(index).Distinct().ToList() : null;
        }

        private List<string> RenamedSince(string stash, IEnumerable<string> paths)
        {
            var wanted = new HashSet<string>(paths, StringComparer.Ordinal);
            var renames = Run("diff", "--name-status", "-z", "--find-renames", $"{stash}^1", "HEAD");
            return renames.IsSuccess
                ? NameStatus(renames.Output.Split('\0'))
                    .Where(entry => entry.Status[0] == 'R' && entry.OldPath != null && wanted.Contains(entry.OldPath))
                    .Select(entry => entry.Path)
                    .ToList()
                : new List<string>();
        }

        public GitResult DropStash(GitStash stash)
        {
            var reference = StashReference(stash.Hash);
            if (reference == null)
            {
                return StashGone(stash, "dropped");
            }

            var drop = DropStashEntry(stash.Hash);
            return drop.IsSuccess ? GitResult.Success($"{reference} dropped") : drop;
        }

        private List<string> StashUntrackedFiles(string reference)
        {
            var parents = Run("log", "-1", "--format=%P", reference).Output
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length < 3)
            {
                return new List<string>();
            }

            return NulSeparated(Run("ls-tree", "-r", "-z", "--name-only", "--full-tree", parents[2]).Output);
        }

        private GitResult RollbackStash(string reference, string stash, string message, GitResult apply,
            List<string> conflicts, string snapshot, List<string> created, TreeState before)
        {
            var reason = conflicts.Count == 0 ? apply.Message : ConflictList(conflicts);
            var staged = Run("diff", "--cached", "--name-only", "-z", "--no-renames");
            var label = $"Uncommitted changes before applying \"{message}\" at {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            if (!string.IsNullOrEmpty(snapshot))
            {
                RememberInterruption($"Applying \"{message}\"", snapshot, label);
            }

            var kept = !string.IsNullOrEmpty(snapshot) && Run("stash", "store", "-m", label, snapshot).IsSuccess;
            var cleanup = (staged.IsSuccess
                ? UndoStashApply(stash, before, conflicts, NulSeparated(staged.Output).Concat(before.Changed))
                : GitResult.Failure("The changed files cannot be read")) ?? GitResult.Success("");
            if (!cleanup.IsSuccess)
            {
                ForgetInterruption();
                var saved = kept
                    ? "Local changes from before it are saved in the stash, see git stash list"
                    : string.IsNullOrEmpty(snapshot)
                        ? ""
                        : $"Local changes from before it: git stash apply --index {snapshot}";
                return GitResult.Failure(
                    $"{StashReference(stash) ?? reference} not applied and the project could not be cleaned up\n{reason}\n" +
                    $"{cleanup.Message}\n{saved}".TrimEnd());
            }

            DeleteCreatedFiles(created);

            var restore = string.IsNullOrEmpty(snapshot) ? GitResult.Success("") : Run("stash", "apply", "--index", snapshot);
            if (restore.IsSuccess && kept)
            {
                DropStashEntry(snapshot);
            }

            ForgetInterruption();
            var because = conflicts.Count == 0 ? "" : " because of conflicts";
            var text = new StringBuilder();
            text.AppendLine(restore.IsSuccess
                ? $"{StashReference(stash) ?? reference} not applied{because}, the project and the stash are unchanged"
                : $"{StashReference(stash) ?? reference} not applied{because}, the local changes could not be restored");
            text.AppendLine(reason);
            if (!restore.IsSuccess)
            {
                text.AppendLine(kept
                    ? StashKept(snapshot)
                    : $"Restore them with git stash apply --index {snapshot}");
                text.AppendLine(restore.Message);
            }

            return GitResult.Failure(text.ToString().TrimEnd());
        }

        private List<string> Stuck(string stash, IEnumerable<string> changed, DateTime pushed)
        {
            var paths = changed.ToList();
            var inStash = new HashSet<string>(StashPaths(stash) ?? paths, StringComparer.Ordinal);
            var saved = SavedAfter(paths, pushed);
            return paths.Where(path => inStash.Contains(path) && !saved.Contains(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }

        private HashSet<string> SavedAfter(IEnumerable<string> paths, DateTime time)
        {
            var saved = new HashSet<string>(StringComparer.Ordinal);
            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return saved;
            }

            var root = top.Output;
            foreach (var path in paths)
            {
                var file = Path.Combine(root, path);
                if (File.Exists(file) && File.GetLastWriteTimeUtc(file) > time)
                {
                    saved.Add(path);
                }
            }

            return saved;
        }

        private string StashHead() => Run("rev-parse", "-q", "--verify", "refs/stash").Output;

        private string OwnStash(string before, string message, string head)
        {
            foreach (var entry in Stashes())
            {
                if (entry.Hash == before)
                {
                    return null;
                }

                if (message != null && !IsStashMessage(entry, message))
                {
                    continue;
                }

                var parent = Run("rev-parse", "-q", "--verify", entry.Hash + "^1");
                if (parent.IsSuccess && parent.Output == head)
                {
                    return entry.Hash;
                }
            }

            return null;
        }

        private static bool IsStashMessage(GitStash entry, string message) =>
            entry.Message.EndsWith(": " + string.Join(" ",
                message.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)),
                StringComparison.Ordinal);

        private const string InterruptionMarker = "vetka-interrupted";

        private static readonly TimeSpan PendingMarkerAge = TimeSpan.FromMinutes(10);

        private string InterruptionMarkerPath() => GitDirPath(InterruptionMarker);

        private void RememberInterruption(string description, string stash, string stashMessage)
        {
            var path = InterruptionMarkerPath();
            if (path == null)
            {
                return;
            }

            try
            {
                File.WriteAllText(path,
                    $"{description}\n{stash}\n{DateTime.Now:yyyy-MM-dd HH:mm}\n{stashMessage}\n", Utf8);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Vetka: cannot write {path}: {exception.Message}");
            }
        }

        private void ForgetInterruption()
        {
            var path = InterruptionMarkerPath();
            if (path != null && File.Exists(path))
            {
                DeleteTempFile(path);
            }
        }

        public (GitInterruption? Found, bool Pending) Interruption()
        {
            var path = InterruptionMarkerPath();
            if (path == null || !File.Exists(path))
            {
                return (null, false);
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path, Utf8);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Vetka: cannot read {path}: {exception.Message}");
                return (null, File.Exists(path));
            }

            if (lines.Length < 2)
            {
                DeleteTempFile(path);
                return (null, File.Exists(path));
            }

            var (list, stashes) = StashList();
            if (!list.IsSuccess)
            {
                return (null, File.Exists(path));
            }

            var hash = lines[1];
            var message = lines.Length > 3 ? lines[3] : "";
            var stash = stashes.FirstOrDefault(current => hash.Length > 0
                ? current.Hash == hash
                : message.Length > 0 && IsStashMessage(current, message));
            if (stash.Reference == null)
            {
                if (hash.Length > 0 || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > PendingMarkerAge)
                {
                    DeleteTempFile(path);
                }

                return (null, File.Exists(path));
            }

            return (new GitInterruption(lines[0], lines.Length > 2 ? lines[2] : "", stash.Reference, stash.Message,
                OperationInProgress().Name, path), true);
        }

        public static void DismissInterruption(GitInterruption found) => DeleteTempFile(found.MarkerPath);

        private GitResult DropStashEntry(string stash)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var reference = StashReference(stash);
                if (reference == null)
                {
                    return GitResult.Success("");
                }

                var drop = Run("stash", "drop", reference);
                var dropped = Regex.Match(drop.Output, @"\(([0-9a-f]{40,64})\)\s*$");
                if (!drop.IsSuccess || !dropped.Success || dropped.Groups[1].Value == stash)
                {
                    return drop;
                }

                var other = dropped.Groups[1].Value;
                var subject = Run("log", "-1", "--format=%s", other);
                var stored = Run("stash", "store", "-m",
                    subject.IsSuccess && subject.Output.Length > 0 ? subject.Output : "Stash entry", other);
                if (!stored.IsSuccess)
                {
                    return GitResult.Failure("Another stash entry was dropped by mistake, " +
                                             $"restore it with git stash store {other}\n{stored.Message}");
                }
            }

            return GitResult.Failure("The stash entry could not be dropped, the stash list kept changing");
        }

        private string StashKept(string stash)
        {
            var reference = StashReference(stash);
            return reference == null || reference == "stash@{0}"
                ? StashKeptText
                : $"Local changes are kept in the stash as {reference}, restore them with git stash pop {reference}";
        }

        private const string StashKeptText = "Local changes are kept in the stash, restore them with git stash pop";
    }
}
