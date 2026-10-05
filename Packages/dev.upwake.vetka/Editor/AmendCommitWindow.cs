using System;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class AmendCommitWindow : EditorWindow
    {
        [NonSerialized] private Git _git;
        private Action _onAmended;
        private string _message;
        private string _head;
        private bool _isPushed;
        [NonSerialized] private bool _loading;
        [NonSerialized] private bool _loaded;
        [NonSerialized] private bool _amendRequested;

        public static void ShowWindow(Git git, Action onAmended = null)
        {
            var window = GetWindow<AmendCommitWindow>(utility: true, "Amend last commit");
            window._git = git;
            window._onAmended = onAmended;
            window.minSize = new Vector2(400, 180);
            window.Load();
        }

        private void Load()
        {
            _git ??= new Git();
            _loading = true;

            var git = _git;
            GitOperations.Read(
                "Git: reading the last commit",
                () => (message: git.LastCommitMessage(), isPushed: git.IsHeadPushed(), head: git.HeadCommit()),
                state =>
                {
                    if (!this)
                    {
                        return;
                    }

                    _message = state.message;
                    _isPushed = state.isPushed;
                    _head = state.head;
                    _loading = false;
                    _loaded = true;
                    Repaint();
                },
                _ =>
                {
                    if (this)
                    {
                        Close();
                    }
                }
            );
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(_head) && !_loading && !_loaded)
            {
                Load();
            }

            if (string.IsNullOrEmpty(_head) || _message == null)
            {
                EditorGUILayout.LabelField(_loaded ? "The last commit cannot be read" : "Reading the last commit...");
                return;
            }

            if (_isPushed)
            {
                EditorGUILayout.HelpBox(
                    "The last commit is already pushed. Rewriting it will require a force push.",
                    MessageType.Warning
                );
            }

            _message = EditorGUILayout.TextArea(_message, GUILayout.ExpandHeight(true));

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_loading || string.IsNullOrWhiteSpace(_message)))
            {
                if (GUILayout.Button("Amend", GUILayout.Width(80)))
                {
                    Amend();
                }
            }

            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            {
                EditorApplication.delayCall += Close;
            }

            EditorGUILayout.EndHorizontal();
        }

        private void Amend()
        {
            if (_amendRequested)
            {
                return;
            }

            _amendRequested = true;
            EditorApplication.delayCall += () => _amendRequested = false;
            var git = _git ??= new Git();
            var message = _message;
            var head = _head;
            var onAmended = _onAmended;

            GitIdentityWindow.Ensure(git, () =>
                GitOperations.Run("Git: amending the last commit", () => git.Amend(message, head),
                    result =>
                    {
                        if (result.IsSuccess)
                        {
                            onAmended?.Invoke();
                            if (this)
                            {
                                Close();
                            }
                        }

                        Notification.Show(result);
                    }));
        }
    }
}
