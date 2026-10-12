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
            Run("fetch", "--progress", "--prune", "--recurse-submodules=no", "--no-auto-maintenance");

        public GitResult UpdateProject(UpdateStrategy strategy)
        {
            var busy = RefuseDuringOperation(UpdateProjectName);
            if (busy.HasValue)
            {
                return busy.Value;
            }

            var branch = CheckedOutBranch("update", out var refusal);
            if (branch == null)
            {
                return refusal;
            }

            var before = FetchedRefs();
            var fetch = Interruptible("Update", Fetch);
            if (!fetch.IsSuccess)
            {
                return fetch;
            }

            var after = before == null ? null : FetchedRefs();
            var source = ResolveUpstream(branch);
            if (source.Problem != null)
            {
                return GitResult.Failure(source.Problem);
            }

            var upstream = source.Name;
            if (source.NotStored)
            {
                var tracked = source.Tracked.Value;
                var fetched = Interruptible("Update", () => FetchBranch(tracked));
                if (fetched.IsCancelled || !fetched.IsSuccess && !BranchMissing(fetched))
                {
                    return fetched;
                }

                if (!fetched.IsSuccess)
                {
                    Run("update-ref", "-d", tracked.TrackingRef);
                    return GitResult.Failure(source.GoneText);
                }

                after = before == null ? null : FetchedRefs();
            }

            var integrated = Integrate(strategy, upstream);
            var report = integrated.IsDone ? FetchReport(before, after, upstream) : "";
            if (report.Length == 0)
            {
                return integrated;
            }

            var text = $"{integrated.Message}\n{report}";
            return integrated.IsSuccess ? GitResult.Success(text) : GitResult.Done(text);
        }

        public GitResult UpdateAndPush(UpdateStrategy strategy, PushTags? tags = null)
        {
            var destination = PushDestination();
            if (!destination.Target.HasValue)
            {
                return GitResult.Failure(destination.Problem);
            }

            var target = destination.Target.Value;
            var fetch = Interruptible("Update", () => FetchBranch(target));
            if (!fetch.IsSuccess)
            {
                if (!BranchMissing(fetch))
                {
                    return fetch;
                }

                var gone = $"{target.Name} no longer exists on the remote";
                var merged = "";
                var mergeWarned = false;
                var tracked = TrackedCommit(target);
                if (tracked != null && !Run("merge-base", "--is-ancestor", tracked, "HEAD").IsSuccess)
                {
                    var integrated = Integrate(strategy, target.TrackingRef, target.Name);
                    if (!integrated.IsDone)
                    {
                        return GitResult.Failure($"{gone}\n{integrated.Message}");
                    }

                    mergeWarned = !integrated.IsSuccess;
                    merged = integrated.Message + (mergeWarned ? "\n" : ", ");
                }

                Run("update-ref", "-d", target.TrackingRef);
                var recreated = Push(tags: tags);
                if (recreated.IsDone)
                {
                    var text = $"{merged}{target.Name} no longer existed on the remote\n{recreated.Message}";
                    return recreated.IsSuccess && !mergeWarned ? GitResult.Success(text) : GitResult.Done(text);
                }

                return NotPushed($"{merged}{gone}", recreated);
            }

            string updated = null;
            var warned = false;
            if (TrackedCommit(target) != null)
            {
                var update = Integrate(strategy, target.TrackingRef, target.Name);
                if (!update.IsDone)
                {
                    return update;
                }

                updated = update.Message;
                warned = !update.IsSuccess;
            }

            var push = Push(tags: tags);
            if (updated == null)
            {
                return push;
            }

            if (push.IsDone)
            {
                var text = $"{updated}\n{push.Message}";
                return push.IsSuccess && !warned ? GitResult.Success(text) : GitResult.Done(text);
            }

            return NotPushed(updated, push, warned);
        }

        private const string UpdateProjectName = "Update Project";

        private sealed class UpstreamSource
        {
            public string Name;
            public string Ref;
            public string Remote;
            public string MergeRef;
            public bool Local;
            public bool NotStored;
            public bool Missing;
            public GitPushTarget? Tracked;
            public string GoneText;
            public string Problem;
        }

        private UpstreamSource ResolveUpstream(string branch)
        {
            var merge = Run("config", "--get", $"branch.{branch}.merge").Output;
            var remote = Run("config", "--get", $"branch.{branch}.remote").Output;
            var source = new UpstreamSource
            {
                Remote = remote, MergeRef = merge, Local = remote == ".",
                GoneText = $"The upstream of {branch} ({merge}) no longer exists, it was probably deleted on the remote"
            };
            var full = Run("rev-parse", "--symbolic-full-name", "@{u}");
            if (full.IsSuccess && full.Output.Length > 0)
            {
                var name = Run("rev-parse", "--abbrev-ref", "@{u}");
                source.Ref = full.Output;
                source.Name = name.IsSuccess && name.Output.Length > 0 ? name.Output : full.Output;
                return source;
            }

            const string branches = "refs/heads/";
            var notStored = full.Message.Contains("not stored as a remote-tracking branch");
            if (merge.Length == 0)
            {
                source.Problem = $"Branch {branch} has no upstream to update from";
            }
            else if (source.Local)
            {
                source.Problem = $"{branch} tracks the local branch {ShortBranch(merge)}, which no longer exists";
            }
            else if (notStored && !merge.StartsWith(branches, StringComparison.Ordinal))
            {
                source.Problem = $"{branch} tracks {merge}, which is not a branch, choose a branch as its upstream";
            }
            else if (notStored && !Run("config", "--get", $"remote.{remote}.url").IsSuccess)
            {
                source.Problem = $"{branch} tracks a remote that is not configured ({remote}), set its upstream again";
            }
            else if (notStored)
            {
                var tracked = new GitPushTarget(remote, merge.Substring(branches.Length), false);
                source.NotStored = true;
                source.Tracked = tracked;
                source.Name = tracked.Name;
                source.Ref = tracked.TrackingRef;
            }
            else
            {
                source.Problem = source.GoneText;
                if (merge.StartsWith(branches, StringComparison.Ordinal) &&
                    Run("config", "--get", $"remote.{remote}.url").IsSuccess)
                {
                    var tracked = new GitPushTarget(remote, merge.Substring(branches.Length), false);
                    source.Missing = true;
                    source.Tracked = tracked;
                    source.Name = tracked.Name;
                    source.Ref = tracked.TrackingRef;
                }
            }

            return source;
        }

        private static string ShortBranch(string reference) =>
            reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? reference.Substring("refs/heads/".Length)
                : reference;

        private GitResult? RebaseRefusal(string target, string name)
        {
            var merges = Run("rev-list", "--merges", "--count", $"{target}..HEAD");
            return merges.IsSuccess && merges.Output != "0"
                ? GitResult.Failure($"Rebase onto {name} is not possible, the local commits include " +
                                    "merge commits that a rebase would flatten, use Merge instead")
                : (GitResult?)null;
        }

        private static GitResult NotPushed(string done, GitResult push, bool onNewLine = false)
        {
            if (push.IsCancelled)
            {
                return GitResult.Cancelled(onNewLine ? $"{done}\nPush cancelled" : $"{done}, push cancelled");
            }

            var failed = onNewLine
                ? $"{done}\nThe push failed\n{push.Message}"
                : $"{done}, but the push failed\n{push.Message}";
            return push.IsRejected ? GitResult.Rejected(failed) : GitResult.Failure(failed);
        }

        private string CheckedOutBranch(string verb, out GitResult refusal)
        {
            var branch = CurrentBranch(out refusal);
            if (branch != "HEAD")
            {
                return branch;
            }

            refusal = GitResult.Failure($"Cannot {verb}: no branch is checked out");
            return null;
        }

        public GitResult Integrate(UpdateStrategy strategy, string target, string name = null)
        {
            name ??= target;
            if (CheckedOutBranch(strategy.ToString().ToLowerInvariant(), out var refusal) == null)
            {
                return refusal;
            }

            var unborn = !HasHead();
            var behind = unborn ? CountCommits(target) : CountCommits($"HEAD..{target}");
            if (behind == 0)
            {
                return GitResult.Success($"Already up to date with {name}");
            }

            if (strategy == UpdateStrategy.Rebase && !unborn)
            {
                var flattened = RebaseRefusal(target, name);
                if (flattened.HasValue)
                {
                    return flattened.Value;
                }
            }

            var incoming = behind ?? 0;
            var done = strategy == UpdateStrategy.Rebase
                ? $"Rebased onto {name}: {Texts.Counted(incoming, "new commit")}"
                : $"Merged {Texts.Counted(incoming, "commit")} from {name}";
            var argument = ShortName(target);

            return WithLocalChangesSaved(new TreeChange
            {
                Description = IntegrationName(strategy, name),
                Target = target,
                ThreeWay = strategy == UpdateStrategy.Merge,
                Done = done,
                Command = strategy == UpdateStrategy.Rebase ? "rebase" : "merge",
                Apply = () => unborn ? Run("merge", "--ff-only", "--no-stat", argument)
                    : strategy == UpdateStrategy.Rebase ? Run("rebase", argument)
                    : Run("merge", "--no-stat", "--no-edit", argument)
            });
        }

        public GitResult CherryPick(string commit) => ApplyCommit(commit, false);

        public GitResult Revert(string commit) => ApplyCommit(commit, true);

        private GitResult ApplyCommit(string commit, bool revert)
        {
            var verb = revert ? "revert" : "cherry-pick";
            var branch = CheckedOutBranch(verb, out var refusal);
            if (branch == null)
            {
                return refusal;
            }

            if (!HasHead())
            {
                return GitResult.Failure($"Cannot {verb}: {branch} has no commits yet");
            }

            var sequencer = GitDirPath("sequencer");
            if (sequencer != null && Directory.Exists(sequencer))
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
                Command = verb,
                Apply = () =>
                {
                    var applied = revert ? Run("revert", "--no-edit", commit) : Run("cherry-pick", commit);
                    return applied.ExitCode == 1 && Conflicts().Count == 0 &&
                           Run("diff", "--cached", "--quiet", "HEAD").IsSuccess
                        ? GitResult.Failure(revert
                            ? $"Its changes are already undone in {branch}"
                            : $"Its changes are already in {branch}")
                        : applied;
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

        private static string IntegrationName(UpdateStrategy strategy, string target) =>
            strategy == UpdateStrategy.Rebase ? $"Rebase onto {target}" : $"Merge of {target}";
    }
}
