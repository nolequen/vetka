using System;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class NewBranchWindow : EditorWindow
    {
        private const string NameField = "GitNewBranchName";

        private string _startPoint;
        private string _name = "";
        private bool _checkout = true;
        private Action<string, bool> _onCreate;
        [NonSerialized] private bool _focused;
        [NonSerialized] private bool _closing;

        public static void Show(string startPoint, Action<string, bool> callback)
        {
            var window = CreateInstance<NewBranchWindow>();
            window.titleContent = new GUIContent("New Branch");
            window._startPoint = startPoint;
            window._onCreate = callback;

            var size = new Vector2(380, 105);
            var main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(main.center - size / 2, size);
            window.minSize = size;
            window.maxSize = size;

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

            var current = Event.current;
            var submit = current.type == EventType.KeyDown &&
                         (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter);
            var cancel = current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape;

            EditorGUILayout.LabelField($"From {_startPoint}", EditorStyles.wordWrappedLabel);

            GUI.SetNextControlName(NameField);
            _name = EditorGUILayout.TextField("Name", _name);
            _checkout = EditorGUILayout.ToggleLeft("Checkout branch", _checkout);

            if (!_focused)
            {
                EditorGUI.FocusTextInControl(NameField);
                _focused = true;
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            var valid = !string.IsNullOrWhiteSpace(_name);
            using (new EditorGUI.DisabledScope(!valid))
            {
                if (GUILayout.Button("Create", GUILayout.Width(80)) || submit && valid)
                {
                    var callback = _onCreate;
                    var name = _name.Trim();
                    var checkout = _checkout;
                    _onCreate = null;
                    EditorApplication.delayCall += () => callback(name, checkout);
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
