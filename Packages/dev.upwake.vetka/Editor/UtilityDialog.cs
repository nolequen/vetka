using System;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class UtilityDialog
    {
        public static void Show(EditorWindow window, string title, Vector2 size, bool fixedSize = true)
        {
            window.titleContent = new GUIContent(title);
            var main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(main.center - size / 2, size);
            window.minSize = size;
            if (fixedSize)
            {
                window.maxSize = size;
            }

            window.ShowUtility();
            window.Focus();
        }

        public static bool Submitted(Event current) =>
            current.type == EventType.KeyDown &&
            (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter);

        public static bool Cancelled(Event current) =>
            current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape;

        public static void Close(EditorWindow window, Action then = null)
        {
            EditorApplication.delayCall += () =>
            {
                if (window)
                {
                    window.Close();
                }

                then?.Invoke();
            };
        }
    }
}
