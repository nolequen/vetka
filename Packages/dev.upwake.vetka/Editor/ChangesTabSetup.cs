using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    [InitializeOnLoad]
    internal static class ChangesTabSetup
    {
        private const string CheckedKey = "Vetka.ChangesTab.Checked";

        static ChangesTabSetup()
        {
            if (!Application.isBatchMode && !SessionState.GetBool(CheckedKey, false))
            {
                EditorApplication.delayCall += DockOnce;
            }
        }

        private static void DockOnce()
        {
            SessionState.SetBool(CheckedKey, true);

            if (EditorWindow.HasOpenInstances<ChangesWindow>())
            {
                return;
            }

            if (!BranchTitle.InRepository || ChangesWindow.DockTargets().Length == 0)
            {
                return;
            }

            ChangesWindow.Dock();
        }
    }
}
