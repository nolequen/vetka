using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class BranchActions
    {
        public static void AddMenuItems(GenericMenu menu, Git git, GitBranch branch, List<GitBranch> branches,
            Action onCompleted)
        {
            var currentName = branches.Find(item => item.IsCurrent).Name;
            var shownName = MenuText(branch.Name);
            var shownCurrent = MenuText(currentName ?? "HEAD");

            AddItem(menu, "Checkout", !branch.IsCurrent, () => Checkout(git, branch, onCompleted));
            AddItem(menu, $"New Branch from '{shownName}'...", true,
                () => NewBranch(git, branch.Name, branch.Ref, onCompleted));
            menu.AddSeparator("");

            var canIntegrate = currentName != null && !branch.IsCurrent;
            AddItem(menu, $"Merge '{shownName}' into '{shownCurrent}'", canIntegrate,
                () => Integrate(git, UpdateStrategy.Merge, branch, onCompleted));
            AddItem(menu, $"Rebase '{shownCurrent}' onto '{shownName}'", canIntegrate,
                () => Integrate(git, UpdateStrategy.Rebase, branch, onCompleted));

            if (!branch.IsRemote)
            {
                menu.AddSeparator("");
                AddItem(menu, "Delete", !branch.IsCurrent, () => Delete(git, branch.Name, onCompleted));
            }
        }

        public static void Checkout(Git git, GitBranch branch, Action onCompleted)
        {
            if (branch.IsRemote)
            {
                Run($"Git: checking out {branch.Name}", () => git.CheckoutRemote(branch.Ref), onCompleted);
            }
            else
            {
                Run($"Git: checking out {branch.Name}", () => git.Checkout(branch.Name), onCompleted);
            }
        }

        public static void NewBranch(Git git, string startName, string startRef, Action onCompleted)
        {
            NewBranchWindow.Show(startName, (name, checkout) =>
            {
                if (checkout)
                {
                    Run($"Git: creating {name}", () => git.CreateBranch(name, startRef, true, startName), onCompleted);
                }
                else
                {
                    GitOperations.Run($"Git: creating {name}", () => git.CreateBranch(name, startRef, false, startName),
                        result => Complete(result, onCompleted));
                }
            });
        }

        public static void Integrate(Git git, UpdateStrategy strategy, GitBranch branch, Action onCompleted)
        {
            GitIdentityWindow.Ensure(git, () =>
                Run($"Git: {strategy} of {branch.Name}", () => git.Integrate(strategy, branch.Ref, branch.Name),
                    onCompleted));
        }

        public static void Delete(Git git, string name, Action onCompleted)
        {
            if (!EditorUtility.DisplayDialog("Delete branch", $"Delete branch '{name}'?", "Delete", "Cancel"))
            {
                return;
            }

            var unmerged = false;
            GitOperations.Run($"Git: deleting {name}", () =>
            {
                var delete = git.DeleteBranch(name, false);
                unmerged = !delete.IsSuccess && git.CanForceDelete(name);
                return delete;
            }, result =>
            {
                if (result.IsSuccess || !unmerged)
                {
                    Complete(result, onCompleted);
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
                    forced => Complete(forced, onCompleted));
            });
        }

        private static void Run(string title, Func<GitResult> work, Action onCompleted)
        {
            GitOperations.Run(title, work, result => Complete(result, onCompleted), refreshAssets: true,
                prepare: UnsavedChanges.SaveOrCancel);
        }

        private static void Complete(GitResult result, Action onCompleted)
        {
            BranchTitle.Refresh();
            onCompleted?.Invoke();
            Notification.Show(result);
        }

        private static void AddItem(GenericMenu menu, string title, bool enabled, GenericMenu.MenuFunction action)
        {
            if (enabled)
            {
                menu.AddItem(new GUIContent(title), false, () => EditorApplication.delayCall += () => action());
            }
            else
            {
                menu.AddDisabledItem(new GUIContent(title));
            }
        }

        private static string MenuText(string name) => name.Replace('/', '\u2215');
    }
}
