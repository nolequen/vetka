using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class PushWindow : EditorWindow
    {
        [NonSerialized] private Git _git;
        private Vector2 _filesScrollPos;
        [NonSerialized] private List<string> _commits;
        private string _localBranch;
        private GitPushTarget? _target;
        [NonSerialized] private string _problem;
        [NonSerialized] private bool _loading;
        [NonSerialized] private bool _pushing;

        public static void ShowWindow(Git git)
        {
            var window = GetWindow<PushWindow>(utility: true, "Commits to push");
            window.minSize = new Vector2(400, 220);
            window._git = git;
            window.Refresh();
        }

        private void Refresh()
        {
            _git ??= new Git();
            _loading = true;

            var git = _git;
            GitOperations.Read(
                "Git: reading outgoing commits",
                () => (commits: git.OutgoingCommits(), local: git.CurrentBranch(), target: git.PushDestination()),
                state =>
                {
                    if (!this)
                    {
                        return;
                    }

                    _commits = state.commits;
                    _localBranch = state.local;
                    _target = state.target.Target;
                    _problem = state.target.Problem;
                    _loading = false;
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
            if (_commits == null && !_loading)
            {
                Refresh();
            }

            var destination = !_target.HasValue ? "?"
                : _target.Value.Exists ? _target.Value.Name
                : $"{_target.Value.Name} (new branch)";
            EditorGUILayout.LabelField($"{_localBranch ?? "?"} → {destination}", EditorStyles.boldLabel);
            if (!_target.HasValue && _problem != null)
            {
                EditorGUILayout.HelpBox(_problem, MessageType.Warning);
            }

            using (var scroll = new EditorGUILayout.ScrollViewScope(
                       _filesScrollPos,
                       GUILayout.ExpandHeight(true),
                       GUILayout.MinHeight(100f)
                   ))
            {
                _filesScrollPos = scroll.scrollPosition;

                if (_commits == null)
                {
                    EditorGUILayout.LabelField("Reading the commit history...");
                }
                else if (_commits.Any())
                {
                    foreach (var commit in _commits)
                    {
                        EditorGUILayout.LabelField(commit);
                    }
                }
                else if (_target.HasValue || _problem == null)
                {
                    EditorGUILayout.LabelField("No commits to push");
                }
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginHorizontal();

            var canPush = !_loading && !_pushing && _commits != null && _target.HasValue &&
                          (_commits.Any() || !_target.Value.Exists);
            using (new EditorGUI.DisabledScope(!canPush))
            {
                if (GUILayout.Button("Push"))
                {
                    Push();
                }
            }

            if (GUILayout.Button("Cancel"))
            {
                EditorApplication.delayCall += Close;
            }

            EditorGUILayout.EndHorizontal();
        }

        private void Push()
        {
            _pushing = true;
            var git = _git ??= new Git();
            GitOperations.Run("Git: pushing", () => git.Push(), result =>
            {
                _pushing = false;
                if (result.IsSuccess && this)
                {
                    Close();
                }
                else if (this)
                {
                    Refresh();
                }

                Notification.Show(result);
            }, reportsProgress: true, changesRepository: true);
        }
    }
}
