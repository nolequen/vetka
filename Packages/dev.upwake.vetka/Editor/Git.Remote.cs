using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Upwake.Vetka
{
    internal partial class Git
    {
        public GitPushTarget? PushTarget() => PushDestination().Target;

        public (GitPushTarget? Target, string Problem) PushDestination()
        {
            var branch = CurrentBranch(out var error);
            if (string.IsNullOrEmpty(branch) || branch == "HEAD")
            {
                return (null, branch == null ? error.Message : "No branch is checked out");
            }

            var target = PushTarget(branch, out var problem);
            return (target, problem);
        }

        private GitPushTarget? PushTarget(string branch, out string problem)
        {
            var chosen = ChooseRemote(branch, out problem);
            if (chosen == null)
            {
                return null;
            }

            var exists = Run("rev-parse", "-q", "--verify", $"refs/remotes/{chosen}/{branch}").IsSuccess;
            return new GitPushTarget(chosen, branch, exists);
        }

        public string TagRemote(out string problem)
        {
            var branch = CurrentBranch();
            return ChooseRemote(branch == "HEAD" ? null : branch, out problem);
        }

        private string ChooseRemote(string branch, out string problem)
        {
            var config = Run("config", "--get-regexp", @"^(remote\..*\.url|remote\.pushdefault|branch\..*\.(pushremote|remote))$")
                .Output
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(new[] { ' ' }, 2))
                .Where(pair => pair.Length == 2)
                .ToList();
            var remotes = config
                .Where(pair => pair[0].StartsWith("remote.") && pair[0].EndsWith(".url"))
                .Select(pair => pair[0].Substring("remote.".Length, pair[0].Length - "remote.".Length - ".url".Length))
                .Distinct()
                .ToList();
            string Setting(string key) => config.LastOrDefault(pair => pair[0] == key)?[1];
            var keys = branch == null
                ? new[] { "remote.pushdefault" }
                : new[] { $"branch.{branch}.pushremote", "remote.pushdefault", $"branch.{branch}.remote" };
            var configured = keys
                .Select(Setting)
                .FirstOrDefault(remote => !string.IsNullOrEmpty(remote) && remotes.Contains(remote));
            var chosen = configured ?? (remotes.Contains("origin") ? "origin" : remotes.Count == 1 ? remotes[0] : null);
            problem = chosen != null ? null
                : remotes.Count == 0 ? "This repository has no remote to push to"
                : "Cannot choose a remote to push to, set remote.pushDefault";
            return chosen;
        }

        public (string Tracked, string Head, int Count, List<string> Commits) RemoteOnlyCommits(GitPushTarget target,
            int limit = 50)
        {
            var tracked = TrackedCommit(target);
            var head = HeadCommit();
            if (tracked == null || head == null)
            {
                return (null, head, 0, new List<string>());
            }

            var count = Run("rev-list", "--count", $"{head}..{tracked}");
            var log = Run("log", $"{head}..{tracked}", "--pretty=format:%h %s", "-n", limit.ToString());
            return (tracked, head,
                count.IsSuccess && int.TryParse(count.Output, out var parsed) ? parsed : 0,
                !log.IsSuccess || string.IsNullOrWhiteSpace(log.Output)
                    ? new List<string>()
                    : log.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList());
        }

        public GitResult Push(string forceOver = null, string shownHead = null, PushTags? tags = null)
        {
            var target = PushTarget();
            var before = target.HasValue ? TrackedCommit(target.Value) : null;
            var result = Interruptible("Push", () => PushHead(forceOver, shownHead, tags));
            if (!result.IsCancelled || !target.HasValue)
            {
                return result;
            }

            var after = TrackedCommit(target.Value);
            return after != null && after != before && after == HeadCommit()
                ? GitResult.Success($"Pushed to {target.Value.Name}")
                : result;
        }

        private GitResult FetchBranch(GitPushTarget target) =>
            RunWithProgress("fetch", "--progress", "--recurse-submodules=no", "--no-auto-maintenance", target.Remote,
                $"+refs/heads/{target.Branch}:{target.TrackingRef}");

        private static bool BranchMissing(GitResult fetch) =>
            !fetch.IsSuccess && !fetch.IsCancelled && fetch.Message.Contains("couldn't find remote ref");

        private static readonly Dictionary<string, string> LfsProgress =
            new Dictionary<string, string> { ["GIT_LFS_FORCE_PROGRESS"] = "1" };

        private string TrackedCommit(GitPushTarget target)
        {
            var tracked = Run("rev-parse", "-q", "--verify", target.TrackingRef);
            return tracked.IsSuccess ? tracked.Output : null;
        }

        private GitResult PushHead(string forceOver, string shownHead, PushTags? tags)
        {
            var branch = CurrentBranch(out var branchError);
            if (branch == null)
            {
                return branchError;
            }

            if (branch == "HEAD")
            {
                return GitResult.Failure("Cannot push: no branch is checked out");
            }

            var found = PushTarget(branch, out var problem);
            if (!found.HasValue)
            {
                return GitResult.Failure(problem);
            }

            var target = found.Value;
            var outgoing = Run(new[] { "rev-list", "--count" }.Concat(OutgoingRange(target, "HEAD")).ToArray());
            var count = outgoing.IsSuccess && int.TryParse(outgoing.Output, out var parsed) ? parsed : 0;
            var force = forceOver != null && target.Exists;
            var removed = 0;
            if (force && shownHead != null && HeadCommit() != shownHead)
            {
                return GitResult.Failure(
                    $"Force push stopped: {branch} has changed since the Push window showed it, look at it again");
            }

            if (force)
            {
                var lost = Run("rev-list", "--count", $"HEAD..{forceOver}");
                removed = lost.IsSuccess && int.TryParse(lost.Output, out var dropped) ? dropped : 0;
            }

            var arguments = new List<string> { "push", "--progress" };
            if (force)
            {
                arguments.Add($"--force-with-lease=refs/heads/{target.Branch}:{forceOver}");
            }

            if (tags.HasValue)
            {
                arguments.Add(tags.Value == PushTags.All ? "--tags" : "--follow-tags");
            }

            if (string.IsNullOrEmpty(UpstreamBranch()))
            {
                arguments.Add("--set-upstream");
            }

            arguments.Add(target.Remote);
            arguments.Add("HEAD");
            var result = RunWithProgress(LfsProgress, arguments.ToArray());
            var newTags = PushedRefs(result, NewTagLine).Count;
            if (!result.IsSuccess)
            {
                if (force && result.Message.Contains("[rejected]") && result.Message.Contains("stale info"))
                {
                    var fetched = FetchBranch(target);
                    if (BranchMissing(fetched))
                    {
                        Run("update-ref", "-d", target.TrackingRef);
                        return GitResult.Failure(
                            $"Force push stopped: {target.Name} no longer exists on the remote, push again to create it");
                    }

                    return GitResult.Failure(fetched.IsSuccess
                        ? $"Force push rejected: {target.Name} has changed on the remote since the Push window showed " +
                          "it. The window now shows its current state, check it and try again"
                        : $"Force push rejected: {target.Name} has changed on the remote, and fetching it to show " +
                          $"the changes failed\n{fetched.Message}");
                }

                var taken = PushedRefs(result, ExistingTagLine);
                var refused = PushedRefs(result, RefusedLine).Count;
                var refusal = taken.Count == 0
                    ? null
                    : $"{target.Remote} already has {(taken.Count == 1 ? "tag" : "tags")} {string.Join(", ", taken)} " +
                      $"on {(taken.Count == 1 ? "another commit" : "other commits")}";
                var tagsSent = newTags > 0 ? $"Pushed {Counted(newTags, "tag")} to {target.Remote}" : null;
                var rejected = result.Message.Contains("[rejected]") &&
                               (result.Message.Contains("fetch first") || result.Message.Contains("non-fast-forward"));
                if (rejected && tags.HasValue && count == 0 && target.Exists)
                {
                    if (refused != taken.Count + 1)
                    {
                        return result;
                    }

                    return refusal == null ? GitResult.Success(tagsSent ?? "No new tags to push")
                        : GitResult.Failure(tagsSent != null ? $"{tagsSent}, but {refusal}" : $"Tags not pushed: {refusal}");
                }

                if (rejected)
                {
                    return GitResult.Rejected($"Push rejected: {target.Name} has commits that {branch} does not have" +
                                              (newTags > 0 ? $". {Counted(newTags, "tag")} pushed anyway" : ""));
                }

                if (taken.Count == 0 || refused != taken.Count)
                {
                    return result;
                }

                var landed = TrackedCommit(target) == HeadCommit() && (count > 0 || removed > 0 || !target.Exists);
                var sent = landed ? PushedText(force, count, removed, newTags, target) : tagsSent;
                return GitResult.Failure(sent != null ? $"{sent}, but {refusal}" : $"Tags not pushed: {refusal}");
            }

            return GitResult.Success(PushedText(force, count, removed, newTags, target));
        }

        private static string PushedText(bool force, int count, int removed, int tags, GitPushTarget target)
        {
            if (force)
            {
                var parts = new List<string>();
                if (count > 0)
                {
                    parts.Add($"{Counted(count, "commit")} pushed");
                }

                if (removed > 0)
                {
                    parts.Add($"{Counted(removed, "commit")} removed");
                }

                if (tags > 0)
                {
                    parts.Add($"{Counted(tags, "tag")} pushed");
                }

                return parts.Count > 0
                    ? $"Force pushed to {target.Name}: {string.Join(", ", parts)}"
                    : $"Force pushed to {target.Name}";
            }

            var sent = string.Join(" and ", new[]
            {
                count > 0 ? Counted(count, "commit") : null,
                tags > 0 ? Counted(tags, "tag") : null
            }.Where(part => part != null));
            if (!target.Exists)
            {
                return sent.Length > 0 ? $"Pushed {sent} to new branch {target.Name}" : $"Pushed to new branch {target.Name}";
            }

            return count > 0 ? $"Pushed {sent} to {target.Name}"
                : tags > 0 ? $"Pushed {sent} to {target.Remote}"
                : "Everything is up to date";
        }

        private static readonly Regex NewTagLine = new Regex(@"^\s*\*\s+\[new tag\]\s+\S+\s+->\s+(\S+)\s*$");

        private static readonly Regex ExistingTagLine =
            new Regex(@"^\s*!\s+\[rejected\]\s+\S+\s+->\s+(\S+)\s+\(already exists\)\s*$");

        private static readonly Regex RefusedLine = new Regex(@"^\s*!\s+\[([^\]]+)\]");

        private static List<string> PushedRefs(GitResult result, Regex line) =>
            (result.Error + "\n" + result.Output)
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(text => line.Match(text))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .ToList();

        public string CurrentBranch() => CurrentBranch(out _);

        public string CurrentBranch(out GitResult error)
        {
            const string branches = "refs/heads/";
            var head = Run("symbolic-ref", "-q", "HEAD");
            error = head;
            if (head.IsSuccess)
            {
                return head.Output.StartsWith(branches) ? head.Output.Substring(branches.Length) : head.Output;
            }

            return head.ExitCode == 1 ? "HEAD" : null;
        }

        public string UpstreamBranch()
        {
            var result = Run("rev-parse", "--abbrev-ref", "@{u}");
            return result.IsSuccess ? result.Output : null;
        }

        public GitIdentity Identity(bool global)
        {
            var identities = Identities();
            return global ? identities.Global : identities.Local;
        }

        public (GitIdentity Local, GitIdentity Global) Identities()
        {
            var values = new Dictionary<string, string>();
            var result = Run("config", "--includes", "--show-scope", "--get-regexp", "-z", @"^user\.(name|email)$");
            if (result.IsSuccess)
            {
                var tokens = result.Output.Split('\0');
                for (var i = 0; i + 1 < tokens.Length; i += 2)
                {
                    var entry = tokens[i + 1];
                    var newline = entry.IndexOf('\n');
                    var key = newline < 0 ? entry : entry.Substring(0, newline);
                    values[tokens[i] + " " + key] = newline < 0 ? "" : entry.Substring(newline + 1);
                }
            }

            GitIdentity Scoped(string scope) => new GitIdentity(
                values.TryGetValue(scope + " user.name", out var name) ? name : "",
                values.TryGetValue(scope + " user.email", out var email) ? email : "");

            return (Scoped("local"), Scoped("global"));
        }

        public GitResult SetIdentity(GitIdentity identity, bool global, GitIdentity current = default)
        {
            if (!identity.IsComplete)
            {
                return GitResult.Failure("Name and email are required");
            }

            var scope = global ? "--global" : "--local";
            if (identity.Name != (current.Name ?? ""))
            {
                var name = Run("config", scope, "user.name", identity.Name);
                if (!name.IsSuccess)
                {
                    return name;
                }
            }

            return identity.Email != (current.Email ?? "")
                ? Run("config", scope, "user.email", identity.Email)
                : GitResult.Success("");
        }
    }
}
