using System;
using UnityEditor;

namespace Upwake.Vetka
{
    internal static class RejectedPush
    {
        public static void Report(Git git, GitResult result, Action<GitResult> completed = null,
            PushTags? tags = null, Action stopped = null)
        {
            if (result.IsRejected)
            {
                Offer(git, result, completed, tags, stopped);
            }
            else
            {
                Notification.Show(result);
            }
        }

        public static void Offer(Git git, GitResult rejected, Action<GitResult> completed = null,
            PushTags? tags = null, Action stopped = null)
        {
            var strategy = Choose(rejected.Message);
            if (!strategy.HasValue)
            {
                stopped?.Invoke();
                return;
            }

            var chosen = strategy.Value;
            GitIdentityWindow.Ensure(git, () => GitOperations.Run(
                BranchActions.IntegrationTitle(chosen, "the incoming changes") + " and pushing",
                () => git.UpdateAndPush(chosen, tags),
                result =>
                {
                    Notification.Show(result);
                    completed?.Invoke(result);
                },
                changesTree: true,
                reportsProgress: true
            ), stopped);
        }

        private static UpdateStrategy? Choose(string message)
        {
            var question = $"{message}\n\nBring those commits into your branch and push again?";
            var strategy = GitProjectSettings.instance.UpdateStrategy;
            if (strategy != UpdateStrategyChoice.Ask)
            {
                return EditorUtility.DisplayDialog("Push rejected", question, $"{strategy} and Push", "Cancel")
                    ? (UpdateStrategy)strategy
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
