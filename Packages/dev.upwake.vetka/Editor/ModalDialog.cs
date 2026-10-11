using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class ModalDialog
    {
        public static bool Show(EditorWindow window, string title, Vector2 size, bool fixedSize = true)
        {
            window.titleContent = new GUIContent(title);
            var main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(main.center - size / 2, size);
            window.minSize = size;
            if (fixedSize)
            {
                window.maxSize = size;
            }

            window.ShowModalUtility();
            if (!window)
            {
                return true;
            }

            window.Close();
            return false;
        }

        public static bool Submitted(Event current) =>
            current.type == EventType.KeyDown &&
            (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter);

        public static bool Cancelled(Event current) =>
            current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape;

        public static void Close(EditorWindow window)
        {
            window.Close();
            GUIUtility.ExitGUI();
        }
    }
}
