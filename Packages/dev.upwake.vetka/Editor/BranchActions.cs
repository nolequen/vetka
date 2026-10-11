using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class BranchActions
    {
        public static void AddMenuItems(GenericMenu menu, Git git, GitBranch branch, List<GitBranch> branches)
        {
            var currentName = branches.Find(item => item.IsCurrent).Name;
            var shownName = Texts.MenuLabel(branch.Name);
            var shownCurrent = Texts.MenuLabel(currentName ?? "HEAD");

            Menus.Add(menu, "Checkout", !branch.IsCurrent, () => Checkout(git, branch));
            Menus.Add(menu, $"New Branch from '{shownName}'...", true,
                () => NewBranch(git, branch.Name, branch.Ref));
            Menus.Add(menu, $"Compare with '{shownCurrent}'", !branch.IsCurrent,
                () => CompareBranchWindow.ShowWindow(branch.Ref, branch.Name));
            menu.AddSeparator("");

            var canIntegrate = currentName != null && !branch.IsCurrent;
            Menus.Add(menu, $"Merge '{shownName}' into '{shownCurrent}'", canIntegrate,
                () => Integrate(git, UpdateStrategy.Merge, branch));
            Menus.Add(menu, $"Rebase '{shownCurrent}' onto '{shownName}'", canIntegrate,
                () => Integrate(git, UpdateStrategy.Rebase, branch));

            if (!branch.IsRemote)
            {
                menu.AddSeparator("");
                Menus.Add(menu, "Delete", !branch.IsCurrent, () => Delete(git, branch.Name));
            }
        }

        public static void Checkout(Git git, GitBranch branch)
        {
            if (branch.IsRemote)
            {
                Run($"Git: checking out {branch.Name}", () => git.CheckoutRemote(branch.Ref));
            }
            else
            {
                Run($"Git: checking out {branch.Name}", () => git.Checkout(branch.Name));
            }
        }

        public static void NewBranch(Git git, string startName, string startRef)
        {
            var answer = NewBranchWindow.Ask(startName);
            if (answer == null)
            {
                return;
            }

            var (name, checkout) = answer.Value;
            if (checkout)
            {
                Run($"Git: creating {name}", () => git.CreateBranch(name, startRef, true, startName));
            }
            else
            {
                GitOperations.Run($"Git: creating {name}", () => git.CreateBranch(name, startRef, false, startName),
                    Notification.Show);
            }
        }

        public static void Integrate(Git git, UpdateStrategy strategy, GitBranch branch)
        {
            GitIdentityWindow.Ensure(git, () =>
                Run(IntegrationTitle(strategy, branch.Name), () => git.Integrate(strategy, branch.Ref, branch.Name)));
        }

        public static string IntegrationTitle(UpdateStrategy strategy, string target) =>
            strategy == UpdateStrategy.Rebase ? $"Git: rebasing onto {target}" : $"Git: merging {target}";

        public static void Delete(Git git, string name)
        {
            if (!EditorUtility.DisplayDialog("Delete branch", $"Delete branch '{name}'?", "Delete", "Cancel"))
            {
                return;
            }

            GitOperations.Run($"Git: deleting {name}", () => git.DeleteBranch(name, false), result =>
            {
                if (!result.IsRejected)
                {
                    Notification.Show(result);
                    return;
                }

                if (!EditorUtility.DisplayDialog("Delete branch",
                        $"Branch '{name}' is not merged into the current branch or its upstream.\n\n" +
                        "Delete it anyway? Its commits that are not merged anywhere will be lost.",
                        "Force Delete", "Cancel"))
                {
                    return;
                }

                GitOperations.Run($"Git: deleting {name}", () => git.DeleteBranch(name, true),
                    Notification.Show);
            });
        }

        private static void Run(string title, Func<GitResult> work)
        {
            GitOperations.Run(title, work, Notification.Show, changesTree: true);
        }
    }
}
