using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class GitLogWindow : EditorWindow
    {
        private const int PageSize = 100;
        private const float FilesHeight = 140;

        [NonSerialized] private Git _git;
        private List<GitCommit> _commits;
        private string _head;
        private bool _hasMore;
        private string _branch;
        private Vector2 _scrollPos;
        [NonSerialized] private bool _loading;
        [NonSerialized] private bool _loadingMore;
        [NonSerialized] private bool _busy;
        [NonSerialized] private string _aborted;
        [NonSerialized] private string _moreAborted;
        private string _selected;
        private List<GitFileChange> _files;
        private string _selectedFile;
        private Vector2 _filesScrollPos;
        private string _historyFile;
        private bool _historyWithMeta;
        [NonSerialized] private History _history;
        [NonSerialized] private int _historyOffset;

        private sealed class History
        {
            public string Head;
            public string Branch;
            public string Remotes;
            public List<string> Commits;
            public List<string> Paths;
            public HashSet<string> Unpushed;
        }

        public static void ShowWindow(Git git) => Open(git, null, false, "Git log");

        public static void ShowHistory(Git git, string file, bool withMeta) =>
            Open(git, file, withMeta, $"History: {System.IO.Path.GetFileName(file)}");

        private static void Open(Git git, string file, bool withMeta, string title)
        {
            var window = Resources.FindObjectsOfTypeAll<GitLogWindow>()
                .FirstOrDefault(candidate => candidate._historyFile == file && candidate._historyWithMeta == withMeta);
            if (window == null)
            {
                window = CreateInstance<GitLogWindow>();
                window._historyFile = file;
                window._historyWithMeta = withMeta;
                window.titleContent = new GUIContent(title);
            }

            window._git = git;
            window.minSize = new Vector2(520, 300);
            window.Show();
            window.Focus();
            window.Refresh();
        }

        private static (GitResult Result, List<GitCommit> Commits, string Head, History History, string Branch)
            ReadFirstPage(Git git, string file, bool withMeta, int count, History cached)
        {
            var branch = git.CurrentBranch();
            var head = file == null ? null : git.HeadCommit();
            if (head == null)
            {
                var log = git.ReadLogPage(null, 0, file == null ? count : 0);
                return (log.Result, log.Commits, log.Head, null, branch);
            }

            var remotes = git.RemoteRefs();
            var history = cached != null && cached.Head == head && cached.Remotes == remotes &&
                          cached.Branch == branch
                ? cached
                : null;
            if (history == null)
            {
                var read = git.FileHistory(head, file, withMeta);
                if (!read.Result.IsSuccess)
                {
                    return (read.Result, new List<GitCommit>(), head, null, branch);
                }

                history = new History
                {
                    Head = head, Branch = branch, Remotes = remotes, Commits = read.Commits, Paths = read.Paths,
                    Unpushed = read.Unpushed
                };
            }

            var page = git.ReadCommits(history.Commits.Take(count).ToList(), history.Unpushed);
            return (page.Result, page.Commits, head, history, branch);
        }

        private static (GitResult Result, List<GitCommit> Commits) ToPage(
            (GitResult Result, List<GitCommit> Commits, string Head) log) => (log.Result, log.Commits);

        private void OnEnable() => GitOperations.RepositoryChanged += Refresh;

        private void OnDisable() => GitOperations.RepositoryChanged -= Refresh;

        private void OnFocus() => Refresh();

        private void Refresh() => Refresh(false);

        private void Refresh(bool force)
        {
            if (_loading)
            {
                return;
            }

            _git ??= new Git();
            _loading = true;
            _aborted = null;

            var git = _git;
            var count = Math.Max(PageSize,
                (_history != null ? _historyOffset : _commits?.Count ?? 0) + (_loadingMore ? PageSize : 0));
            var file = _historyFile;
            var withMeta = _historyWithMeta;
            var cached = force ? null : _history;
            GitOperations.Read(
                file == null ? "Git: reading the log" : "Git: reading the file history",
                () => ReadFirstPage(git, file, withMeta, count, cached),
                state =>
                {
                    if (!this)
                    {
                        return;
                    }

                    _loading = false;
                    if (!state.Result.IsSuccess)
                    {
                        _commits = null;
                        _head = null;
                        _history = null;
                        _aborted = state.Result.Message;
                        Repaint();
                        return;
                    }

                    _commits = state.Commits;
                    _head = state.Head;
                    _history = state.History;
                    _historyOffset = _history != null ? Math.Min(count, _history.Commits.Count) : 0;
                    _hasMore = _history != null ? _historyOffset < _history.Commits.Count : _commits.Count == count;
                    _moreAborted = null;
                    _branch = state.Branch;

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

        private void LoadMore()
        {
            if (_loading || _loadingMore || !_hasMore || _head == null || _commits == null)
            {
                return;
            }

            _loadingMore = true;
            _moreAborted = null;
            var git = _git ??= new Git();
            var head = _head;
            var skip = _commits.Count;
            var history = _history;
            var offset = _historyOffset;
            GitOperations.Read("Git: reading older commits", () => history != null
                ? git.ReadCommits(history.Commits.Skip(offset).Take(PageSize).ToList(), history.Unpushed)
                : ToPage(git.ReadLogPage(head, skip, PageSize)), page =>
            {
                if (!this)
                {
                    return;
                }

                _loadingMore = false;
                if (_commits != null && _head == head && _history == history && _commits.Count == skip &&
                    _historyOffset == offset)
                {
                    if (page.Result.IsSuccess)
                    {
                        _commits.AddRange(page.Commits);
                        if (history != null)
                        {
                            _historyOffset = Math.Min(offset + PageSize, history.Commits.Count);
                            _hasMore = _historyOffset < history.Commits.Count;
                        }
                        else
                        {
                            _hasMore = page.Commits.Count == PageSize;
                        }
                    }
                    else
                    {
                        _moreAborted = page.Result.Message;
                    }
                }

                Repaint();
            }, reason =>
            {
                if (!this)
                {
                    return;
                }

                _loadingMore = false;
                _moreAborted = reason;
                Repaint();
            });
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

                var paths = _history?.Paths;
                var own = paths == null
                    ? files
                    : files.Where(file => paths.Contains(file.Path) ||
                                          file.OldPath != null && paths.Contains(file.OldPath)).ToList();
                _files = own.Count > 0 ? own : files;
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

            EditorGUILayout.LabelField(_historyFile ?? _branch ?? "?", EditorStyles.boldLabel);

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
                EditorGUILayout.LabelField(_historyFile == null ? "No commits yet" : "No commits change this file");
                return;
            }

            var rowHeight = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            var rows = _commits.Count + (_hasMore ? 1 : 0);
            var area = GUILayoutUtility.GetRect(0, rows * rowHeight, GUILayout.ExpandWidth(true));
            var first = Mathf.Max(0, (int)(_scrollPos.y / rowHeight));
            var last = Mathf.Min(rows, first + (int)(position.height / rowHeight) + 2);
            for (var i = first; i < last; i++)
            {
                var rect = new Rect(area.x, area.y + i * rowHeight, area.width, EditorGUIUtility.singleLineHeight);
                if (i < _commits.Count)
                {
                    DrawCommit(_commits[i], rect);
                }
                else
                {
                    DrawMore(rect);
                }
            }
        }

        private void DrawCommit(GitCommit commit, Rect rect)
        {
            var current = Event.current;
            if (current.type == EventType.MouseDown && (current.button == 0 || current.button == 1) &&
                rect.Contains(current.mousePosition))
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
                EditorGUI.DrawRect(rect, GitColors.Selection);
            }

            var style = commit.IsPushed
                ? EditorStyles.label
                : GitColors.TextStyle(EditorStyles.label, GitColors.Unpushed);
            GUI.Label(new Rect(rect.x, rect.y, 60, rect.height), commit.Hash, style);
            GUI.Label(new Rect(rect.x + 64, rect.y, 80, rect.height), commit.Date, style);
            GUI.Label(new Rect(rect.x + 148, rect.y, 110, rect.height), commit.Author, style);
            GUI.Label(new Rect(rect.x + 262, rect.y, Mathf.Max(0, rect.width - 262), rect.height), commit.Subject,
                style);
        }

        private void DrawMore(Rect rect)
        {
            if (_moreAborted == null)
            {
                GUI.Label(rect, "Loading older commits...", EditorStyles.centeredGreyMiniLabel);
                if (Event.current.type == EventType.Repaint)
                {
                    LoadMore();
                }

                return;
            }

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            if (GUI.Button(rect, $"{_moreAborted}, click to load older commits", EditorStyles.centeredGreyMiniLabel))
            {
                _moreAborted = null;
                LoadMore();
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

            if (_historyFile == null)
            {
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
            }
            else
            {
                GUILayout.FlexibleSpace();
            }

            using (new EditorGUI.DisabledScope(_loading))
            {
                if (GUILayout.Button("Refresh"))
                {
                    Refresh(true);
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
