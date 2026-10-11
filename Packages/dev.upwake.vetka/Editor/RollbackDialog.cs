using System;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class RollbackDialog : EditorWindow
    {
        private string _question;
        private int _added;
        private bool _deleteAdded;
        private Action<bool> _onConfirm;

        private static GUIStyle _paddingStyle;

        private static GUIStyle PaddingStyle => _paddingStyle ??= new GUIStyle
        {
            padding = new RectOffset(10, 10, 8, 10)
        };

        public static void Show(string question, int added, Action<bool> onConfirm)
        {
            var window = CreateInstance<RollbackDialog>();
            window._question = question;
            window._added = added;
            window._onConfirm = onConfirm;
            UtilityDialog.Show(window, "Rollback", new Vector2(380, added > 0 ? 112 : 92));
        }

        private void OnGUI()
        {
            if (_onConfirm == null)
            {
                UtilityDialog.Close(this);
                return;
            }

            var current = Event.current;
            var submit = UtilityDialog.Submitted(current);
            var cancel = UtilityDialog.Cancelled(current);

            using (new EditorGUILayout.VerticalScope(PaddingStyle))
            {
                EditorGUILayout.LabelField(_question, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Local changes in them will be lost.", EditorStyles.wordWrappedLabel);

                if (_added > 0)
                {
                    GUILayout.Space(4);
                    _deleteAdded = EditorGUILayout.ToggleLeft(
                        _added == 1 ? "Delete the added file from disk" : $"Delete {_added} added files from disk",
                        _deleteAdded);
                }

                GUILayout.FlexibleSpace();

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Rollback", GUILayout.Width(90)) || submit)
                    {
                        var callback = _onConfirm;
                        var deleteAdded = _deleteAdded;
                        _onConfirm = null;
                        UtilityDialog.Close(this, () => callback(deleteAdded));
                    }

                    if (GUILayout.Button("Cancel", GUILayout.Width(80)) || cancel)
                    {
                        _onConfirm = null;
                        UtilityDialog.Close(this);
                    }
                }
            }
        }
    }
}
