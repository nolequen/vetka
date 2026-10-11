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
        private (string Name, bool Checkout)? _answer;
        [NonSerialized] private bool _focused;

        public static (string Name, bool Checkout)? Ask(string startPoint)
        {
            var window = CreateInstance<NewBranchWindow>();
            window._startPoint = startPoint;
            return ModalDialog.Show(window, "New Branch", new Vector2(380, 105)) ? window._answer : null;
        }

        private void OnGUI()
        {
            var current = Event.current;
            var submit = ModalDialog.Submitted(current);
            var cancel = ModalDialog.Cancelled(current);

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
                    _answer = (_name.Trim(), _checkout);
                    ModalDialog.Close(this);
                }
            }

            if (GUILayout.Button("Cancel", GUILayout.Width(80)) || cancel)
            {
                ModalDialog.Close(this);
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
