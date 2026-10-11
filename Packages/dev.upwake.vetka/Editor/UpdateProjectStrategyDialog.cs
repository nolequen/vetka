using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class UpdateProjectStrategyDialog : EditorWindow
    {
        private readonly Dictionary<UpdateStrategy, string> _options = new()
        {
            { UpdateStrategy.Merge, "Merge incoming changes into the current branch" },
            { UpdateStrategy.Rebase, "Rebase the current branch on top of the remote branch" }
        };

        private UpdateStrategy _selected;
        private bool _remember;
        private (UpdateStrategy Strategy, bool Remember)? _answer;

        public static (UpdateStrategy Strategy, bool Remember)? Ask()
        {
            var window = CreateInstance<UpdateProjectStrategyDialog>();
            return ModalDialog.Show(window, "Update Project", new Vector2(350, 100)) ? window._answer : null;
        }

        private void OnGUI()
        {
            var current = Event.current;
            var submit = ModalDialog.Submitted(current);
            var cancel = ModalDialog.Cancelled(current);

            foreach (var option in _options)
            {
                if (GUILayout.Toggle(_selected == option.Key, option.Value, "Radio"))
                {
                    _selected = option.Key;
                }
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginHorizontal();
            _remember = EditorGUILayout.ToggleLeft("Don't show again", _remember);

            if (GUILayout.Button("OK") || submit)
            {
                _answer = (_selected, _remember);
                ModalDialog.Close(this);
            }

            if (GUILayout.Button("Cancel") || cancel)
            {
                ModalDialog.Close(this);
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}