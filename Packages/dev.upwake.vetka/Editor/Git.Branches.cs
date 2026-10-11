using System;
using System.Collections.Generic;
using System.Linq;

namespace Upwake.Vetka
{
    internal partial class Git
    {
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

            var behind = CountCommits($"refs/heads/{local}..{remoteRef}") ?? 0;
            var advice = upstream == remoteRef
                ? "update the project to get them"
                : $"merge {remoteBranch} into it to get them";
            var note = behind > 0 ? $"\n{local} is {Texts.Counted(behind, "commit")} behind {remoteBranch}, {advice}" : "";
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
            if (delete.IsSuccess)
            {
                return GitResult.Success($"Branch {name} deleted");
            }

            return !force && delete.Message.Contains("is not fully merged")
                ? GitResult.Rejected($"Branch {name} is not merged into the current branch or its upstream")
                : delete;
        }

        private GitResult Switch(string description, string done, string target, string expectedBranch,
            string createdBranch, params string[] arguments)
        {
            var switchArguments = new[] { "switch" }.Concat(arguments).ToArray();
            return WithLocalChangesSaved(new TreeChange
            {
                Description = description,
                Target = target,
                Done = done,
                ExpectedBranch = expectedBranch,
                CreatedBranch = createdBranch,
                Apply = () => Run(switchArguments)
            });
        }

        public (GitResult Result, List<GitBranch> Branches) ReadBranches(bool track = true)
        {
            var result = Run("for-each-ref",
                $"--format=%(refname)%1f%(refname:lstrip=2)%1f%(upstream:short)%1f{(track ? "%(upstream:track)" : "")}%1f%(HEAD)",
                "refs/heads", "refs/remotes");
            if (!result.IsSuccess)
            {
                return (result, new List<GitBranch>());
            }

            return (result, Lines(result.Output)
                .Select(line => line.Split(new[] { LogFieldSeparator }, StringSplitOptions.None))
                .Where(fields => fields.Length == 5)
                .Where(fields => !(fields[0].StartsWith("refs/remotes/") && fields[0].EndsWith("/HEAD")))
                .Select(fields => new GitBranch(fields[1], fields[0].StartsWith("refs/remotes/"),
                    fields[4].Trim() == "*", fields[2], fields[3], fields[0]))
                .ToList());
        }

        public List<string> RecentBranches()
        {
            var result = Run("reflog", "--format=%gs", "-n", "300");
            var recent = new List<string>();
            if (!result.IsSuccess)
            {
                return recent;
            }

            const string prefix = "checkout: moving from ";
            foreach (var line in Lines(result.Output))
            {
                if (!line.StartsWith(prefix))
                {
                    continue;
                }

                var move = line.Substring(prefix.Length);
                var separator = move.LastIndexOf(" to ", StringComparison.Ordinal);
                if (separator < 0)
                {
                    continue;
                }

                foreach (var name in new[] { move.Substring(separator + " to ".Length), move.Substring(0, separator) })
                {
                    if (!recent.Contains(name))
                    {
                        recent.Add(name);
                    }
                }
            }

            return recent;
        }
    }
}
