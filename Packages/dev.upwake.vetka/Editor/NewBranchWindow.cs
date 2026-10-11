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

        public static void Show(string startPoint, Action<string, bool> callback)
        {
            var window = CreateInstance<NewBranchWindow>();
            window._startPoint = startPoint;
            window._onCreate = callback;
            UtilityDialog.Show(window, "New Branch", new Vector2(380, 105));
        }

        private void OnGUI()
        {
            if (_onCreate == null)
            {
                UtilityDialog.Close(this);
                return;
            }

            var current = Event.current;
            var submit = UtilityDialog.Submitted(current);
            var cancel = UtilityDialog.Cancelled(current);

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
                    UtilityDialog.Close(this, () => callback(name, checkout));
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
