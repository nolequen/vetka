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
        private bool _historyFolder;
        private string _historyLocal;
        [NonSerialized] private History _history;
        [NonSerialized] private int _historyOffset;
        [NonSerialized] private GUIStyle _tagStyle;
        private string _logRef;
        [NonSerialized] private List<(string Label, string Ref)> _choices;
        [NonSerialized] private string[] _choiceLabels;
        [NonSerialized] private HashSet<string> _foreign;

        private sealed class History
        {
            public string Head;
            public string Branch;
            public string Remotes;
            public List<string> Commits;
            public List<string> Paths;
            public HashSet<string> Unpushed;
            public Dictionary<string, HashSet<string>> Names;
        }

        public static void ShowWindow(Git git) => Open(git, null, false, false, "Git log");

        public static void ShowHistory(Git git, string file, bool withMeta, bool folder = false,
            string local = null) =>
            Open(git, file, withMeta, folder, $"History: {System.IO.Path.GetFileName(local ?? file)}", local);

        private static void Open(Git git, string file, bool withMeta, bool folder, string title,
            string local = null)
        {
            var window = Resources.FindObjectsOfTypeAll<GitLogWindow>()
                .FirstOrDefault(candidate => candidate._historyFile == file && candidate._historyWithMeta == withMeta &&
                                             candidate._historyFolder == folder);
            if (window == null)
            {
                window = CreateInstance<GitLogWindow>();
                window._historyFile = file;
                window._historyWithMeta = withMeta;
                window._historyFolder = folder;
                window.titleContent = new GUIContent(title);
            }

            window._git = git;
            window._historyLocal = local;
            window.minSize = new Vector2(520, 300);
            window.Show();
            window.Focus();
            window.Refresh();
        }

        private sealed class FirstPage
        {
            public GitResult Result;
            public List<GitCommit> Commits = new List<GitCommit>();
            public string Head;
            public History History;
            public string Branch;
            public List<GitBranch> Branches;
            public string LogRef;
            public HashSet<string> Foreign = new HashSet<string>();
        }

        private static FirstPage ReadFirstPage(Git git, string file, bool withMeta, bool folder, string logRef,
            int count, History cached)
        {
            var branch = git.CurrentBranch();
            if (file == null)
            {
                var page = new FirstPage { Branch = branch, Branches = git.Branches() };
                var tip = logRef != null && logRef != "refs/heads/" + branch ? git.CommitOf(logRef) : null;
                page.LogRef = tip != null ? logRef : null;
                var log = git.ReadLogPage(tip, 0, count);
                page.Result = log.Result;
                page.Commits = log.Commits;
                page.Head = log.Head;
                if (tip != null && log.Result.IsSuccess)
                {
                    page.Foreign = git.CommitsMissingFromHead(tip, count);
                }

                return page;
            }

            var head = git.HeadCommit();
            if (head == null)
            {
                var log = git.ReadLogPage(null, 0, 0);
                return new FirstPage { Result = log.Result, Commits = log.Commits, Head = log.Head, Branch = branch };
            }

            var remotes = git.RemoteRefs();
            var history = cached != null && cached.Head == head && cached.Remotes == remotes &&
                          cached.Branch == branch
                ? cached
                : null;
            if (history == null)
            {
                var read = folder ? git.FolderHistory(head, file, withMeta) : git.FileHistory(head, file, withMeta);
                if (!read.Result.IsSuccess)
                {
                    return new FirstPage { Result = read.Result, Head = head, Branch = branch };
                }

                history = new History
                {
                    Head = head, Branch = branch, Remotes = remotes, Commits = read.Commits, Paths = read.Paths,
                    Unpushed = read.Unpushed, Names = read.Names
                };
            }

            var commits = git.ReadCommits(history.Commits.Take(count).ToList(), history.Unpushed);
            return new FirstPage
            {
                Result = commits.Result, Commits = commits.Commits, Head = head, History = history, Branch = branch
            };
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
            var folder = _historyFolder;
            var logRef = _logRef;
            var cached = force ? null : _history;
            GitOperations.Read(
                file == null ? "Git: reading the log" : "Git: reading the file history",
                () => ReadFirstPage(git, file, withMeta, folder, logRef, count, cached),
                state =>
                {
                    if (!this)
                    {
                        return;
                    }

                    _loading = false;
                    if (_logRef != logRef)
                    {
                        Refresh(true);
                        return;
                    }

                    _branch = state.Branch;
                    if (file == null)
                    {
                        _logRef = state.LogRef;
                        SetChoices(state.Branches);
                    }

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
                    _foreign = state.Foreign;
                    _historyOffset = _history != null ? Math.Min(count, _history.Commits.Count) : 0;
                    _hasMore = _history != null ? _historyOffset < _history.Commits.Count : _commits.Count == count;
                    _moreAborted = null;

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
                    if (_logRef != logRef)
                    {
                        Refresh(true);
                        return;
                    }

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
            var logRef = _logRef;
            GitOperations.Read("Git: reading older commits", () =>
            {
                var read = history != null
                    ? git.ReadCommits(history.Commits.Skip(offset).Take(PageSize).ToList(), history.Unpushed)
                    : ToPage(git.ReadLogPage(head, skip, PageSize));
                var foreign = history == null && logRef != null && read.Result.IsSuccess
                    ? git.CommitsMissingFromHead(head, skip + PageSize)
                    : null;
                return (read.Result, read.Commits, Foreign: foreign);
            }, page =>
            {
                if (!this)
                {
                    return;
                }

                _loadingMore = false;
                if (_commits != null && _head == head && _history == history && _commits.Count == skip &&
                    _historyOffset == offset && _logRef == logRef)
                {
                    if (page.Result.IsSuccess)
                    {
                        _commits.AddRange(page.Commits);
                        if (page.Foreign != null)
                        {
                            _foreign = page.Foreign;
                        }

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

                var paths = OwnNames(hash);
                var folder = _historyFolder ? _historyFile + "/" : null;
                bool Own(string path) => path != null &&
                                         (paths.Contains(path) ||
                                          folder != null && path.StartsWith(folder, StringComparison.Ordinal));
                var own = paths == null
                    ? files
                    : files.Where(file => Own(file.Path) || Own(file.OldPath)).ToList();
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

            if (_historyFile != null)
            {
                EditorGUILayout.LabelField(_historyFile, EditorStyles.boldLabel);
            }
            else
            {
                DrawBranchChoice();
            }

            DrawCommits();
            DrawFiles();
            DrawButtons();
        }

        private void SetChoices(List<GitBranch> branches)
        {
            _choices = new List<(string Label, string Ref)> { ($"{_branch ?? "HEAD"} (current)", null) };
            if (branches != null)
            {
                _choices.AddRange(branches
                    .Where(branch => !branch.IsCurrent)
                    .OrderBy(branch => branch.IsRemote)
                    .ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(branch => (branch.Name.Replace('/', '∕'), branch.Ref)));
            }

            _choiceLabels = _choices.Select(choice => choice.Label).ToArray();
        }

        private void DrawBranchChoice()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Branch", EditorStyles.boldLabel, GUILayout.Width(52));
            if (_choices == null)
            {
                EditorGUILayout.LabelField(_branch ?? "?");
            }
            else
            {
                var index = Math.Max(0, _choices.FindIndex(choice => choice.Ref == _logRef));
                var chosen = EditorGUILayout.Popup(index, _choiceLabels, GUILayout.MaxWidth(320));
                if (chosen != index)
                {
                    var reference = _choices[chosen].Ref;
                    EditorApplication.delayCall += () =>
                    {
                        if (this)
                        {
                            ShowBranch(reference);
                        }
                    };
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void ShowBranch(string reference)
        {
            _logRef = reference;
            _commits = null;
            _head = null;
            _hasMore = false;
            _selected = null;
            _files = null;
            _foreign = null;
            _aborted = null;
            _moreAborted = null;
            _scrollPos = Vector2.zero;
            Refresh(true);
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
                EditorGUILayout.LabelField(_historyFile == null ? "No commits yet"
                    : _historyFolder ? "No commits change this folder"
                    : "No commits change this file");
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

            if (current.type == EventType.ContextClick && rect.Contains(current.mousePosition))
            {
                ShowCommitMenu(commit);
                current.Use();
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
            var x = rect.x + 262;
            _tagStyle ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
            foreach (var tag in commit.Tags)
            {
                var content = new GUIContent(tag);
                var width = _tagStyle.CalcSize(content).x + 8;
                var chip = new Rect(x, rect.y + 1, Mathf.Max(0, Mathf.Min(width, rect.xMax - x)), rect.height - 2);
                if (current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(chip, GitColors.Tag);
                }

                GUI.Label(chip, content, _tagStyle);
                x += width + 4;
            }

            GUI.Label(new Rect(x, rect.y, Mathf.Max(0, rect.xMax - x), rect.height), commit.Subject, style);
        }

        private void ShowCommitMenu(GitCommit commit)
        {
            var menu = new GenericMenu();
            var foreign = _foreign != null && _foreign.Contains(commit.Hash);
            AddItem(menu, "Cherry-Pick", foreign, () => ApplyCommit(commit, false));
            AddItem(menu, "Revert Commit", !foreign, () => ApplyCommit(commit, true));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("New Tag..."), false, () => EditorApplication.delayCall += () => NewTag(commit));
            foreach (var tag in commit.Tags)
            {
                var name = tag;
                menu.AddItem(new GUIContent($"Delete Tag '{name.Replace('/', '∕')}'"), false,
                    () => EditorApplication.delayCall += () => DeleteTag(name));
            }

            menu.ShowAsContext();
        }

        private static void AddItem(GenericMenu menu, string title, bool enabled, Action action)
        {
            if (enabled)
            {
                menu.AddItem(new GUIContent(title), false, () => EditorApplication.delayCall += () => action());
            }
            else
            {
                menu.AddDisabledItem(new GUIContent(title));
            }
        }

        private void ApplyCommit(GitCommit commit, bool revert)
        {
            var git = _git ??= new Git();
            GitIdentityWindow.Ensure(git, () => GitOperations.Run(
                revert ? $"Git: reverting {commit.Hash}" : $"Git: cherry-picking {commit.Hash}",
                () => revert ? git.Revert(commit.Hash) : git.CherryPick(commit.Hash),
                result =>
                {
                    if (this)
                    {
                        Refresh();
                    }

                    Notification.Show(result);
                },
                refreshAssets: true,
                prepare: UnsavedChanges.SaveOrCancel));
        }

        private void NewTag(GitCommit commit)
        {
            var git = _git ??= new Git();
            NewTagWindow.Show($"{commit.Hash} {commit.Subject}", (name, message) =>
            {
                void Create() => GitOperations.Run($"Git: creating tag {name}",
                    () => git.CreateTag(name, commit.Hash, message), OnTagChanged, changesRepository: true);

                if (string.IsNullOrWhiteSpace(message))
                {
                    Create();
                }
                else
                {
                    GitIdentityWindow.Ensure(git, Create);
                }
            });
        }

        private void DeleteTag(string name)
        {
            var git = _git ??= new Git();
            GitOperations.Read("Git: reading the remote", () => git.TagRemote(out _), remote =>
            {
                var question = $"Delete tag '{name}'?";
                var choice = remote == null
                    ? EditorUtility.DisplayDialog("Delete tag", question, "Delete", "Cancel") ? 0 : 1
                    : EditorUtility.DisplayDialogComplex("Delete tag",
                        $"{question}\n\nDeleting it on {remote} removes it for everyone who fetches from there.",
                        "Delete Here", "Cancel", $"Delete Here and on {remote}");
                if (choice == 1)
                {
                    return;
                }

                GitOperations.Run($"Git: deleting tag {name}", () => git.DeleteTag(name, choice == 2), OnTagChanged,
                    changesRepository: true);
            });
        }

        private void OnTagChanged(GitResult result)
        {
            if (this)
            {
                Refresh();
            }

            Notification.Show(result);
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
                    var present = file.Status != GitStatus.Deleted;
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Show Diff"), false, () => ShowDiff(commit, path));
                    AddItem(menu, "Compare with Local", present, () => CompareWithLocal(commit, path));
                    AddItem(menu, "Get This Version...", present, () => GetVersion(commit, path));
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

        private ICollection<string> OwnNames(string commit)
        {
            if (_history == null)
            {
                return null;
            }

            if (_history.Names == null || !_history.Names.TryGetValue(commit, out var names))
            {
                return _history.Paths;
            }

            return names.SelectMany(name => _historyWithMeta ? new[] { name, name + ".meta" } : new[] { name })
                .ToList();
        }

        internal static string LocalPathFor(string pathAtCommit, ICollection<string> own, string local)
        {
            if (local == null || own == null || !own.Contains(pathAtCommit))
            {
                return pathAtCommit;
            }

            return pathAtCommit.EndsWith(".meta", StringComparison.Ordinal) &&
                   !local.EndsWith(".meta", StringComparison.Ordinal)
                ? local + ".meta"
                : local;
        }

        private string LocalPath(string commit, string pathAtCommit) =>
            LocalPathFor(pathAtCommit, _historyFile == null || _historyFolder ? null : OwnNames(commit),
                _historyLocal ?? _historyFile);

        private void CompareWithLocal(string commit, string pathAtCommit)
        {
            DiffWindow.ShowCompareWindow(_git ??= new Git(), commit, pathAtCommit, LocalPath(commit, pathAtCommit));
        }

        private void GetVersion(string commit, string pathAtCommit)
        {
            var local = LocalPath(commit, pathAtCommit);
            if (!EditorUtility.DisplayDialog("Get this version",
                    $"Replace {local} with its version from {commit}?\n\n" +
                    "Its current content will be lost unless it is committed.", "Replace", "Cancel"))
            {
                return;
            }

            var git = _git ??= new Git();
            GitOperations.Run($"Git: getting {local} from {commit}", () => git.GetVersion(commit, pathAtCommit, local),
                Notification.Show, refreshAssets: true, prepare: UnsavedChanges.SaveOrCancel);
        }

        private void DrawButtons()
        {
            var head = _commits != null && _commits.Count > 0 ? _commits[0] : default;
            var hasCommits = _commits != null && _commits.Count > 0;

            EditorGUILayout.BeginHorizontal();

            if (_historyFile == null)
            {
                using (new EditorGUI.DisabledScope(_loading || _busy || !hasCommits || _logRef != null))
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
