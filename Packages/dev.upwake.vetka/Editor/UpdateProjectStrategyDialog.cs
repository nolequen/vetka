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
        [NonSerialized] private bool _closing;

        public static void Show(Action<UpdateStrategy, bool> callback)
        {
            var window = CreateInstance<UpdateProjectStrategyDialog>();
            window.titleContent = new GUIContent("Update Project");
            window._onClose = callback;

            var size = new Vector2(350, 100);
            var main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(main.center - size / 2, size);
            window.minSize = size;
            window.maxSize = size;

            window.ShowUtility();
            window.Focus();
        }

        private void OnGUI()
        {
            if (_onClose == null)
            {
                CloseLater();
                return;
            }

            var current = Event.current;
            var submit = current.type == EventType.KeyDown &&
                         (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter);
            var cancel = current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape;

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
                EditorApplication.delayCall += () => callback(selected, remember);
                CloseLater();
            }

            if (GUILayout.Button("Cancel") || cancel)
            {
                _onClose = null;
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