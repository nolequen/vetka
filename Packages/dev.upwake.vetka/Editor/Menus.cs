using System;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class Menus
    {
        public static void Add(GenericMenu menu, string title, bool enabled, Action action)
        {
            if (enabled)
            {
                menu.AddItem(new GUIContent(title), false, () => EditorApplication.delayCall += () => action());
            }
            else
            {
                menu.AddDisabledItem(new GUIContent(title));
            }
        }
    }
}
