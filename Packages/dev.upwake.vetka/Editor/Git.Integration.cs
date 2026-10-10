using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Upwake.Vetka
{
    internal partial class Git
    {
        public GitResult Fetch() =>
            RunWithProgress("fetch", "--progress", "--prune", "--recurse-submodules=no", "--no-auto-maintenance");

        public GitResult UpdateProject(UpdateStrategy strategy)
        {
            if (strategy == UpdateStrategy.Ask)
            {
                return GitResult.Failure("No update strategy selected");
            }

            var branch = CurrentBranch(out var branchError);
            if (branch == null)
            {
                return branchError;
            }

            if (branch == "HEAD")
            {
                return GitResult.Failure("Cannot update: no branch is checked out");
            }

            var fetch = Interruptible("Update", Fetch);
            if (!fetch.IsSuccess)
            {
                return fetch;
            }

            var upstream = UpstreamBranch();
            if (string.IsNullOrEmpty(upstream))
            {
                var merge = Run("config", "--get", $"branch.{branch}.merge");
                return GitResult.Failure(merge.IsSuccess && merge.Output.Length > 0
                    ? $"The upstream of {branch} ({merge.Output}) no longer exists, it was probably deleted on the remote"
                    : $"Branch {branch} has no upstream to update from");
            }

            return Integrate(strategy, upstream);
        }

        public GitResult UpdateAndPush(UpdateStrategy strategy, PushTags? tags = null)
        {
            if (strategy == UpdateStrategy.Ask)
            {
                return GitResult.Failure("No update strategy selected");
            }

            var destination = PushDestination();
            if (!destination.Target.HasValue)
            {
                return GitResult.Failure(destination.Problem);
            }

            var target = destination.Target.Value;
            var fetch = Interruptible("Update", () => FetchBranch(target));
            if (!fetch.IsSuccess)
            {
                if (fetch.IsCancelled || !BranchMissing(fetch))
                {
                    return fetch;
                }

                var gone = $"{target.Name} no longer exists on the remote";
                var merged = "";
                var tracked = TrackedCommit(target);
                if (tracked != null && !Run("merge-base", "--is-ancestor", tracked, "HEAD").IsSuccess)
                {
                    var integrated = Integrate(strategy, target.TrackingRef, target.Name);
                    if (!integrated.IsSuccess)
                    {
                        return integrated.IsCancelled
                            ? GitResult.Cancelled($"{gone}\n{integrated.Message}")
                            : GitResult.Failure($"{gone}\n{integrated.Message}");
                    }

                    merged = integrated.Message + ", ";
                }

                Run("update-ref", "-d", target.TrackingRef);
                var recreated = Push(tags: tags);
                if (recreated.IsSuccess)
                {
                    return GitResult.Success(
                        $"{merged}{target.Name} no longer existed on the remote\n{recreated.Message}");
                }

                return recreated.IsCancelled
                    ? GitResult.Cancelled($"{merged}{gone}, push cancelled")
                    : GitResult.Failure($"{merged}{gone}, but the push failed\n{recreated.Message}");
            }

            string updated = null;
            if (TrackedCommit(target) != null)
            {
                var update = Integrate(strategy, target.TrackingRef, target.Name);
                if (!update.IsSuccess)
                {
                    return update;
                }

                updated = update.Message;
            }

            var push = Push(tags: tags);
            if (updated == null)
            {
                return push;
            }

            if (push.IsSuccess)
            {
                return GitResult.Success($"{updated}\n{push.Message}");
            }

            return push.IsCancelled
                ? GitResult.Cancelled($"{updated}, push cancelled")
                : GitResult.Failure($"{updated}, but the push failed\n{push.Message}");
        }

        public GitResult Integrate(UpdateStrategy strategy, string target, string name = null)
        {
            if (strategy == UpdateStrategy.Ask)
            {
                return GitResult.Failure("No strategy selected");
            }

            name ??= target;
            var branch = CurrentBranch(out var branchError);
            if (branch == null)
            {
                return branchError;
            }

            if (branch == "HEAD")
            {
                return GitResult.Failure($"Cannot {strategy.ToString().ToLowerInvariant()}: no branch is checked out");
            }

            var behind = Run("rev-list", "--count", $"HEAD..{target}");
            if (behind.IsSuccess && behind.Output == "0")
            {
                return GitResult.Success($"Already up to date with {name}");
            }

            if (strategy == UpdateStrategy.Rebase)
            {
                var merges = Run("rev-list", "--merges", "--count", $"{target}..HEAD");
                if (merges.IsSuccess && merges.Output != "0")
                {
                    return GitResult.Failure($"Rebase onto {name} is not possible, the local commits include " +
                                             "merge commits that a rebase would flatten, use Merge instead");
                }
            }

            var incoming = behind.IsSuccess && int.TryParse(behind.Output, out var parsed) ? parsed : 0;
            var done = strategy == UpdateStrategy.Rebase
                ? $"Rebased onto {name}: {Counted(incoming, "new commit")}"
                : $"Merged {Counted(incoming, "commit")} from {name}";
            var command = strategy == UpdateStrategy.Rebase ? "rebase" : "merge";
            var argument = ShortName(target);

            return WithLocalChangesSaved(new TreeChange
            {
                Description = IntegrationName(strategy, name),
                Target = target,
                ThreeWay = strategy == UpdateStrategy.Merge,
                Done = done,
                Restored = "local changes restored",
                Apply = () => strategy == UpdateStrategy.Rebase
                    ? Run("rebase", argument)
                    : Run("merge", "--no-stat", "--no-edit", argument),
                Abort = () => Run(command, "--abort"),
                Quit = () => Run(command, "--quit"),
                Remember = head => new ReturnPoint
                {
                    Name = head,
                    Head = head,
                    Branch = branch,
                    GoBack = () => Run("reset", "--hard", "--quiet", head),
                    GoBackKeepingChanges = () => Run("reset", "--keep", "--quiet", head)
                }
            });
        }

        public GitResult CherryPick(string commit) => ApplyCommit(commit, false);

        public GitResult Revert(string commit) => ApplyCommit(commit, true);

        private GitResult ApplyCommit(string commit, bool revert)
        {
            var verb = revert ? "revert" : "cherry-pick";
            var branch = CurrentBranch(out var branchError);
            if (branch == null)
            {
                return branchError;
            }

            if (branch == "HEAD")
            {
                return GitResult.Failure($"Cannot {verb}: no branch is checked out");
            }

            if (!HasHead())
            {
                return GitResult.Failure($"Cannot {verb}: {branch} has no commits yet");
            }

            var sequencer = Run("rev-parse", "--git-path", "sequencer");
            if (sequencer.IsSuccess && Directory.Exists(Path.GetFullPath(Path.Combine(_projectRoot, sequencer.Output))))
            {
                return GitResult.Failure($"Cannot {verb}: an earlier cherry-pick or revert of several commits is " +
                                         "unfinished, finish it or quit it with Git first");
            }

            var info = Run("log", "-1", "--format=%h%x1f%P%x1f%s", commit, "--");
            var fields = info.IsSuccess ? info.Output.Split(new[] { LogFieldSeparator }, 3, StringSplitOptions.None) : null;
            if (fields == null || fields.Length < 3)
            {
                return GitResult.Failure($"Commit {commit} cannot be found");
            }

            var hash = fields[0];
            var parents = fields[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length > 1)
            {
                return GitResult.Failure(revert
                    ? $"Reverting merge commit {hash} is not supported"
                    : $"Cherry-picking merge commit {hash} is not supported");
            }

            var contained = Run("merge-base", "--is-ancestor", commit, "HEAD").IsSuccess;
            if (!revert && contained)
            {
                return GitResult.Failure($"{hash} is already in {branch}");
            }

            if (revert && !contained)
            {
                return GitResult.Failure($"{hash} is not in {branch}, only commits of the current branch can be reverted");
            }

            if (revert && parents.Length == 0)
            {
                return GitResult.Failure($"Reverting the first commit {hash} is not supported");
            }

            var parent = parents.Length > 0 ? parents[0] : null;
            var shown = $"{hash} \"{fields[2]}\"";
            return WithLocalChangesSaved(new TreeChange
            {
                Description = revert ? $"Revert of {hash}" : $"Cherry-pick of {hash}",
                Target = revert ? parent : commit,
                IncomingFrom = revert ? commit : parent,
                Done = revert ? $"Reverted {shown}" : $"Cherry-picked {shown} into {branch}",
                Restored = "local changes restored",
                Apply = () =>
                {
                    var applied = revert ? Run("revert", "--no-edit", commit) : Run("cherry-pick", commit);
                    return applied.ExitCode == 1 && string.IsNullOrEmpty(Conflicts()) &&
                           Run("diff", "--cached", "--quiet", "HEAD").IsSuccess
                        ? GitResult.Failure(revert
                            ? $"Its changes are already undone in {branch}"
                            : $"Its changes are already in {branch}")
                        : applied;
                },
                Abort = () => Run(verb, "--abort"),
                Quit = () => Run(verb, "--quit"),
                Remember = head => new ReturnPoint
                {
                    Name = head,
                    Head = head,
                    Branch = branch,
                    GoBack = () => Run("reset", "--hard", "--quiet", head),
                    GoBackKeepingChanges = () => Run("reset", "--keep", "--quiet", head)
                }
            });
        }

        private string ShortName(string reference)
        {
            if (!reference.StartsWith("refs/", StringComparison.Ordinal))
            {
                return reference;
            }

            var shortName = Run("rev-parse", "--abbrev-ref", reference);
            return shortName.IsSuccess && shortName.Output.Length > 0 && !shortName.Output.Contains("\n")
                ? shortName.Output
                : reference;
        }

        private sealed class TreeChange
        {
            public string Description;
            public string Target;
            public string TargetCommit;
            public string IncomingFrom;
            public bool ThreeWay;
            public string Done;
            public string Restored;
            public Func<GitResult> Apply;
            public Func<GitResult> Abort;
            public Func<GitResult> Quit;
            public Func<bool> Arrived;
            public Func<string, ReturnPoint> Remember;
        }

        private sealed class ReturnPoint
        {
            public string Name;
            public string Head;
            public string Branch;
            public Func<GitResult> GoBack;
            public Func<GitResult> GoBackKeepingChanges;
        }

        private static readonly (string Path, string Name)[] OperationsInProgress =
        {
            ("MERGE_HEAD", "a merge"),
            ("CHERRY_PICK_HEAD", "a cherry-pick"),
            ("REVERT_HEAD", "a revert"),
            ("rebase-merge", "a rebase"),
            ("rebase-apply", "a rebase")
        };

        private GitResult? RefuseDuringOperation(string description, string allowed = null)
        {
            var (state, inProgress) = OperationInProgress();
            if (!state.IsSuccess)
            {
                return state;
            }

            return inProgress == null || inProgress == allowed
                ? (GitResult?)null
                : GitResult.Failure(
                    $"{description} is not possible while {inProgress} is in progress, finish or abort it first");
        }

        private GitResult WithLocalChangesSaved(TreeChange change)
        {
            var refusal = RefuseDuringOperation(change.Description);
            if (refusal.HasValue)
            {
                return refusal.Value;
            }

            var head = Run("rev-parse", "-q", "--verify", "HEAD");
            if (!head.IsSuccess)
            {
                var plain = change.Apply();
                return plain.IsSuccess ? GitResult.Success(change.Done) : plain;
            }

            var start = change.Remember(head.Output);
            if (start == null)
            {
                return GitResult.Failure("Cannot read the current branch");
            }

            var target = Run("rev-parse", "-q", "--verify", change.Target + "^{commit}");
            if (!target.IsSuccess)
            {
                return GitResult.Failure($"{change.Description} is not possible, {change.Target} cannot be found");
            }

            change.TargetCommit = target.Output;
            var inTheWay = UntrackedInTheWay(change, head.Output);
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
            if (local.Value)
            {
                var before = StashHead();
                var stashMessage =
                    $"Uncommitted changes before {change.Description} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                RememberInterruption(change.Description, "", stashMessage);
                var push = Run("stash", "push", "-m", stashMessage);
                pushed = DateTime.UtcNow;
                stash = OwnStash(before, stashMessage, head.Output);
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
            else if (change.Arrived != null && string.IsNullOrEmpty(Conflicts()) && change.Arrived())
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
            var diff = Run("diff", "--name-only", "-z", "--no-renames", "--diff-filter=A", head, change.TargetCommit);
            if (!diff.IsSuccess)
            {
                return new List<string>();
            }

            IEnumerable<string> added = NulSeparated(diff.Output);
            var from = change.IncomingFrom;
            if (from == null && change.ThreeWay)
            {
                var mergeBase = Run("merge-base", head, change.TargetCommit);
                from = mergeBase.IsSuccess ? mergeBase.Output : null;
            }

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

            var root = Path.GetFullPath(top.Output);
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

            var diff = Run("diff", "--name-only", "-z", "--no-renames", from, to);
            return diff.IsSuccess ? new HashSet<string>(NulSeparated(diff.Output), StringComparer.Ordinal) : null;
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

            var root = Path.GetFullPath(top.Output);
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

        private GitResult Completed(TreeChange change, ReturnPoint start, string stash, TreeState baseline)
        {
            var now = ReadTree();
            if (now == null)
            {
                return stash == null
                    ? GitResult.Success(change.Done)
                    : GitResult.Failure($"{change.Done}, but the local changes cannot be read\n{StashKept(stash)}");
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
                    : GitResult.Failure(change.Done + stayModified);
            }

            var (restore, withoutStaging) = RestoreLocalChanges(stash);
            if (restore.IsSuccess)
            {
                return withoutStaging
                    ? GitResult.Failure($"{change.Done}, {change.Restored} without the staging\n{StagingKept}{stayModified}")
                    : stayModified.Length == 0
                        ? GitResult.Success($"{change.Done}, {change.Restored}")
                        : GitResult.Failure($"{change.Done}, {change.Restored}{stayModified}");
            }

            var conflicts = Conflicts();
            if (string.IsNullOrEmpty(conflicts))
            {
                return GitResult.Failure(
                    $"{change.Done}, but local changes were not restored\n{StashKept(stash)}\n{restore.Message}{stayModified}");
            }

            var undone = !UndoRestore(stash, baseline, conflicts).HasValue;
            var back = undone ? start.GoBackKeepingChanges() : start.GoBack();
            if (!back.IsSuccess && undone && modified.Count > 0 && OnlyChanged(baseline, modified))
            {
                back = start.GoBack();
            }

            var message = new StringBuilder();
            message.AppendLine(back.IsSuccess
                ? $"{change.Description} rolled back, it conflicts with local changes"
                : $"{change.Description} could not be rolled back, it conflicts with local changes");
            message.AppendLine(ConflictList(conflicts));
            if (!back.IsSuccess)
            {
                message.AppendLine($"Could not return to {start.Name}. {StashKept(stash)}");
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

        private GitResult? UndoRestore(string stash, TreeState baseline, string conflicts)
        {
            var paths = StashPaths(stash);
            var now = ReadTree();
            if (paths == null || now == null)
            {
                return GitResult.Failure("The changed files cannot be read");
            }

            var conflicted = conflicts.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var applied = new HashSet<string>(paths.Concat(RenamedSince(stash, paths)).Concat(conflicted),
                StringComparer.Ordinal);
            return ResetToHead(now.Changed.Concat(conflicted).Where(applied.Contains).Distinct().ToList(),
                path => !baseline.Untracked.Contains(path));
        }

        private HashSet<string> Missing(IEnumerable<string> paths)
        {
            var top = TopLevel();
            if (!top.IsSuccess)
            {
                return new HashSet<string>();
            }

            var root = Path.GetFullPath(top.Output);
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

            return GitResult.Failure(message.ToString().TrimEnd());
        }

        private GitResult RolledBackInUse(TreeChange change, ReturnPoint start, string stash, List<string> stale,
            List<string> unwritten)
        {
            var back = RunWithPathspecs(null, stale.Concat(unwritten), "add");
            if (back.IsSuccess)
            {
                back = start.GoBack();
            }

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
                    message.AppendLine(FileList($"{change.Done}, but these files could not be written", unwritten));
                }

                message.AppendLine($"Could not return to {start.Name}");
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
            var details = string.IsNullOrEmpty(conflicts) ? applied.Message : ConflictList(conflicts);
            var top = TopLevel();
            using var saves = new FileBackup(top.IsSuccess ? Path.GetFullPath(top.Output) : _projectRoot);
            var notSaved = saves.Save(WrittenMeanwhile(conflicts, baseline));
            if (notSaved != null && change.Abort != null)
            {
                var kept = new StringBuilder();
                kept.AppendLine($"{change.Description} failed and was not undone, " +
                                "the files changed while it ran could not be copied first");
                kept.AppendLine(details);
                kept.AppendLine(notSaved);
                var (_, running) = OperationInProgress();
                if (running != null)
                {
                    kept.AppendLine($"{char.ToUpperInvariant(running[0])}{running.Substring(1)} is still in progress, " +
                                    "finish or abort it with Git");
                }

                if (stash != null)
                {
                    kept.AppendLine(StashKept(stash));
                }

                return GitResult.Failure(kept.ToString().TrimEnd());
            }

            var abort = change.Abort?.Invoke();
            var (problem, note) = Unreturned(change, start, baseline, abort, saves);
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

        private List<string> WrittenMeanwhile(string conflicts, TreeState baseline)
        {
            var unstaged = Run("diff", "--name-only", "-z", "--no-renames");
            var now = ReadTree();
            if (!unstaged.IsSuccess || now == null)
            {
                return new List<string>();
            }

            var conflicted = new HashSet<string>(
                conflicts.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            var paths = NulSeparated(unstaged.Output)
                .Where(path => !conflicted.Contains(path))
                .Concat(Added(now.Untracked, baseline.Untracked))
                .Distinct()
                .ToList();
            var missing = Missing(paths);
            return paths.Where(path => !missing.Contains(path)).ToList();
        }

        private (string Problem, string Note) Unreturned(TreeChange change, ReturnPoint start, TreeState baseline,
            GitResult? abort, FileBackup saves)
        {
            var (state, inProgress) = OperationInProgress();
            if (inProgress != null && change.Abort != null)
            {
                abort = change.Abort();
                (state, inProgress) = OperationInProgress();
            }

            if (inProgress != null && change.Quit != null)
            {
                abort = Run("reset", "--hard", "--quiet", start.Head);
                if (abort.Value.IsSuccess && OperationInProgress().Name != null)
                {
                    abort = change.Quit();
                }

                if (abort.Value.IsSuccess && start.Branch != null && CurrentBranch() != start.Branch)
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
                return ($"{char.ToUpperInvariant(inProgress[0])}{inProgress.Substring(1)} is still in progress{reason}",
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
            if (start.Branch != null && branch != start.Branch)
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

            var different = DifferentFrom(change.TargetCommit ?? change.Target, created);
            var mergeBase = change.ThreeWay
                ? Run("merge-base", start.Head, change.TargetCommit)
                : GitResult.Failure("No merge base");
            var touched = Touched(change.IncomingFrom ?? (mergeBase.IsSuccess ? mergeBase.Output : start.Head),
                change.TargetCommit);
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

        private (GitResult State, string Name) OperationInProgress()
        {
            var arguments = new List<string> { "rev-parse" };
            foreach (var operation in OperationsInProgress)
            {
                arguments.Add("--git-path");
                arguments.Add(operation.Path);
            }

            var paths = Run(arguments.ToArray());
            if (!paths.IsSuccess)
            {
                return (paths, null);
            }

            var lines = paths.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < OperationsInProgress.Length && i < lines.Length; i++)
            {
                var path = Path.GetFullPath(Path.Combine(_projectRoot, lines[i]));
                if (File.Exists(path) || Directory.Exists(path))
                {
                    return (paths, OperationsInProgress[i].Name);
                }
            }

            return (paths, null);
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

        private string InterruptionMarkerPath()
        {
            var path = Run("rev-parse", "--git-path", InterruptionMarker);
            return path.IsSuccess ? Path.GetFullPath(Path.Combine(_projectRoot, path.Output)) : null;
        }

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

        public GitInterruption? InterruptedOperation()
        {
            var path = InterruptionMarkerPath();
            if (path == null || !File.Exists(path))
            {
                return null;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path, Utf8);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Vetka: cannot read {path}: {exception.Message}");
                return null;
            }

            if (lines.Length < 2)
            {
                DeleteTempFile(path);
                return null;
            }

            var (list, stashes) = StashList();
            if (!list.IsSuccess)
            {
                return null;
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

                return null;
            }

            return new GitInterruption(lines[0], lines.Length > 2 ? lines[2] : "", stash.Reference, stash.Message,
                OperationInProgress().Name, path);
        }

        private string Conflicts() => Run("diff", "--name-only", "--diff-filter=U").Output;

        private (GitResult Result, bool WithoutStaging) RestoreLocalChanges(string stash)
        {
            var withIndex = Run("stash", "apply", "--index", stash);
            if (!withIndex.IsSuccess)
            {
                return !string.IsNullOrEmpty(Conflicts())
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

        public bool InterruptionPending()
        {
            var path = InterruptionMarkerPath();
            return path != null && File.Exists(path);
        }

        private GitResult DropStashEntry(string stash)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var reference = Stashes().FirstOrDefault(current => current.Hash == stash).Reference;
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

        private string StashKept(string stash)
        {
            var reference = string.IsNullOrEmpty(stash)
                ? null
                : Stashes().FirstOrDefault(current => current.Hash == stash).Reference;
            return reference == null || reference == "stash@{0}"
                ? StashKeptText
                : $"Local changes are kept in the stash as {reference}, restore them with git stash pop {reference}";
        }

        private const string StashKeptText = "Local changes are kept in the stash, restore them with git stash pop";

        private const string StagingKept =
            "The staged version could not be restored, it is kept in the stash, see git stash list";

        private const string InUseHint =
            "Close the programs that use them and try again, a native plugin loaded by Unity needs an editor restart";

        private const string UnwrittenHint =
            "Check the free disk space and the folder permissions, for Git LFS files check the connection to the LFS server";

        private const string ModifiedHint =
            "Git sees them as changed because of their line endings or Git LFS settings, " +
            "commit them as they are or run git add --renormalize .";

        private const string StuckHint = ModifiedHint + " and try again";

        private static string IntegrationName(UpdateStrategy strategy, string target) =>
            strategy == UpdateStrategy.Rebase ? $"Rebase onto {target}" : $"Merge of {target}";

        private static string ConflictList(string conflicts) =>
            FileList("Conflicts", conflicts.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));

        private static string FileList(string title, IEnumerable<string> files)
        {
            var all = files.ToList();
            var list = new StringBuilder(title + ":");
            foreach (var file in all.Take(10))
            {
                list.Append("\n  ").Append(file);
            }

            if (all.Count > 10)
            {
                list.Append($"\n  and {all.Count - 10} more");
            }

            return list.ToString();
        }

        public GitResult Checkout(string branch) =>
            Switch($"Checkout of {branch}", $"Checked out {branch}", "refs/heads/" + branch, branch, null, branch);

        public GitResult CheckoutRemote(string remoteBranch)
        {
            const string remotes = "refs/remotes/";
            var remoteRef = remoteBranch.StartsWith(remotes, StringComparison.Ordinal)
                ? remoteBranch
                : remotes + remoteBranch;
            remoteBranch = remoteRef.Substring(remotes.Length);
            var separator = remoteBranch.IndexOf('/');
            var local = separator < 0 ? remoteBranch : remoteBranch.Substring(separator + 1);
            if (!Run("rev-parse", "-q", "--verify", "refs/heads/" + local).IsSuccess)
            {
                return Switch($"Checkout of {remoteBranch} as {local}", $"Checked out {remoteBranch} as {local}",
                    remoteRef, local, local, "--track", remoteRef);
            }

            var upstream = Run("for-each-ref", "--format=%(upstream)", "refs/heads/" + local).Output;
            var pushTarget = PushTarget(local, out _);
            if (upstream != remoteRef && (!pushTarget.HasValue || pushTarget.Value.TrackingRef != remoteRef))
            {
                return GitResult.Failure(
                    $"Checkout of {remoteBranch} is not possible, the local branch {local} already exists and " +
                    $"does not track it\nCheck out {local} from the local branches or create a new branch from {remoteBranch}");
            }

            var behind = Run("rev-list", "--count", $"refs/heads/{local}..{remoteRef}");
            var advice = upstream == remoteRef
                ? "update the project to get them"
                : $"merge {remoteBranch} into it to get them";
            var note = behind.IsSuccess && behind.Output != "0"
                ? $"\n{local} is {Counted(int.TryParse(behind.Output, out var count) ? count : 0, "commit")} " +
                  $"behind {remoteBranch}, {advice}"
                : "";
            if (CurrentBranch() == local)
            {
                return GitResult.Success($"{local} is already checked out{note}");
            }

            var checkout = Checkout(local);
            return note.Length == 0
                ? checkout
                : checkout.IsSuccess
                    ? GitResult.Success(checkout.Message + note)
                    : checkout;
        }

        public GitResult CreateBranch(string name, string startPoint, bool checkout, string startName = null)
        {
            name = name?.Trim() ?? "";
            if (!Run("check-ref-format", "--branch", name).IsSuccess)
            {
                return GitResult.Failure($"'{name}' is not a valid branch name");
            }

            var start = string.IsNullOrEmpty(startPoint) ? "HEAD" : startPoint;
            if (start == "HEAD" && !HasHead())
            {
                if (!checkout)
                {
                    return GitResult.Failure($"Branch {name} cannot be created before the first commit");
                }

                var created = Run("switch", "-c", name);
                return created.IsSuccess ? GitResult.Success($"Branch {name} created and checked out") : created;
            }

            var shown = startName ?? start;
            if (checkout)
            {
                return Switch($"Creation of {name}", $"Branch {name} created from {shown} and checked out", start,
                    name, name, "-c", name, start);
            }

            var create = Run("branch", name, start);
            return create.IsSuccess ? GitResult.Success($"Branch {name} created from {shown}") : create;
        }

        public GitResult DeleteBranch(string name, bool force)
        {
            var delete = Run("branch", force ? "-D" : "-d", name);
            return delete.IsSuccess ? GitResult.Success($"Branch {name} deleted") : delete;
        }

        public bool CanForceDelete(string name)
        {
            var branch = Run("for-each-ref", "--format=%(worktreepath)", "refs/heads/" + name);
            return branch.IsSuccess && Run("rev-parse", "-q", "--verify", "refs/heads/" + name).IsSuccess &&
                   branch.Output.Length == 0;
        }

        private GitResult Switch(string description, string done, string target, string expectedBranch,
            string createdBranch, params string[] arguments)
        {
            var switchArguments = new[] { "switch" }.Concat(arguments).ToArray();
            var change = new TreeChange
            {
                Description = description,
                Target = target,
                Done = done,
                Restored = "local changes carried over",
                Apply = () => Run(switchArguments)
            };
            change.Arrived = () => CurrentBranch() == expectedBranch && HeadCommit() == change.TargetCommit;
            change.Remember = head =>
            {
                var original = CurrentBranch();
                return string.IsNullOrEmpty(original)
                    ? null
                    : new ReturnPoint
                    {
                        Name = original,
                        Head = head,
                        Branch = original,
                        GoBack = () => SwitchBack(original, head, createdBranch, true),
                        GoBackKeepingChanges = () => SwitchBack(original, head, createdBranch, false)
                    };
            };
            return WithLocalChangesSaved(change);
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
