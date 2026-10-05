using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class StashWindow : EditorWindow
    {
        [NonSerialized] private Git _git;
        private List<GitStash> _stashes;
        private string _selected;
        private string _message = "";
        private bool _includeUntracked;
        private Vector2 _scrollPos;
        [NonSerialized] private bool _loading;
        [NonSerialized] private string _aborted;

        private static GUIStyle _hintStyle;

        private static GUIStyle HintStyle => _hintStyle ??= new GUIStyle(GUI.skin.label)
        {
            normal = { textColor = Color.gray },
            padding = new RectOffset(4, 0, 0, 0)
        };

        public static void ShowWindow(Git git)
        {
            var window = GetWindow<StashWindow>(utility: false, "Git stash");
            window._git = git;
            window.minSize = new Vector2(460, 280);
            window.Refresh();
        }

        private void OnEnable() => GitOperations.RepositoryChanged += Refresh;

        private void OnDisable() => GitOperations.RepositoryChanged -= Refresh;

        private void OnFocus() => Refresh();

        private void Refresh()
        {
            if (_loading)
            {
                return;
            }

            _git ??= new Git();
            _loading = true;
            _aborted = null;

            var git = _git;
            GitOperations.Read("Git: reading stashes", () => git.StashList(), list =>
            {
                if (!this)
                {
                    return;
                }

                if (!list.Result.IsSuccess)
                {
                    _loading = false;
                    _stashes = null;
                    _aborted = list.Result.Message;
                    Repaint();
                    return;
                }

                _stashes = list.Stashes;
                if (!_stashes.Exists(stash => stash.Hash == _selected))
                {
                    _selected = _stashes.Count > 0 ? _stashes[0].Hash : null;
                }

                _loading = false;
                Repaint();
            }, reason =>
            {
                if (!this)
                {
                    return;
                }

                _loading = false;
                _aborted = _stashes == null ? reason : null;
                Repaint();
            });
        }

        private void OnGUI()
        {
            if (_stashes == null && !_loading && _aborted == null)
            {
                Refresh();
            }

            DrawStashChanges();

            EditorGUILayout.Space();

            DrawStashes();
            DrawButtons();
        }

        private void DrawStashChanges()
        {
            EditorGUILayout.LabelField("Stash local changes", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _message = EditorGUILayout.TextField(_message);
            using (new EditorGUI.DisabledScope(_loading))
            {
                if (GUILayout.Button("Stash", GUILayout.Width(80)))
                {
                    EditorApplication.delayCall += Stash;
                }
            }

            EditorGUILayout.EndHorizontal();

            if (string.IsNullOrEmpty(_message))
            {
                var rect = GUILayoutUtility.GetLastRect();
                rect.width -= 84;
                GUI.Label(rect, "Stash message (optional)", HintStyle);
            }

            _includeUntracked = EditorGUILayout.ToggleLeft("Include unversioned files", _includeUntracked);
        }

        private void DrawStashes()
        {
            EditorGUILayout.LabelField(
                _stashes == null ? "Stashes" : $"Stashes ({_stashes.Count})", EditorStyles.boldLabel);

            using var scroll = new EditorGUILayout.ScrollViewScope(_scrollPos, GUILayout.ExpandHeight(true));
            _scrollPos = scroll.scrollPosition;

            if (_stashes == null)
            {
                EditorGUILayout.LabelField(_aborted != null ? $"{_aborted}, press Refresh" : "Reading stashes...");
                return;
            }

            if (_stashes.Count == 0)
            {
                EditorGUILayout.LabelField("No stashes");
                return;
            }

            foreach (var stash in _stashes)
            {
                if (GUILayout.Toggle(_selected == stash.Hash,
                        $"{stash.Reference}   {stash.Date}   {stash.Message}", "Radio"))
                {
                    _selected = stash.Hash;
                }
            }
        }

        private void DrawButtons()
        {
            EditorGUILayout.BeginHorizontal();

            var selected = _stashes?.Find(stash => stash.Hash == _selected) ?? default;
            using (new EditorGUI.DisabledScope(_loading || selected.Hash == null))
            {
                if (GUILayout.Button("Apply"))
                {
                    EditorApplication.delayCall += () => Apply(selected, drop: false);
                }

                if (GUILayout.Button("Pop"))
                {
                    EditorApplication.delayCall += () => Apply(selected, drop: true);
                }

                if (GUILayout.Button("Drop"))
                {
                    EditorApplication.delayCall += () => Drop(selected);
                }
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_loading))
            {
                if (GUILayout.Button("Refresh"))
                {
                    Refresh();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void Stash()
        {
            if (!this)
            {
                return;
            }

            var git = _git ??= new Git();
            var message = _message;
            var includeUntracked = _includeUntracked;

            GitOperations.Run("Git: stashing local changes", () => git.Stash(message, includeUntracked), result =>
            {
                if (this)
                {
                    if (result.IsSuccess)
                    {
                        _message = "";
                        GUIUtility.keyboardControl = 0;
                    }

                    Refresh();
                }

                Notification.Show(result);
            }, refreshAssets: true, prepare: UnsavedChanges.SaveOrCancel);
        }

        private void Apply(GitStash stash, bool drop)
        {
            if (!this)
            {
                return;
            }

            var git = _git ??= new Git();
            GitOperations.Run(
                drop ? $"Git: popping {stash.Reference}" : $"Git: applying {stash.Reference}",
                () => git.ApplyStash(stash, drop),
                OnCompleted,
                refreshAssets: true,
                prepare: UnsavedChanges.SaveOrCancel
            );
        }

        private void Drop(GitStash stash)
        {
            if (!EditorUtility.DisplayDialog("Drop stash",
                    $"Drop {stash.Reference} \"{stash.Message}\"?\n\nIts changes will be lost.", "Drop", "Cancel"))
            {
                return;
            }

            var git = _git ??= new Git();
            GitOperations.Run($"Git: dropping {stash.Reference}", () => git.DropStash(stash), OnCompleted);
        }

        private void OnCompleted(GitResult result)
        {
            if (this)
            {
                Refresh();
            }

            Notification.Show(result);
        }
    }
}
