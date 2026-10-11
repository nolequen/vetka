using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Upwake.Vetka
{
    internal partial class Git
    {
        public GitPushTarget? PushTarget() => PushDestination().Target;

        public (string Branch, GitPushTarget? Target, string Problem) PushDestination()
        {
            var branch = CurrentBranch(out var error);
            if (string.IsNullOrEmpty(branch) || branch == "HEAD")
            {
                return (branch, null, branch == null ? error.Message : "No branch is checked out");
            }

            var target = PushTarget(branch, out var problem);
            return (branch, target, problem);
        }

        private GitPushTarget? PushTarget(string branch, out string problem)
        {
            var chosen = ChooseRemote(branch, out problem);
            if (chosen == null)
            {
                return null;
            }

            var target = new GitPushTarget(chosen, branch, false);
            return new GitPushTarget(chosen, branch, CommitOf(target.TrackingRef) != null);
        }

        public (string Remote, string Problem) TagRemote()
        {
            var branch = CurrentBranch();
            var remote = ChooseRemote(branch == "HEAD" ? null : branch, out var problem);
            return (remote, problem);
        }

        private string ChooseRemote(string branch, out string problem)
        {
            var config = Lines(Run("config", "--get-regexp",
                    @"^(remote\..*\.url|remote\.pushdefault|branch\..*\.(pushremote|remote))$").Output)
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

        public (GitResult Result, string Tracked, string Head, int Count, List<GitCommit> Commits) RemoteOnlyCommits(
            GitPushTarget target, int limit)
        {
            var tracked = TrackedCommit(target);
            var head = HeadCommit();
            if (tracked == null || head == null)
            {
                return (GitResult.Success(""), null, head, 0, new List<GitCommit>());
            }

            var only = CommitsOnlyIn(tracked, head, limit);
            return (only.Result, tracked, head, only.Count, only.Commits);
        }

        public GitResult Push(string forceOver = null, string shownHead = null, PushTags? tags = null)
        {
            var destination = PushDestination();
            return destination.Target.HasValue
                ? PushTo(destination.Target.Value, forceOver, shownHead, tags, out _)
                : GitResult.Failure(destination.Problem);
        }

        private static GitResult InterruptiblePush(Func<GitResult> run)
        {
            var result = OperationContext.Interruptible(run, out _);
            return result.IsCancelled ? GitResult.Cancelled("Push cancelled") : result;
        }

        private GitResult FetchBranch(GitPushTarget target) =>
            Run("fetch", "--progress", "--recurse-submodules=no", "--no-auto-maintenance", target.Remote,
                $"+refs/heads/{target.Branch}:{target.TrackingRef}");

        private static bool BranchMissing(GitResult fetch) =>
            !fetch.IsSuccess && !fetch.IsCancelled && fetch.Message.Contains("couldn't find remote ref");

        private string TrackedCommit(GitPushTarget target) => CommitOf(target.TrackingRef);

        private GitResult PushTo(GitPushTarget target, string forceOver, string shownHead, PushTags? tags,
            out int newTags)
        {
            newTags = 0;
            var count = CountCommits(OutgoingRange(target, "HEAD").ToArray()) ?? 0;
            var force = forceOver != null && target.Exists;
            var removed = 0;
            if (force && shownHead != null && HeadCommit() != shownHead)
            {
                return GitResult.Failure(
                    $"Force push stopped: {target.Branch} has changed since the Push window showed it, look at it again");
            }

            if (force)
            {
                removed = CountCommits($"HEAD..{forceOver}") ?? 0;
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
            var pushed = HeadCommit();
            var result = InterruptiblePush(() => Run(arguments.ToArray()));
            if (result.IsCancelled)
            {
                return result;
            }

            newTags = PushedRefs(result, NewTagLine).Count;
            if (!result.IsSuccess)
            {
                if (force && result.Message.Contains("[rejected]") && result.Message.Contains("stale info"))
                {
                    var fetched = InterruptiblePush(() => FetchBranch(target));
                    if (fetched.IsCancelled)
                    {
                        return fetched;
                    }

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
                var tagsSent = newTags > 0 ? $"Pushed {Texts.Counted(newTags, "tag")} to {target.Remote}" : null;
                var rejected = result.Message.Contains("[rejected]") &&
                               (result.Message.Contains("fetch first") || result.Message.Contains("non-fast-forward"));
                if (rejected && tags.HasValue && count == 0 && target.Exists)
                {
                    if (refused != taken.Count + 1)
                    {
                        return result;
                    }

                    return refusal == null ? GitResult.Success(tagsSent ?? "No new tags to push")
                        : TagsRefused(tagsSent, refusal);
                }

                if (rejected)
                {
                    return GitResult.Rejected($"Push rejected: {target.Name} has commits that {target.Branch} does not have" +
                                              (newTags > 0 ? $". {Texts.Counted(newTags, "tag")} pushed anyway" : ""));
                }

                if (taken.Count == 0 || refused != taken.Count)
                {
                    return result;
                }

                RememberPushed(target, pushed);
                var landed = count > 0 || removed > 0 || !target.Exists;
                return TagsRefused(landed ? PushedText(force, count, removed, newTags, target) : tagsSent, refusal);
            }

            RememberPushed(target, pushed);
            return GitResult.Success(PushedText(force, count, removed, newTags, target));
        }

        private static GitResult TagsRefused(string sent, string refusal) =>
            sent != null ? GitResult.Done($"{sent}, but {refusal}") : GitResult.Failure($"Tags not pushed: {refusal}");

        private void RememberPushed(GitPushTarget target, string pushed)
        {
            if (pushed != null && TrackedCommit(target) != pushed)
            {
                Run("update-ref", target.TrackingRef, pushed);
            }
        }

        private static string PushedText(bool force, int count, int removed, int tags, GitPushTarget target)
        {
            if (force)
            {
                var parts = new List<string>();
                if (count > 0)
                {
                    parts.Add($"{Texts.Counted(count, "commit")} pushed");
                }

                if (removed > 0)
                {
                    parts.Add($"{Texts.Counted(removed, "commit")} removed");
                }

                if (tags > 0)
                {
                    parts.Add($"{Texts.Counted(tags, "tag")} pushed");
                }

                return parts.Count > 0
                    ? $"Force pushed to {target.Name}: {string.Join(", ", parts)}"
                    : $"Force pushed to {target.Name}";
            }

            var sent = string.Join(" and ", new[]
            {
                count > 0 ? Texts.Counted(count, "commit") : null,
                tags > 0 ? Texts.Counted(tags, "tag") : null
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
            Lines(result.Error + "\n" + result.Output)
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

        public string UpstreamBranch() => UpstreamBranch(out _);

        private string UpstreamBranch(out GitResult error)
        {
            error = Run("rev-parse", "--abbrev-ref", "@{u}");
            return error.IsSuccess ? error.Output : null;
        }

        internal int ReportedRefs { get; set; } = 20;

        private const string RemoteBranchRefs = "refs/remotes/";

        private const string TagRefs = "refs/tags/";

        private Dictionary<string, string> FetchedRefs()
        {
            var refs = Run("for-each-ref", "--format=%(refname) %(objectname) %(symref)", RemoteBranchRefs, TagRefs);
            if (!refs.IsSuccess)
            {
                return null;
            }

            return Lines(refs.Output)
                .Select(line => line.Split(' '))
                .Where(fields => fields.Length == 2 || fields.Length == 3 && fields[2].Length == 0)
                .ToDictionary(fields => fields[0], fields => fields[1], StringComparer.Ordinal);
        }

        private string FetchReport(Dictionary<string, string> before, Dictionary<string, string> after,
            string upstream)
        {
            if (before == null || after == null)
            {
                return "";
            }

            int Order(string name)
            {
                var tag = name.StartsWith(TagRefs, StringComparison.Ordinal);
                var kind = !before.ContainsKey(name) ? 1 : !after.ContainsKey(name) ? 2 : 0;
                return name == RemoteBranchRefs + upstream || name == "refs/" + upstream ? 0 : (tag ? 4 : 1) + kind;
            }

            var changed = before.Keys.Union(after.Keys)
                .Where(name => !before.TryGetValue(name, out var old) || !after.TryGetValue(name, out var now) ||
                               old != now)
                .OrderBy(Order)
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToList();
            var lines = changed.Take(ReportedRefs).Select(name => "  " + RefChange(name, before, after)).ToList();
            if (changed.Count > ReportedRefs)
            {
                lines.Add($"  and {changed.Count - ReportedRefs} more");
            }

            return string.Join("\n", lines);
        }

        private string RefChange(string name, Dictionary<string, string> before, Dictionary<string, string> after)
        {
            var tag = name.StartsWith(TagRefs, StringComparison.Ordinal);
            var shown = name.Substring(tag ? TagRefs.Length : RemoteBranchRefs.Length);
            if (!before.TryGetValue(name, out var old))
            {
                return $"{shown}: {(tag ? "new tag" : "new branch")}";
            }

            if (!after.TryGetValue(name, out var now))
            {
                return $"{shown}: {(tag ? "tag deleted, the remote does not have it" : "deleted on the remote")}";
            }

            if (tag)
            {
                return $"{shown}: tag moved";
            }

            var counts = Run("rev-list", "--left-right", "--count", $"{old}...{now}");
            var fields = counts.IsSuccess ? counts.Output.Split('\t') : Array.Empty<string>();
            if (fields.Length != 2 || !int.TryParse(fields[0], out var removed) ||
                !int.TryParse(fields[1], out var added) || removed == 0 && added == 0)
            {
                return $"{shown}: updated";
            }

            return removed == 0 ? $"{shown}: {Texts.Counted(added, "new commit")}"
                : added == 0 ? $"{shown}: {Texts.Counted(removed, "commit")} removed on the remote"
                : $"{shown}: rewritten on the remote, {Texts.Counted(added, "new commit")}, {removed} removed";
        }

        public (GitResult Result, GitIdentity Local, GitIdentity Global) Identities()
        {
            var values = new Dictionary<string, string>();
            var result = Run("config", "--includes", "--show-scope", "--get-regexp", "-z", @"^user\.(name|email)$");
            if (!result.IsSuccess && result.ExitCode != 1)
            {
                return (result, default, default);
            }

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

            return (GitResult.Success(""), Scoped("local"), Scoped("global"));
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
