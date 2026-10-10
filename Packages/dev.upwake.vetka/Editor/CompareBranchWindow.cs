using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class CompareBranchWindow : EditorWindow
    {
        private const int CommitLimit = 100;
        private const int FileLimit = 1000;

        [SerializeField] private string _reference;
        [SerializeField] private string _name;
        [NonSerialized] private Git _git;
        [NonSerialized] private bool _loading;
        [NonSerialized] private Comparison _comparison;
        [NonSerialized] private string _selectedFile;
        private Vector2 _scrollPos;

        private sealed class Comparison
        {
            public GitResult Result;
            public string Current;
            public string Tip;
            public int OnlyHereCount;
            public List<GitCommit> OnlyHere = new List<GitCommit>();
            public int OnlyCurrentCount;
            public List<GitCommit> OnlyCurrent = new List<GitCommit>();
            public List<GitFileChange> Files = new List<GitFileChange>();
        }

        public static void ShowWindow(Git git, string reference, string name)
        {
            var window = GetWindow<CompareBranchWindow>(false, "Compare", false);
            window._git = git;
            window._reference = reference;
            window._name = name;
            window._comparison = null;
            window._selectedFile = null;
            window._scrollPos = Vector2.zero;
            window.minSize = new Vector2(520, 360);
            window.Focus();
            window.Refresh();
        }

        private void OnEnable() => GitOperations.RepositoryChanged += Refresh;

        private void OnDisable() => GitOperations.RepositoryChanged -= Refresh;

        private void OnFocus() => Refresh();

        private void Refresh()
        {
            if (_loading || string.IsNullOrEmpty(_reference))
            {
                return;
            }

            _git ??= new Git();
            _loading = true;
            var git = _git;
            var reference = _reference;
            var name = _name;
            GitOperations.Read("Git: comparing branches", () => Compare(git, reference, name), comparison =>
            {
                if (!this)
                {
                    return;
                }

                _loading = false;
                if (reference != _reference)
                {
                    Refresh();
                    return;
                }

                _comparison = comparison;
                Repaint();
            }, reason =>
            {
                if (!this)
                {
                    return;
                }

                _loading = false;
                if (reference != _reference)
                {
                    Refresh();
                    return;
                }

                _comparison ??= new Comparison { Result = GitResult.Failure(reason) };
                Repaint();
            });
        }

        private static Comparison Compare(Git git, string reference, string name)
        {
            var comparison = new Comparison { Current = git.CurrentBranch() ?? "HEAD" };
            comparison.Tip = git.CommitOf(reference);
            if (comparison.Tip == null)
            {
                comparison.Result = GitResult.Failure($"{name} cannot be found");
                return comparison;
            }

            var head = git.HeadCommit();
            var onlyHere = git.CommitsOnlyIn(comparison.Tip, head, CommitLimit);
            var onlyCurrent = head != null
                ? git.CommitsOnlyIn(head, comparison.Tip, CommitLimit)
                : (Result: GitResult.Success(""), Count: 0, Commits: new List<GitCommit>());
            var files = git.DifferencesFromWorkingTree(comparison.Tip);
            comparison.Result = !onlyHere.Result.IsSuccess ? onlyHere.Result
                : !onlyCurrent.Result.IsSuccess ? onlyCurrent.Result
                : files.Result;
            comparison.OnlyHereCount = onlyHere.Count;
            comparison.OnlyHere = onlyHere.Commits;
            comparison.OnlyCurrentCount = onlyCurrent.Count;
            comparison.OnlyCurrent = onlyCurrent.Commits;
            comparison.Files = files.Files;
            return comparison;
        }

        private void OnGUI()
        {
            if (_comparison == null && !_loading)
            {
                Refresh();
            }

            var current = _comparison?.Current ?? "the current branch";
            EditorGUILayout.LabelField($"{_name} compared with {current}", EditorStyles.boldLabel);
            if (_comparison == null)
            {
                EditorGUILayout.LabelField("Reading the differences...");
                return;
            }

            if (!_comparison.Result.IsSuccess)
            {
                EditorGUILayout.HelpBox(_comparison.Result.Message, MessageType.Warning);
            }
            else
            {
                using var scroll = new EditorGUILayout.ScrollViewScope(_scrollPos, GUILayout.ExpandHeight(true));
                _scrollPos = scroll.scrollPosition;
                DrawCommits($"In {_name}, not in {current}", _comparison.OnlyHereCount, _comparison.OnlyHere);
                DrawCommits($"In {current}, not in {_name}", _comparison.OnlyCurrentCount, _comparison.OnlyCurrent);
                DrawFiles();
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(_loading))
            {
                if (GUILayout.Button("Refresh", GUILayout.Width(80)))
                {
                    Refresh();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private static void DrawCommits(string title, int count, List<GitCommit> commits)
        {
            EditorGUILayout.LabelField($"{title} ({count})", EditorStyles.boldLabel);
            if (count == 0)
            {
                EditorGUILayout.LabelField("No commits", EditorStyles.miniLabel);
            }

            foreach (var commit in commits)
            {
                var rect = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
                GUI.Label(new Rect(rect.x, rect.y, 60, rect.height), commit.Hash);
                GUI.Label(new Rect(rect.x + 64, rect.y, 80, rect.height), commit.Date);
                GUI.Label(new Rect(rect.x + 148, rect.y, 110, rect.height), commit.Author);
                GUI.Label(new Rect(rect.x + 262, rect.y, Mathf.Max(0, rect.width - 262), rect.height),
                    commit.Tags.Count > 0 ? $"[{string.Join(", ", commit.Tags)}] {commit.Subject}" : commit.Subject);
            }

            if (count > commits.Count)
            {
                EditorGUILayout.LabelField($"...and {count - commits.Count} more", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space();
        }

        private void DrawFiles()
        {
            var files = _comparison.Files;
            EditorGUILayout.LabelField($"Files that differ between {_name} and the working tree ({files.Count})",
                EditorStyles.boldLabel);
            if (files.Count == 0)
            {
                EditorGUILayout.LabelField("No differences", EditorStyles.miniLabel);
            }

            var current = Event.current;
            for (var i = 0; i < files.Count && i < FileLimit; i++)
            {
                var file = files[i];
                var rect = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
                if (current.type == EventType.MouseDown && rect.Contains(current.mousePosition))
                {
                    _selectedFile = file.Path;
                    if (current.button == 0 && current.clickCount == 2)
                    {
                        ShowDiff(file);
                    }

                    current.Use();
                    Repaint();
                }

                if (current.type == EventType.ContextClick && rect.Contains(current.mousePosition))
                {
                    var chosen = file;
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Show Diff"), false, () => ShowDiff(chosen));
                    menu.ShowAsContext();
                    current.Use();
                }

                if (current.type == EventType.Repaint && file.Path == _selectedFile)
                {
                    EditorGUI.DrawRect(rect, GitColors.Selection);
                }

                var label = file.OldPath != null ? $"{file.Path}   (from {file.OldPath})" : file.Path;
                GUI.Label(rect, label, GitColors.StatusStyle(GUI.skin.label, file.Status));
            }

            if (files.Count > FileLimit)
            {
                EditorGUILayout.LabelField($"...and {files.Count - FileLimit} more files", EditorStyles.miniLabel);
            }
        }

        private void ShowDiff(GitFileChange file)
        {
            var git = _git ??= new Git();
            var tip = _comparison?.Tip;
            if (tip == null)
            {
                return;
            }

            EditorApplication.delayCall += () =>
                DiffWindow.ShowCompareWindow(git, tip, file.OldPath ?? file.Path, file.Path);
        }
    }
}
