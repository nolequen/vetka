using System;
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

        private Action<UpdateStrategy, bool> _onClose;
        private UpdateStrategy _selected;
        private bool _remember;

        public static void Show(Action<UpdateStrategy, bool> callback)
        {
            var window = CreateInstance<UpdateProjectStrategyDialog>();
            window._onClose = callback;
            UtilityDialog.Show(window, "Update Project", new Vector2(350, 100));
        }

        private void OnGUI()
        {
            if (_onClose == null)
            {
                UtilityDialog.Close(this);
                return;
            }

            var current = Event.current;
            var submit = UtilityDialog.Submitted(current);
            var cancel = UtilityDialog.Cancelled(current);

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
                var callback = _onClose;
                var selected = _selected;
                var remember = _remember;
                _onClose = null;
                UtilityDialog.Close(this, () => callback(selected, remember));
            }

            if (GUILayout.Button("Cancel") || cancel)
            {
                _onClose = null;
                UtilityDialog.Close(this);
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}