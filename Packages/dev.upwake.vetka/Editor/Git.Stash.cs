using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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

            return (result, result.Output
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(new[] { LogFieldSeparator }, StringSplitOptions.None))
                .Where(fields => fields.Length == 4)
                .Select(fields => new GitStash(fields[0],
                    fields[1].Length > 16 ? fields[1].Substring(0, 16) : fields[1], fields[2], fields[3]))
                .ToList());
        }

        private string CurrentReference(GitStash stash) =>
            Stashes().FirstOrDefault(current => current.Hash == stash.Hash).Reference;

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
                : GitResult.Failure($"{done}\n{FileList("These files stay modified after stashing", stuck)}\n{ModifiedHint}");
        }

        public GitResult ApplyStash(GitStash stash, bool drop)
        {
            var reference = CurrentReference(stash);
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

            var root = Path.GetFullPath(top.Output);
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
                    ? GitResult.Failure($"{reference} applied without the staging and not dropped\n{StagingKept}")
                    : GitResult.Success($"{reference} applied without the staging");
            }

            if (!drop)
            {
                return GitResult.Success($"{reference} applied");
            }

            var dropResult = DropStashEntry(stash.Hash);
            return dropResult.IsSuccess
                ? GitResult.Success($"{reference} applied and dropped")
                : GitResult.Failure($"{reference} applied, but not dropped\n{dropResult.Message}");
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
            var lines = trees.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            return trees.IsSuccess && lines.Length == 2 && lines[0] != lines[1];
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

            var root = Path.GetFullPath(top.Output);
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
            if (!string.IsNullOrEmpty(Conflicts()) || created.Any(path => File.Exists(Path.Combine(root, path))))
            {
                return false;
            }

            var now = StashCreate();
            var expected = Trees(snapshot);
            return now.IsSuccess && expected != null && expected == Trees(now.Output);
        }

        private List<string> StashPaths(string reference)
        {
            var worktree = Run("diff", "--name-only", "-z", "--no-renames", $"{reference}^1", reference);
            var index = Run("diff", "--name-only", "-z", "--no-renames", $"{reference}^1", $"{reference}^2");
            return worktree.IsSuccess && index.IsSuccess
                ? (worktree.Output + "\0" + index.Output).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)
                .Distinct()
                .ToList()
                : null;
        }

        private List<string> RenamedSince(string stash, IEnumerable<string> paths)
        {
            var wanted = new HashSet<string>(paths, StringComparer.Ordinal);
            var renames = Run("diff", "--name-status", "-z", "--find-renames", $"{stash}^1", "HEAD");
            var found = new List<string>();
            if (!renames.IsSuccess)
            {
                return found;
            }

            var tokens = renames.Output.Split('\0');
            for (var i = 0; i + 1 < tokens.Length; i++)
            {
                var status = tokens[i];
                if (status.Length == 0)
                {
                    continue;
                }

                if (status[0] != 'R' && status[0] != 'C')
                {
                    i++;
                    continue;
                }

                if (i + 2 < tokens.Length && status[0] == 'R' && wanted.Contains(tokens[i + 1]))
                {
                    found.Add(tokens[i + 2]);
                }

                i += 2;
            }

            return found;
        }

        public GitResult DropStash(GitStash stash)
        {
            var reference = CurrentReference(stash);
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

            return Run("ls-tree", "-r", "-z", "--name-only", "--full-tree", parents[2]).Output
                .Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        private GitResult RollbackStash(string reference, string stash, string message, GitResult apply,
            string conflicts, string snapshot, List<string> created, TreeState before)
        {
            var conflicted = conflicts.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var reason = conflicted.Length == 0 ? apply.Message : ConflictList(conflicts);
            var touched = StashPaths(stash);
            var staged = Run("diff", "--cached", "--name-only", "-z", "--no-renames");
            var now = ReadTree();
            var label = $"Uncommitted changes before applying \"{message}\" at {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            if (!string.IsNullOrEmpty(snapshot))
            {
                RememberInterruption($"Applying \"{message}\"", snapshot, label);
            }

            var kept = !string.IsNullOrEmpty(snapshot) && Run("stash", "store", "-m", label, snapshot).IsSuccess;
            var cleanup = touched == null || now == null || !staged.IsSuccess
                ? GitResult.Failure("The changed files cannot be read")
                : ResetToHead(now.Changed
                    .Where(new HashSet<string>(touched
                        .Concat(RenamedSince(stash, touched))
                        .Concat(NulSeparated(staged.Output))
                        .Concat(before.Changed)
                        .Concat(conflicted)).Contains)
                    .ToList(), path => !before.Untracked.Contains(path)) ?? GitResult.Success("");
            if (!cleanup.IsSuccess)
            {
                ForgetInterruption();
                var saved = kept
                    ? "Local changes from before it are saved in the stash, see git stash list"
                    : string.IsNullOrEmpty(snapshot)
                        ? ""
                        : $"Local changes from before it: git stash apply --index {snapshot}";
                return GitResult.Failure(
                    $"{Named(stash, reference)} not applied and the project could not be cleaned up\n{reason}\n" +
                    $"{cleanup.Message}\n{saved}".TrimEnd());
            }

            DeleteCreatedFiles(created);

            var restore = string.IsNullOrEmpty(snapshot) ? GitResult.Success("") : Run("stash", "apply", "--index", snapshot);
            if (restore.IsSuccess && kept)
            {
                DropStashEntry(snapshot);
            }

            ForgetInterruption();
            var because = conflicted.Length == 0 ? "" : " because of conflicts";
            var text = new StringBuilder();
            text.AppendLine(restore.IsSuccess
                ? $"{Named(stash, reference)} not applied{because}, the project and the stash are unchanged"
                : $"{Named(stash, reference)} not applied{because}, the local changes could not be restored");
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

        private string Named(string stash, string fallback) =>
            Stashes().FirstOrDefault(current => current.Hash == stash).Reference ?? fallback;
    }
}
