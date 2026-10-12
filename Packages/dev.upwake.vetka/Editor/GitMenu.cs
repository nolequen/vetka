using UnityEditor;

namespace Upwake.Vetka
{
    internal static class GitMenu
    {
        [MenuItem("Tools/Git/Update Project", false, 1)]
        public static void UpdateProject()
        {
            var settings = GitProjectSettings.instance;
            if (settings.UpdateStrategy != UpdateStrategyChoice.Ask)
            {
                UpdateProject(new Git(), (UpdateStrategy)settings.UpdateStrategy);
                return;
            }

            UpdateProjectStrategyDialog.Show((strategy, remember) =>
            {
                if (remember)
                {
                    settings.UpdateStrategy = (UpdateStrategyChoice)strategy;
                }

                UpdateProject(new Git(), strategy);
            });
        }

        [MenuItem("Tools/Git/Incoming Changes...", false, 2)]
        public static void OpenIncomingChangesWindow()
        {
            IncomingChangesWindow.ShowWindow();
        }

        private static void UpdateProject(Git git, UpdateStrategy strategy)
        {
            GitIdentityWindow.Ensure(git, () => GitOperations.Run(
                BranchActions.IntegrationTitle(strategy, "the incoming changes"),
                () => git.UpdateProject(strategy),
                Notification.Show,
                changesTree: true,
                reportsProgress: true
            ));
        }

        [MenuItem("Tools/Git/Commit...", false, 3)]
        public static void OpenChangesWindow()
        {
            ChangesWindow.ShowWindow();
        }

        [MenuItem("Tools/Git/Push...", false, 4)]
        public static void OpenPushWindow()
        {
            PushWindow.ShowWindow();
        }

        [MenuItem("Tools/Git/Branches...", false, 4)]
        public static void OpenBranchesWindow()
        {
            BranchesWindow.ShowWindow();
        }

        [MenuItem("Tools/Git/Stash...", false, 5)]
        public static void OpenStashWindow()
        {
            StashWindow.ShowWindow();
        }

        [MenuItem("Tools/Git/Apply Patch...", false, 5)]
        public static void ApplyPatch()
        {
            var path = EditorUtility.OpenFilePanel("Apply patch", "", "patch,diff");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var git = new Git();
            GitOperations.Run("Git: applying a patch", () => git.ApplyPatch(path), Notification.Show,
                changesTree: true);
        }

        [MenuItem("Tools/Git/Log...", false, 5)]
        public static void OpenLogWindow()
        {
            GitLogWindow.ShowWindow();
        }

        [MenuItem("Tools/Git/Settings...", false, 6)]
        public static void OpenSettings()
        {
            SettingsService.OpenUserPreferences(GitSettingsProvider.Path);
        }
    }
}
