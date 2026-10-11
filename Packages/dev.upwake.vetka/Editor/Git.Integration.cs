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
            var upstream = UpstreamBranch(out var upstreamError);
            if (string.IsNullOrEmpty(upstream))
            {
                var merge = Run("config", "--get", $"branch.{branch}.merge");
                var remote = Run("config", "--get", $"branch.{branch}.remote");
                if (upstreamError.Message.Contains("not stored as a remote-tracking branch") &&
                    merge.Output.StartsWith("refs/heads/", StringComparison.Ordinal) &&
                    Run("config", "--get", $"remote.{remote.Output}.url").IsSuccess)
                {
                    var tracked = new GitPushTarget(remote.Output, merge.Output.Substring("refs/heads/".Length), false);
                    var fetched = Interruptible("Update", () => FetchBranch(tracked));
                    if (fetched.IsCancelled || !fetched.IsSuccess && !BranchMissing(fetched))
                    {
                        return fetched;
                    }

                    if (fetched.IsSuccess)
                    {
                        upstream = tracked.Name;
                        after = before == null ? null : FetchedRefs();
                    }
                    else
                    {
                        Run("update-ref", "-d", tracked.TrackingRef);
                    }
                }

                if (string.IsNullOrEmpty(upstream))
                {
                    return GitResult.Failure(merge.IsSuccess && merge.Output.Length > 0
                        ? $"The upstream of {branch} ({merge.Output}) no longer exists, it was probably deleted on the remote"
                        : $"Branch {branch} has no upstream to update from");
                }
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
                var merges = Run("rev-list", "--merges", "--count", $"{target}..HEAD");
                if (merges.IsSuccess && merges.Output != "0")
                {
                    return GitResult.Failure($"Rebase onto {name} is not possible, the local commits include " +
                                             "merge commits that a rebase would flatten, use Merge instead");
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
