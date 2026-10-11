using System;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class NewTagWindow : EditorWindow
    {
        private const string NameField = "GitNewTagName";

        private string _commit;
        private string _name = "";
        private string _message = "";
        private Action<string, string> _onCreate;
        [NonSerialized] private bool _focused;
        [NonSerialized] private GUIStyle _messageStyle;

        public static void Show(string commit, Action<string, string> callback)
        {
            var window = CreateInstance<NewTagWindow>();
            window._commit = commit;
            window._onCreate = callback;
            UtilityDialog.Show(window, "New Tag", new Vector2(420, 200), fixedSize: false);
        }

        private void OnGUI()
        {
            if (_onCreate == null)
            {
                UtilityDialog.Close(this);
                return;
            }

            _messageStyle ??= new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            var current = Event.current;
            var submit = GUI.GetNameOfFocusedControl() == NameField && UtilityDialog.Submitted(current);
            var cancel = UtilityDialog.Cancelled(current);

            EditorGUILayout.LabelField($"On {_commit}", EditorStyles.wordWrappedLabel);

            GUI.SetNextControlName(NameField);
            _name = EditorGUILayout.TextField("Name", _name);
            EditorGUILayout.LabelField("Message (optional, makes an annotated tag)");
            _message = EditorGUILayout.TextArea(_message, _messageStyle, GUILayout.ExpandHeight(true));

            if (!_focused)
            {
                EditorGUI.FocusTextInControl(NameField);
                _focused = true;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            var valid = !string.IsNullOrWhiteSpace(_name);
            using (new EditorGUI.DisabledScope(!valid))
            {
                if (GUILayout.Button("Create", GUILayout.Width(80)) || submit && valid)
                {
                    var callback = _onCreate;
                    var name = _name.Trim();
                    var message = _message;
                    _onCreate = null;
                    UtilityDialog.Close(this, () => callback(name, message));
                }
            }

            if (GUILayout.Button("Cancel", GUILayout.Width(80)) || cancel)
            {
                _onCreate = null;
                UtilityDialog.Close(this);
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
