using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class GitLogWindow : EditorWindow
    {
        private const int Limit = 50;
        private const float FilesHeight = 140;

        [NonSerialized] private Git _git;
        private List<GitCommit> _commits;
        private string _branch;
        private Vector2 _scrollPos;
        [NonSerialized] private bool _loading;
        [NonSerialized] private bool _busy;
        [NonSerialized] private string _aborted;
        private string _selected;
        private List<GitFileChange> _files;
        private string _selectedFile;
        private Vector2 _filesScrollPos;

        public static void ShowWindow(Git git)
        {
            var window = GetWindow<GitLogWindow>(utility: false, "Git log");
            window._git = git;
            window.minSize = new Vector2(520, 300);
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
            GitOperations.Read(
                "Git: reading the log",
                () => (log: git.ReadLog(Limit), branch: git.CurrentBranch()),
                state =>
                {
                    if (!this)
                    {
                        return;
                    }

                    _loading = false;
                    if (!state.log.Result.IsSuccess)
                    {
                        _commits = null;
                        _aborted = state.log.Result.Message;
                        Repaint();
                        return;
                    }

                    _commits = state.log.Commits;
                    _branch = state.branch;
                    _loading = false;

                    if (_selected != null && _commits.Any(commit => commit.Hash == _selected))
                    {
                        if (_files == null)
                        {
                            Select(_selected);
                        }
                    }
                    else
                    {
                        _selected = null;
                        _files = null;
                    }

                    Repaint();
                },
                reason =>
                {
                    if (!this)
                    {
                        return;
                    }

                    _loading = false;
                    _aborted = _commits == null ? reason : null;
                    Repaint();
                }
            );
        }

        private void Select(string hash)
        {
            _selected = hash;
            _files = null;

            var git = _git;
            GitOperations.Read("Git: reading the commit", () => git.CommitFiles(hash), files =>
            {
                if (!this || _selected != hash)
                {
                    return;
                }

                _files = files;
                if (!_files.Exists(file => file.Path == _selectedFile))
                {
                    _selectedFile = null;
                }

                _filesScrollPos = Vector2.zero;
                Repaint();
            }, _ =>
            {
                if (!this || _selected != hash)
                {
                    return;
                }

                _selected = null;
                Repaint();
            });
        }

        private void OnGUI()
        {
            if (_commits == null && !_loading && _aborted == null)
            {
                Refresh();
            }

            EditorGUILayout.LabelField(_branch ?? "?", EditorStyles.boldLabel);

            DrawCommits();
            DrawFiles();
            DrawButtons();
        }

        private void DrawCommits()
        {
            using var scroll = new EditorGUILayout.ScrollViewScope(_scrollPos, GUILayout.ExpandHeight(true));
            _scrollPos = scroll.scrollPosition;

            if (_commits == null)
            {
                EditorGUILayout.LabelField(_aborted != null ? $"{_aborted}, press Refresh" : "Reading the commit history...");
                return;
            }

            if (_commits.Count == 0)
            {
                EditorGUILayout.LabelField("No commits yet");
                return;
            }

            foreach (var commit in _commits)
            {
                var row = EditorGUILayout.BeginHorizontal();
                var current = Event.current;
                if (current.type == EventType.MouseDown && (current.button == 0 || current.button == 1) &&
                    row.Contains(current.mousePosition))
                {
                    if (commit.Hash != _selected)
                    {
                        Select(commit.Hash);
                    }

                    current.Use();
                    Repaint();
                }

                if (current.type == EventType.Repaint && commit.Hash == _selected)
                {
                    EditorGUI.DrawRect(row, GitColors.Selection);
                }

                var style = commit.IsPushed
                    ? EditorStyles.label
                    : GitColors.TextStyle(EditorStyles.label, GitColors.Unpushed);
                EditorGUILayout.LabelField(commit.Hash, style, GUILayout.Width(60));
                EditorGUILayout.LabelField(commit.Date, style, GUILayout.Width(80));
                EditorGUILayout.LabelField(commit.Author, style, GUILayout.Width(110));
                EditorGUILayout.LabelField(commit.Subject, style);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawFiles()
        {
            if (_selected == null)
            {
                return;
            }

            EditorGUILayout.LabelField(
                _files == null ? $"Changed files in {_selected}" : $"Changed files in {_selected} ({_files.Count})",
                EditorStyles.boldLabel);

            using var scroll = new EditorGUILayout.ScrollViewScope(_filesScrollPos, GUILayout.Height(FilesHeight));
            _filesScrollPos = scroll.scrollPosition;

            if (_files == null)
            {
                EditorGUILayout.LabelField("Reading the commit...");
                return;
            }

            var commit = _selected;
            var rowHeight = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            var area = GUILayoutUtility.GetRect(0, _files.Count * rowHeight, GUILayout.ExpandWidth(true));
            var first = Mathf.Max(0, (int)(_filesScrollPos.y / rowHeight));
            var last = Mathf.Min(_files.Count, first + (int)(FilesHeight / rowHeight) + 2);
            for (var i = first; i < last; i++)
            {
                var file = _files[i];
                var rect = new Rect(area.x, area.y + i * rowHeight, area.width, EditorGUIUtility.singleLineHeight);
                var current = Event.current;
                if (current.type == EventType.MouseDown && rect.Contains(current.mousePosition))
                {
                    _selectedFile = file.Path;
                    if (current.button == 0 && current.clickCount == 2)
                    {
                        ShowDiff(commit, file.Path);
                    }

                    current.Use();
                    Repaint();
                }

                if (current.type == EventType.ContextClick && rect.Contains(current.mousePosition))
                {
                    var path = file.Path;
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Show Diff"), false, () => ShowDiff(commit, path));
                    menu.ShowAsContext();
                    current.Use();
                }

                if (current.type == EventType.Repaint && file.Path == _selectedFile)
                {
                    EditorGUI.DrawRect(rect, GitColors.Selection);
                }

                GUI.Label(rect, file.Path, GitColors.StatusStyle(GUI.skin.label, file.Status));
            }
        }

        private void ShowDiff(string commit, string path)
        {
            var git = _git;
            EditorApplication.delayCall += () => DiffWindow.ShowCommitWindow(git, commit, path);
        }

        private void DrawButtons()
        {
            var head = _commits != null && _commits.Count > 0 ? _commits[0] : default;
            var hasCommits = _commits != null && _commits.Count > 0;

            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(_loading || _busy || !hasCommits))
            {
                if (GUILayout.Button("Amend last commit..."))
                {
                    EditorApplication.delayCall += () => AmendCommitWindow.ShowWindow(new Git(), OnAmended);
                }

                if (GUILayout.Button("Undo last commit") && !_busy)
                {
                    _busy = true;
                    EditorApplication.delayCall += () => UndoLastCommit(head);
                }
            }

            using (new EditorGUI.DisabledScope(_loading))
            {
                if (GUILayout.Button("Refresh"))
                {
                    Refresh();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void OnAmended()
        {
            if (this)
            {
                Refresh();
            }
        }

        private void UndoLastCommit(GitCommit head)
        {
            var warning = head.IsPushed
                ? $"Commit {head.Hash} \"{head.Subject}\" is already pushed.\n" +
                  "Undoing it here will make the branches diverge and the next push will need --force.\n\n" +
                  "Undo it anyway?"
                : $"Undo commit {head.Hash} \"{head.Subject}\"?\n\n" +
                  "Its changes stay in the working tree, only the commit itself goes away.";

            if (!EditorUtility.DisplayDialog("Undo last commit", warning, "Undo", "Cancel"))
            {
                if (this)
                {
                    _busy = false;
                }

                return;
            }

            var git = _git;
            GitOperations.Run("Git: undoing the last commit", () => git.UndoLastCommit(head.Hash), result =>
            {
                if (this)
                {
                    _busy = false;
                    Refresh();
                }

                Notification.Show(result);
            }, refreshAssets: true);
        }
    }
}
