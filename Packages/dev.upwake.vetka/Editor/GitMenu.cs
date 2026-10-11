using UnityEditor;

namespace Upwake.Vetka
{
    internal static class GitMenu
    {
        [MenuItem("Tools/Git/Update Project", false, 1)]
        public static void UpdateProject()
        {
            var settings = GitProjectSettings.instance;
            var strategy = (UpdateStrategy)settings.UpdateStrategy;
            if (settings.UpdateStrategy == UpdateStrategyChoice.Ask)
            {
                var answer = UpdateProjectStrategyDialog.Ask();
                if (answer == null)
                {
                    return;
                }

                strategy = answer.Value.Strategy;
                if (answer.Value.Remember)
                {
                    settings.UpdateStrategy = (UpdateStrategyChoice)strategy;
                }
            }

            UpdateProject(new Git(), strategy);
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

        [MenuItem("Tools/Git/Commit...", false, 2)]
        public static void OpenChangesWindow()
        {
            ChangesWindow.ShowWindow();
        }

        [MenuItem("Tools/Git/Push...", false, 3)]
        public static void OpenPushWindow()
        {
            PushWindow.ShowWindow();
        }

        [MenuItem("Tools/Git/Branches...", false, 3)]
        public static void OpenBranchesWindow()
        {
            BranchesWindow.ShowWindow();
        }

        [MenuItem("Tools/Git/Stash...", false, 4)]
        public static void OpenStashWindow()
        {
            StashWindow.ShowWindow();
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
                changesTree: true);
        }

        [MenuItem("Tools/Git/Log...", false, 4)]
        public static void OpenLogWindow()
        {
            GitLogWindow.ShowWindow();
        }

        [MenuItem("Tools/Git/Settings...", false, 5)]
        public static void OpenSettings()
        {
            SettingsService.OpenUserPreferences(GitSettingsProvider.Path);
        }
    }
}
