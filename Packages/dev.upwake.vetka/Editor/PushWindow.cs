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
        [NonSerialized] private string _tracked;
        [NonSerialized] private string _head;
        [NonSerialized] private int _remoteOnlyCount;
        [NonSerialized] private List<string> _remoteOnly;
        [NonSerialized] private bool _loading;
        [NonSerialized] private bool _pushing;

        public static void ShowWindow(Git git)
        {
            var window = GetWindow<PushWindow>(utility: true, "Commits to push");
            window.minSize = new Vector2(400, 220);
            window._git = git;
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

            var git = _git;
            GitOperations.Read(
                "Git: reading outgoing commits",
                () =>
                {
                    var destination = git.PushDestination();
                    var remoteOnly = destination.Target.HasValue && destination.Target.Value.Exists
                        ? git.RemoteOnlyCommits(destination.Target.Value)
                        : (null, null, 0, new List<string>());
                    return (commits: git.OutgoingCommits(), local: git.CurrentBranch(), target: destination,
                        remoteOnly);
                },
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
                    _tracked = state.remoteOnly.Tracked;
                    _head = state.remoteOnly.Head;
                    _remoteOnlyCount = state.remoteOnly.Count;
                    _remoteOnly = state.remoteOnly.Commits;
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

            if (_target.HasValue && _remoteOnlyCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{_target.Value.Name} has {Commits(_remoteOnlyCount)} that {_localBranch ?? "your branch"} does not " +
                    (_commits != null && _commits.Any()
                        ? "have. Push will offer to merge or rebase them first, Force Push removes them from "
                        : "have and there is nothing to push. Force Push removes them from ") +
                    $"{_target.Value.Name}.", MessageType.Warning);
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
                    Push(null, null);
                }
            }

            if (_target.HasValue && _remoteOnlyCount > 0 && _tracked != null)
            {
                using (new EditorGUI.DisabledScope(_loading || _pushing))
                {
                    if (GUILayout.Button("Force Push..."))
                    {
                        _pushing = true;
                        EditorApplication.delayCall += ConfirmForcePush;
                    }
                }
            }

            if (GUILayout.Button("Cancel"))
            {
                EditorApplication.delayCall += Close;
            }

            EditorGUILayout.EndHorizontal();
        }

        private static string Commits(int count) => count == 1 ? "1 commit" : $"{count} commits";

        private void ConfirmForcePush()
        {
            if (!this)
            {
                return;
            }

            if (!_target.HasValue || _tracked == null || _remoteOnlyCount == 0)
            {
                _pushing = false;
                return;
            }

            var target = _target.Value;
            var shown = _remoteOnly.Take(10).ToList();
            var more = _remoteOnlyCount - shown.Count;
            var list = string.Join("\n", shown) + (more > 0 ? $"\n...and {Commits(more)} more" : "");
            if (!EditorUtility.DisplayDialog($"Force push to {target.Name}?",
                    $"These {Commits(_remoteOnlyCount)} on {target.Name} are not in {_localBranch} and will be removed " +
                    $"from {target.Name}:\n\n{list}\n\nAnyone who already has them will have to fix their branch.",
                    "Force Push", "Cancel"))
            {
                _pushing = false;
                return;
            }

            Push(_tracked, _head);
        }

        private void Push(string forceOver, string shownHead)
        {
            _pushing = true;
            var git = _git ??= new Git();
            GitOperations.Run(forceOver == null ? "Git: pushing" : "Git: force pushing", () => git.Push(forceOver, shownHead), result =>
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

                if (result.IsRejected)
                {
                    RejectedPush.Offer(git, result, OnUpdatedAndPushed);
                }
                else
                {
                    Notification.Show(result);
                }
            }, reportsProgress: true, changesRepository: true);
        }

        private void OnUpdatedAndPushed(GitResult result)
        {
            if (!this)
            {
                return;
            }

            if (result.IsSuccess)
            {
                Close();
            }
            else
            {
                Refresh();
            }
        }
    }
}
