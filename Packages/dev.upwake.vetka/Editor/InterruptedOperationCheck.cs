using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    [InitializeOnLoad]
    internal static class InterruptedOperationCheck
    {
        private const string CheckedKey = "Vetka.InterruptedOperation.Checked";

        static InterruptedOperationCheck()
        {
            if (!Application.isBatchMode &&
                (!SessionState.GetBool(CheckedKey, false) || SessionState.GetBool(GitOperations.InterruptedKey, false)))
            {
                EditorApplication.delayCall += Check;
            }
        }

        private static void Check()
        {
            var git = new Git();
            GitOperations.Read("Git: checking for an interrupted operation", git.Interruption,
                result => Report(result.Found, result.Pending));
        }

        private static void CheckLater()
        {
            var due = EditorApplication.timeSinceStartup + 15;

            void Wait()
            {
                if (EditorApplication.timeSinceStartup < due)
                {
                    return;
                }

                EditorApplication.update -= Wait;
                Check();
            }

            EditorApplication.update += Wait;
        }

        private static void Report(GitInterruption? interruption, bool pending)
        {
            SessionState.SetBool(CheckedKey, true);
            SessionState.SetBool(GitOperations.InterruptedKey, false);
            if (!interruption.HasValue)
            {
                if (pending)
                {
                    CheckLater();
                }

                return;
            }

            var found = interruption.Value;
            var time = string.IsNullOrEmpty(found.Time) ? "" : $" at {found.Time}";
            var message = $"{found.Description} was interrupted{time} before it finished.\n\n" +
                          $"Your local changes are saved in {found.Stash} \"{found.StashMessage}\".";
            if (found.InProgress != null)
            {
                message += $"\n\n{Texts.Capitalized(found.InProgress)} " +
                           "is still in progress, finish or abort it before restoring them.";
            }

            var open = EditorUtility.DisplayDialog("Git", message, "Open Stash Window", "Close");
            Git.DismissInterruption(found);

            if (open)
            {
                StashWindow.ShowWindow();
            }
        }
    }
}
