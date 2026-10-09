using System;
using System.Collections.Generic;
using System.Linq;

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
            var configured = new[] { $"branch.{branch}.pushremote", "remote.pushdefault", $"branch.{branch}.remote" }
                .Select(Setting)
                .FirstOrDefault(remote => !string.IsNullOrEmpty(remote) && remotes.Contains(remote));
            var chosen = configured ?? (remotes.Contains("origin") ? "origin" : remotes.Count == 1 ? remotes[0] : null);
            if (chosen == null)
            {
                problem = remotes.Count == 0
                    ? "This repository has no remote to push to"
                    : "Cannot choose a remote to push to, set remote.pushDefault";
                return null;
            }

            problem = null;
            var exists = Run("rev-parse", "-q", "--verify", $"refs/remotes/{chosen}/{branch}").IsSuccess;
            return new GitPushTarget(chosen, branch, exists);
        }

        public GitResult Push()
        {
            var target = PushTarget();
            var before = target.HasValue ? TrackedCommit(target.Value) : null;
            var result = Interruptible("Push", PushHead);
            if (!result.IsCancelled || !target.HasValue)
            {
                return result;
            }

            var after = TrackedCommit(target.Value);
            return after != null && after != before && after == HeadCommit()
                ? GitResult.Success($"Pushed to {target.Value.Name}")
                : result;
        }

        private static readonly Dictionary<string, string> LfsProgress =
            new Dictionary<string, string> { ["GIT_LFS_FORCE_PROGRESS"] = "1" };

        private string TrackedCommit(GitPushTarget target)
        {
            var tracked = Run("rev-parse", "-q", "--verify", target.TrackingRef);
            return tracked.IsSuccess ? tracked.Output : null;
        }

        private GitResult PushHead()
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

            var arguments = new List<string> { "push", "--progress" };
            if (string.IsNullOrEmpty(UpstreamBranch()))
            {
                arguments.Add("--set-upstream");
            }

            arguments.Add(target.Remote);
            arguments.Add("HEAD");
            var result = RunWithProgress(LfsProgress, arguments.ToArray());
            if (!result.IsSuccess)
            {
                var rejected = result.Message.Contains("[rejected]") &&
                               (result.Message.Contains("fetch first") || result.Message.Contains("non-fast-forward"));
                return rejected
                    ? GitResult.Failure("Push rejected: the remote has new commits, update the project first")
                    : result;
            }

            if (!target.Exists)
            {
                return GitResult.Success(count > 0
                    ? $"Pushed {Counted(count, "commit")} to new branch {target.Name}"
                    : $"Pushed to new branch {target.Name}");
            }

            return GitResult.Success(count > 0
                ? $"Pushed {Counted(count, "commit")} to {target.Name}"
                : "Everything is up to date");
        }

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
