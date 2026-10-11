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
        [NonSerialized] private GitComparison _comparison;
        [NonSerialized] private string _selectedFile;
        private Vector2 _scrollPos;
        [NonSerialized] private int _generation = -1;

        public static void ShowWindow(string reference, string name)
        {
            var window = GetWindow<CompareBranchWindow>(false, "Compare", false);
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

        private void OnFocus()
        {
            if (_generation != GitOperations.Generation)
            {
                Refresh();
            }
        }

        private void Refresh()
        {
            if (_loading || string.IsNullOrEmpty(_reference))
            {
                return;
            }

            _git ??= new Git();
            _loading = true;
            _generation = GitOperations.Generation;
            var git = _git;
            var reference = _reference;
            var name = _name;
            GitOperations.Read("Git: comparing branches", () => git.CompareWithWorkingTree(reference, name, CommitLimit), comparison =>
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
                if (!comparison.Result.IsSuccess)
                {
                    _generation = -1;
                }

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

                _comparison ??= new GitComparison { Result = GitResult.Failure(reason) };
                _generation = -1;
                Repaint();
            });
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
                HistoryRows.Commit(rect, commit);
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

            for (var i = 0; i < files.Count && i < FileLimit; i++)
            {
                var file = files[i];
                var rect = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
                var label = file.OldPath != null ? $"{file.Path}   (from {file.OldPath})" : file.Path;
                HistoryRows.File(rect, file, label, ref _selectedFile, () => ShowDiff(file),
                    menu => menu.AddItem(new GUIContent("Show Diff"), false, () => ShowDiff(file)), Repaint);
            }

            if (files.Count > FileLimit)
            {
                EditorGUILayout.LabelField($"...and {files.Count - FileLimit} more files", EditorStyles.miniLabel);
            }
        }

        private void ShowDiff(GitFileChange file)
        {
            var tip = _comparison?.Tip;
            if (tip == null)
            {
                return;
            }

            EditorApplication.delayCall += () => DiffWindow.ShowCompareWindow(tip, file.OldPath ?? file.Path, file.Path);
        }
    }
}
