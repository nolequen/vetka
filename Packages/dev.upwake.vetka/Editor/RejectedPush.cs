using System;
using UnityEditor;

namespace Upwake.Vetka
{
    internal static class RejectedPush
    {
        public static void Offer(Git git, GitResult rejected, Action<GitResult> completed = null)
        {
            var strategy = Choose(rejected.Message);
            if (!strategy.HasValue)
            {
                return;
            }

            var chosen = strategy.Value;
            GitIdentityWindow.Ensure(git, () => GitOperations.Run(
                $"Git: {chosen} of the incoming changes and push",
                () => git.UpdateAndPush(chosen),
                result =>
                {
                    Notification.Show(result);
                    completed?.Invoke(result);
                },
                refreshAssets: true,
                reportsProgress: true,
                prepare: UnsavedChanges.SaveOrCancel
            ));
        }

        private static UpdateStrategy? Choose(string message)
        {
            var question = $"{message}\n\nUpdate the project with the new commits and push again?";
            var strategy = GitSettings.UpdateStrategyValue;
            if (strategy != UpdateStrategy.Ask)
            {
                return EditorUtility.DisplayDialog("Push rejected", question, $"{strategy} and Push", "Cancel")
                    ? strategy
                    : (UpdateStrategy?)null;
            }

            return EditorUtility.DisplayDialogComplex("Push rejected", question, "Merge and Push", "Cancel",
                    "Rebase and Push") switch
                {
                    0 => UpdateStrategy.Merge,
                    2 => UpdateStrategy.Rebase,
                    _ => null
                };
        }
    }
}
