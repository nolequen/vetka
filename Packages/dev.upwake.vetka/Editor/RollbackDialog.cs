using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class RollbackDialog : EditorWindow
    {
        private string _question;
        private int _added;
        private bool _deleteAdded;
        private bool? _answer;

        private static GUIStyle _paddingStyle;

        private static GUIStyle PaddingStyle => _paddingStyle ??= new GUIStyle
        {
            padding = new RectOffset(10, 10, 8, 10)
        };

        public static bool? Ask(string question, int added)
        {
            var window = CreateInstance<RollbackDialog>();
            window._question = question;
            window._added = added;
            return ModalDialog.Show(window, "Rollback", new Vector2(380, added > 0 ? 112 : 92)) ? window._answer : null;
        }

        private void OnGUI()
        {
            var current = Event.current;
            var submit = ModalDialog.Submitted(current);
            var cancel = ModalDialog.Cancelled(current);

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
                        _answer = _deleteAdded;
                        ModalDialog.Close(this);
                    }

                    if (GUILayout.Button("Cancel", GUILayout.Width(80)) || cancel)
                    {
                        ModalDialog.Close(this);
                    }
                }
            }
        }
    }
}
