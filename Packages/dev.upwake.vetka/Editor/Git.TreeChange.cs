using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Upwake.Vetka
{
    internal partial class Git
    {
        private sealed class TreeChange
        {
            public string Description;
            public string Target;
            public string TargetCommit;
            public string IncomingFrom;
            public bool ThreeWay;
            public string Done;
            public string Command;
            public string ExpectedBranch;
            public string CreatedBranch;
            public Func<GitResult> Apply;

            public bool Switches => Command == null;

            public string Restored => Switches ? "local changes carried over" : "local changes restored";
        }

        private sealed class ReturnPoint
        {
            public string Head;
            public string Branch;
        }

        private GitResult GoBack(ReturnPoint start, TreeChange change, bool discard) =>
            change.Switches
                ? SwitchBack(start.Branch, start.Head, change.CreatedBranch, discard)
                : Run("reset", discard ? "--hard" : "--keep", "--quiet", start.Head);

        private static string Destination(ReturnPoint start, TreeChange change) =>
            change.Switches ? start.Branch : start.Head;

        private bool Arrived(TreeChange change) =>
            CurrentBranch() == change.ExpectedBranch && HeadCommit() == change.TargetCommit;

        private string IncomingBase(TreeChange change, string head)
        {
            if (change.IncomingFrom != null || !change.ThreeWay)
            {
                return change.IncomingFrom;
            }

            var mergeBase = Run("merge-base", head, change.TargetCommit);
            return mergeBase.IsSuccess ? mergeBase.Output : null;
        }

        private GitResult WithLocalChangesSaved(TreeChange change)
        {
            var refusal = RefuseDuringOperation(change.Description);
            if (refusal.HasValue)
            {
                return refusal.Value;
            }

            var head = HeadCommit();
            if (head == null)
            {
                var plain = change.Apply();
                return plain.IsSuccess ? GitResult.Success(change.Done) : plain;
            }

            var branch = CurrentBranch();
            if (string.IsNullOrEmpty(branch))
            {
                return GitResult.Failure("Cannot read the current branch");
            }

            var start = new ReturnPoint { Head = head, Branch = branch };

            change.TargetCommit = CommitOf(change.Target);
            if (change.TargetCommit == null)
            {
                return GitResult.Failure($"{change.Description} is not possible, {change.Target} cannot be found");
            }

            var inTheWay = UntrackedInTheWay(change, head);
            if (inTheWay.Count > 0)
            {
                return GitResult.Failure(FileList(
                                             $"{change.Description} is not possible, it would overwrite these files that Git does not track",
                                             inTheWay) +
                                         "\nMove or delete them and try again");
            }

            var local = HasUncommittedChanges();
            if (!local.HasValue)
            {
                return GitResult.Failure($"{change.Description} is not possible, the local changes cannot be read");
            }

            string stash = null;
            var pushed = DateTime.UtcNow;
            if (local.Value && change.TargetCommit != head)
            {
                var before = StashHead();
                var stashMessage =
                    $"Uncommitted changes before {change.Description} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                RememberInterruption(change.Description, "", stashMessage);
                var push = Run("stash", "push", "-m", stashMessage);
                pushed = DateTime.UtcNow;
                stash = OwnStash(before, stashMessage, head);
                if (!push.IsSuccess)
                {
                    var notMade = StashNotMade(push, stash, false, pushed,
                        $"{change.Description} is not possible, the local changes could not be stashed");
                    ForgetInterruption();
                    return notMade;
                }

                if (stash == null)
                {
                    ForgetInterruption();
                }
                else
                {
                    RememberInterruption(change.Description, stash, stashMessage);
                }
            }

            var baseline = ReadTree() ?? new TreeState();
            var stuck = stash != null && baseline.Changed.Count > 0
                ? Stuck(stash, baseline.Changed, pushed)
                : new List<string>();
            if (stuck.Count > 0)
            {
                var problem = PutBack(stash, false, pushed);
                ForgetInterruption();
                return GitResult.Failure(problem.HasValue
                    ? $"{FileList($"{change.Description} is not possible, these files stay modified after stashing", stuck)}\n" +
                      $"{StashKept(stash)}\n{problem.Value.Message}"
                    : $"{FileList($"{change.Description} is not possible, the project is unchanged. These files stay modified even after stashing", stuck)}\n" +
                      StuckHint);
            }

            var applied = change.Apply();
            GitResult result;
            if (applied.IsSuccess)
            {
                result = Completed(change, start, stash, baseline);
            }
            else if (change.Switches && Conflicts().Count == 0 && Arrived(change))
            {
                var completed = Completed(change, start, stash, baseline);
                result = GitResult.Failure($"{completed.Message}\n" +
                                           "Git reported an error after it, a hook of this repository may have failed:\n" +
                                           applied.Message);
            }
            else
            {
                result = Undone(change, start, stash, baseline, applied);
            }

            if (stash != null)
            {
                ForgetInterruption();
            }

            return result;
        }

        private List<string> UntrackedInTheWay(TreeChange change, string head)
        {
            IEnumerable<string> added = ChangedPaths("--diff-filter=A", head, change.TargetCommit);
            if (added == null)
            {
                return new List<string>();
            }

            var from = IncomingBase(change, head);
            var incoming = from != null ? Touched(from, change.TargetCommit) : null;
            if (incoming != null)
            {
                added = added.Where(incoming.Contains);
            }

            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return new List<string>();
            }

            var root = top.Output;
            return added.Select(path => InTheWay(root, path))
                .Where(path => path != null)
                .Distinct()
                .Where(path => Run("ls-files", "-z", "--", ":(top,icase,literal)" + path).Output.Length == 0)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }

        private static string InTheWay(string root, string path)
        {
            var full = Path.Combine(root, path);
            if (File.Exists(full) || Directory.Exists(full))
            {
                return path;
            }

            for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                var parent = path.Substring(0, slash);
                if (File.Exists(Path.Combine(root, parent)))
                {
                    return parent;
                }
            }

            return null;
        }

        private HashSet<string> Touched(string from, string to)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            {
                return null;
            }

            var paths = ChangedPaths(from, to);
            return paths == null ? null : new HashSet<string>(paths, StringComparer.Ordinal);
        }

        private GitResult Completed(TreeChange change, ReturnPoint start, string stash, TreeState baseline)
        {
            var now = ReadTree();
            if (now == null)
            {
                return stash == null
                    ? GitResult.Success(change.Done)
                    : GitResult.Done($"{change.Done}, but the local changes cannot be read\n{StashKept(stash)}");
            }

            var changed = Added(now.Changed, baseline.Changed);
            var created = Added(now.Untracked, baseline.Untracked);
            var modified = new List<string>();
            if (changed.Count > 0 || created.Count > 0)
            {
                var touched = Touched(start.Head, HeadCommit());
                var unwritten = new HashSet<string>(Missing(changed).Where(path => touched == null || touched.Contains(path)),
                    StringComparer.Ordinal);
                var different = DifferentFrom(start.Head, created);
                var stale = different == null
                    ? new List<string>()
                    : changed.Concat(created)
                        .Where(path => !different.Contains(path) && !unwritten.Contains(path))
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToList();
                var rest = changed
                    .Where(path => !unwritten.Contains(path) && (different == null || different.Contains(path)))
                    .ToList();
                modified = touched == null ? new List<string>() : rest.Where(touched.Contains).ToList();
                var foreign = rest.Where(path => !modified.Contains(path)).ToList();
                if (foreign.Count > 0 && (stash != null || stale.Count > 0 || unwritten.Count > 0))
                {
                    return ChangedMeanwhile(change, stash, foreign, stale,
                        unwritten.OrderBy(path => path, StringComparer.Ordinal).ToList());
                }

                if (stale.Count > 0 || unwritten.Count > 0)
                {
                    return RolledBackInUse(change, start, stash, stale,
                        unwritten.OrderBy(path => path, StringComparer.Ordinal).ToList());
                }
            }

            var stayModified = modified.Count == 0
                ? ""
                : $"\n{FileList("These files it wrote stay modified", modified)}\n{ModifiedHint}";
            if (stash == null)
            {
                return stayModified.Length == 0
                    ? GitResult.Success(change.Done)
                    : GitResult.Done(change.Done + stayModified);
            }

            var (restore, withoutStaging) = RestoreLocalChanges(stash);
            if (restore.IsSuccess)
            {
                return withoutStaging
                    ? GitResult.Done($"{change.Done}, {change.Restored} without the staging\n{StagingKept}{stayModified}")
                    : stayModified.Length == 0
                        ? GitResult.Success($"{change.Done}, {change.Restored}")
                        : GitResult.Done($"{change.Done}, {change.Restored}{stayModified}");
            }

            var conflicts = Conflicts();
            if (conflicts.Count == 0)
            {
                return GitResult.Done(
                    $"{change.Done}, but local changes were not restored\n{StashKept(stash)}\n{restore.Message}{stayModified}");
            }

            var undone = !UndoStashApply(stash, baseline, conflicts, Array.Empty<string>()).HasValue;
            var back = GoBack(start, change, !undone);
            if (!back.IsSuccess && undone && modified.Count > 0 && OnlyChanged(baseline, modified))
            {
                back = GoBack(start, change, true);
            }

            var message = new StringBuilder();
            message.AppendLine(back.IsSuccess
                ? $"{change.Description} rolled back, it conflicts with local changes"
                : $"{change.Description} could not be rolled back, it conflicts with local changes");
            message.AppendLine(ConflictList(conflicts));
            if (!back.IsSuccess)
            {
                message.AppendLine($"Could not return to {Destination(start, change)}. {StashKept(stash)}");
                message.AppendLine(back.Message);
                return GitResult.Failure(message.ToString().TrimEnd());
            }

            AppendIfNotRestored(message, RestoreLocalChanges(stash), stash);
            return GitResult.Failure(message.ToString().TrimEnd());
        }

        private bool OnlyChanged(TreeState baseline, ICollection<string> paths)
        {
            var now = ReadTree();
            return now != null && Added(now.Changed, baseline.Changed).All(paths.Contains);
        }

        private GitResult? UndoStashApply(string stash, TreeState before, List<string> conflicts,
            IEnumerable<string> alsoReset)
        {
            var paths = StashPaths(stash);
            var now = ReadTree();
            if (paths == null || now == null)
            {
                return GitResult.Failure("The changed files cannot be read");
            }

            var applied = new HashSet<string>(paths.Concat(RenamedSince(stash, paths)).Concat(conflicts).Concat(alsoReset),
                StringComparer.Ordinal);
            return ResetToHead(now.Changed.Concat(conflicts).Where(applied.Contains).Distinct().ToList(),
                path => !before.Untracked.Contains(path));
        }

        private HashSet<string> Missing(IEnumerable<string> paths)
        {
            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return new HashSet<string>();
            }

            var root = top.Output;
            return new HashSet<string>(paths.Where(path => !File.Exists(Path.Combine(root, path)) &&
                                                           !Directory.Exists(Path.Combine(root, path))),
                StringComparer.Ordinal);
        }

        private GitResult ChangedMeanwhile(TreeChange change, string stash, List<string> foreign,
            List<string> stale, List<string> unwritten)
        {
            var message = new StringBuilder();
            message.AppendLine(FileList($"{change.Done}, but files were changed while it ran", foreign));
            if (stale.Count > 0)
            {
                message.AppendLine(FileList("These files are in use and could not be updated", stale));
            }

            if (unwritten.Count > 0)
            {
                message.AppendLine(FileList("These files could not be written", unwritten));
            }

            if (stash != null)
            {
                message.AppendLine(StashKept(stash));
            }

            return GitResult.Done(message.ToString().TrimEnd());
        }

        private GitResult RolledBackInUse(TreeChange change, ReturnPoint start, string stash, List<string> stale,
            List<string> unwritten)
        {
            var staged = RunWithPathspecs(null, stale.Concat(unwritten), "add");
            var back = staged.IsSuccess ? GoBack(start, change, true) : staged;
            var message = new StringBuilder();
            if (!back.IsSuccess)
            {
                if (stale.Count > 0)
                {
                    message.AppendLine(FileList($"{change.Done}, but these files are in use and could not be updated",
                        stale));
                }

                if (unwritten.Count > 0)
                {
                    message.AppendLine(FileList(stale.Count > 0
                        ? "These files could not be written"
                        : $"{change.Done}, but these files could not be written", unwritten));
                }

                if (staged.IsSuccess)
                {
                    message.AppendLine("These files are staged as they are on disk");
                }

                message.AppendLine($"Could not return to {Destination(start, change)}");
                message.AppendLine(back.Message);
                if (stash != null)
                {
                    message.AppendLine(StashKept(stash));
                }

                return GitResult.Failure(message.ToString().TrimEnd());
            }

            if (stale.Count > 0)
            {
                message.AppendLine(FileList(
                    $"{change.Description} rolled back, these files are in use and could not be updated", stale));
                message.AppendLine(InUseHint);
            }

            if (unwritten.Count > 0)
            {
                message.AppendLine(FileList(stale.Count > 0
                    ? "These files could not be written"
                    : $"{change.Description} rolled back, these files could not be written", unwritten));
                message.AppendLine(UnwrittenHint);
            }

            if (stash != null)
            {
                AppendIfNotRestored(message, RestoreLocalChanges(stash), stash);
            }

            return GitResult.Failure(message.ToString().TrimEnd());
        }

        private GitResult Undone(TreeChange change, ReturnPoint start, string stash, TreeState baseline,
            GitResult applied)
        {
            var conflicts = Conflicts();
            var details = conflicts.Count == 0 ? applied.Message : ConflictList(conflicts);
            var top = TopLevel();
            using var saves = new FileBackup(top.IsSuccess ? top.Output : _projectRoot);
            var notSaved = saves.Save(WrittenMeanwhile(conflicts, baseline));
            if (notSaved != null && !change.Switches)
            {
                var kept = new StringBuilder();
                kept.AppendLine($"{change.Description} failed and was not undone, " +
                                "the files changed while it ran could not be copied first");
                kept.AppendLine(details);
                kept.AppendLine(notSaved);
                var (_, running) = OperationInProgress();
                if (running != null)
                {
                    kept.AppendLine($"{Texts.Capitalized(running)} is still in progress, " +
                                    "finish or abort it with Git");
                }

                if (stash != null)
                {
                    kept.AppendLine(StashKept(stash));
                }

                return GitResult.Failure(kept.ToString().TrimEnd());
            }

            var (problem, note) = Unreturned(change, start, baseline, saves);
            var restore = problem == null && stash != null
                ? RestoreLocalChanges(stash)
                : ((GitResult Result, bool WithoutStaging)?)null;

            var message = new StringBuilder();
            message.AppendLine(problem != null
                ? $"{change.Description} failed and could not be undone"
                : restore.HasValue && !restore.Value.Result.IsSuccess
                    ? $"{change.Description} failed"
                    : $"{change.Description} failed, the project is unchanged");
            message.AppendLine(details);
            if (problem != null)
            {
                message.AppendLine(problem);
                if (stash != null)
                {
                    message.AppendLine(StashKept(stash));
                }
            }
            else if (restore.HasValue)
            {
                AppendIfNotRestored(message, restore.Value, stash);
            }

            if (note != null)
            {
                message.AppendLine(note);
            }

            return GitResult.Failure(message.ToString().TrimEnd());
        }

        private List<string> WrittenMeanwhile(List<string> conflicts, TreeState baseline)
        {
            var unstaged = ChangedPaths();
            var now = ReadTree();
            if (unstaged == null || now == null)
            {
                return new List<string>();
            }

            var conflicted = new HashSet<string>(conflicts, StringComparer.Ordinal);
            var paths = unstaged
                .Where(path => !conflicted.Contains(path))
                .Concat(Added(now.Untracked, baseline.Untracked))
                .Distinct()
                .ToList();
            var missing = Missing(paths);
            return paths.Where(path => !missing.Contains(path)).ToList();
        }

        private (string Problem, string Note) Unreturned(TreeChange change, ReturnPoint start, TreeState baseline,
            FileBackup saves)
        {
            var abort = change.Switches ? (GitResult?)null : Run(change.Command, "--abort");
            var (state, inProgress) = OperationInProgress();
            if (inProgress != null && !change.Switches)
            {
                abort = Run(change.Command, "--abort");
                (state, inProgress) = OperationInProgress();
            }

            if (inProgress != null && !change.Switches)
            {
                abort = GoBack(start, change, true);
                if (abort.Value.IsSuccess && OperationInProgress().Name != null)
                {
                    abort = Run(change.Command, "--quit");
                }

                if (abort.Value.IsSuccess && CurrentBranch() != start.Branch)
                {
                    abort = SwitchBack(start.Branch, start.Head, null, false);
                }

                (state, inProgress) = OperationInProgress();
            }

            if (!state.IsSuccess)
            {
                return (state.Message, null);
            }

            if (inProgress != null)
            {
                var reason = abort.HasValue && !abort.Value.IsSuccess ? $": {abort.Value.Message}" : "";
                return ($"{Texts.Capitalized(inProgress)} is still in progress{reason}",
                    null);
            }

            var lost = saves.Restore();
            if (lost.Count > 0)
            {
                return (FileList($"These files changed while it ran and could not be put back, " +
                                 $"their copies are in {saves.Keep()}", lost), null);
            }

            var head = Run("rev-parse", "HEAD");
            if (!head.IsSuccess)
            {
                return (head.Message, null);
            }

            if (head.Output != start.Head)
            {
                return ($"The current commit is {head.Output} instead of {start.Head}", null);
            }

            var branch = CurrentBranch();
            if (branch != start.Branch)
            {
                return ($"The current branch is {branch ?? "unknown"} instead of {start.Branch}", null);
            }

            var now = ReadTree();
            if (now == null)
            {
                return ("The local changes cannot be read", null);
            }

            var changed = Added(now.Changed, baseline.Changed);
            var created = Added(now.Untracked, baseline.Untracked);
            if (changed.Count == 0 && created.Count == 0)
            {
                return (null, null);
            }

            var different = DifferentFrom(change.TargetCommit, created);
            var touched = Touched(IncomingBase(change, start.Head) ?? start.Head, change.TargetCommit);
            if (different == null || touched == null)
            {
                return (FileList("Files changed while it ran", changed.Concat(created)), null);
            }

            var unwritten = Missing(changed).Where(touched.Contains).OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            var leftovers = changed.Where(path => different.Contains(path) && !unwritten.Contains(path)).ToList();
            var restored = RestoreFromHead(changed.Where(path => !leftovers.Contains(path)).ToList(),
                "--staged", "--worktree");
            if (restored.HasValue)
            {
                return (restored.Value.Message, null);
            }

            var notes = new List<string>();
            if (unwritten.Count > 0)
            {
                notes.Add(FileList("These files could not be written and were put back", unwritten));
            }

            if (leftovers.Count > 0)
            {
                var stored = StoreLeftovers(change);
                if (stored == null)
                {
                    return (FileList("Files changed while it ran", leftovers), null);
                }

                restored = RestoreFromHead(leftovers, "--staged", "--worktree");
                if (restored.HasValue)
                {
                    return ($"{restored.Value.Message}\nTheir changes are saved in the stash as \"{stored}\"", null);
                }

                notes.Add(FileList($"These files were changed while it ran or left changed by it, " +
                                   $"the changes are saved in the stash as \"{stored}\"", leftovers));
            }

            DeleteCreatedFiles(created.Where(path => !different.Contains(path)).ToList());
            var appeared = created.Where(different.Contains).ToList();
            if (appeared.Count > 0)
            {
                notes.Add(FileList("These new files appeared while it ran and are kept", appeared));
            }

            return (null, notes.Count == 0 ? null : string.Join("\n", notes));
        }

        private string StoreLeftovers(TreeChange change)
        {
            var label = $"Changes left after {change.Description} failed at {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            var snapshot = StashCreate(label);
            if (!snapshot.IsSuccess || snapshot.Output.Length == 0)
            {
                return null;
            }

            return Run("stash", "store", "-m", label, snapshot.Output).IsSuccess ? label : null;
        }

        private static List<string> Added(HashSet<string> now, HashSet<string> before) =>
            now.Where(path => !before.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToList();

        private (GitResult Result, bool WithoutStaging) RestoreLocalChanges(string stash)
        {
            var withIndex = Run("stash", "apply", "--index", stash);
            if (!withIndex.IsSuccess)
            {
                return Conflicts().Count > 0
                    ? (withIndex, false)
                    : (Run("stash", "apply", stash), true);
            }

            var drop = DropStashEntry(stash);
            if (!drop.IsSuccess)
            {
                Debug.LogWarning($"Vetka: the local changes were restored, but their stash entry stays: {drop.Message}");
            }

            return (withIndex, false);
        }

        private void AppendIfNotRestored(StringBuilder message, (GitResult Result, bool WithoutStaging) restore,
            string stash)
        {
            if (!restore.Result.IsSuccess)
            {
                message.AppendLine(StashKept(stash));
                message.AppendLine(restore.Result.Message);
            }
            else if (restore.WithoutStaging)
            {
                message.AppendLine(StagingKept);
            }
        }

        private GitResult SwitchBack(string original, string originalHead, string createdBranch, bool discard)
        {
            var arguments = new List<string> { "switch" };
            if (discard)
            {
                arguments.Add("--discard-changes");
            }

            if (original == "HEAD")
            {
                arguments.Add("--detach");
                arguments.Add(originalHead);
            }
            else
            {
                arguments.Add(original);
            }

            var back = Run(arguments.ToArray());
            if (!back.IsSuccess && CurrentBranch() == original && HeadCommit() == originalHead)
            {
                back = GitResult.Success(back.Output);
            }

            if (back.IsSuccess && !string.IsNullOrEmpty(createdBranch))
            {
                Run("branch", "-D", createdBranch);
            }

            return back;
        }
    }
}
