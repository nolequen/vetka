using UnityEditor;

namespace Upwake.Vetka
{
    internal static class GitMenu
    {
        [MenuItem("Tools/Git/Update Project", false, 1)]
        public static void UpdateProject()
        {
            var git = new Git();
            var updateStrategy = GitSettings.UpdateStrategyValue;

            if (updateStrategy != UpdateStrategy.Ask)
            {
                UpdateProject(git, updateStrategy);
            }
            else
            {
                UpdateProjectStrategyDialog.Show((selectedStrategy, remember) =>
                {
                    if (remember)
                    {
                        GitSettings.UpdateStrategyValue = selectedStrategy;
                    }

                    UpdateProject(git, selectedStrategy);
                });
            }
        }

        private static void UpdateProject(Git git, UpdateStrategy strategy)
        {
            GitIdentityWindow.Ensure(git, () => GitOperations.Run(
                $"Git: {strategy} of the incoming changes",
                () => git.UpdateProject(strategy),
                Notification.Show,
                refreshAssets: true,
                reportsProgress: true,
                prepare: UnsavedChanges.SaveOrCancel
            ));
        }

        [MenuItem("Tools/Git/Commit...", false, 2)]
        public static void OpenChangesWindow()
        {
            ChangesWindow.ShowWindow(new Git());
        }

        [MenuItem("Tools/Git/Push...", false, 3)]
        public static void OpenPushWindow()
        {
            PushWindow.ShowWindow(new Git());
        }

        [MenuItem("Tools/Git/Branches...", false, 3)]
        public static void OpenBranchesWindow()
        {
            BranchesWindow.ShowWindow();
        }

        [MenuItem("Tools/Git/Stash...", false, 4)]
        public static void OpenStashWindow()
        {
            StashWindow.ShowWindow(new Git());
        }

        [MenuItem("Tools/Git/Apply Patch...", false, 4)]
        public static void ApplyPatch()
        {
            var path = EditorUtility.OpenFilePanel("Apply patch", "", "patch,diff");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var git = new Git();
            GitOperations.Run("Git: applying a patch", () => git.ApplyPatch(path), Notification.Show,
                refreshAssets: true, prepare: UnsavedChanges.SaveOrCancel);
        }

        [MenuItem("Tools/Git/Log...", false, 4)]
        public static void OpenLogWindow()
        {
            GitLogWindow.ShowWindow(new Git());
        }

        [MenuItem("Tools/Git/Settings...", false, 5)]
        public static void OpenSettingsWindow()
        {
            GitSettingsWindow.ShowWindow();
        }
    }
}
