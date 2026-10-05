using UnityEditor;
using UnityEditor.SceneManagement;

namespace Upwake.Vetka
{
    internal static class UnsavedChanges
    {
        public static bool SaveOrCancel()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Git", "This is not available in Play Mode, exit Play Mode and try again",
                    "OK");
                return false;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return false;
            }

            AssetDatabase.SaveAssets();
            return true;
        }
    }
}
