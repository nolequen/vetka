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
        [NonSerialized] private bool _closing;
        [NonSerialized] private GUIStyle _messageStyle;

        public static void Show(string commit, Action<string, string> callback)
        {
            var window = CreateInstance<NewTagWindow>();
            window.titleContent = new GUIContent("New Tag");
            window._commit = commit;
            window._onCreate = callback;

            var size = new Vector2(420, 200);
            var main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(main.center - size / 2, size);
            window.minSize = size;

            window.ShowUtility();
            window.Focus();
        }

        private void OnGUI()
        {
            if (_onCreate == null)
            {
                CloseLater();
                return;
            }

            _messageStyle ??= new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            var current = Event.current;
            var submit = current.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == NameField &&
                         (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter);
            var cancel = current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape;

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
                    EditorApplication.delayCall += () => callback(name, message);
                    CloseLater();
                }
            }

            if (GUILayout.Button("Cancel", GUILayout.Width(80)) || cancel)
            {
                _onCreate = null;
                CloseLater();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void CloseLater()
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            EditorApplication.delayCall += () =>
            {
                if (this != null)
                {
                    Close();
                }
            };
        }
    }
}
