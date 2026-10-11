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
        [NonSerialized] private string _filesAborted;
        private string _selectedFile;
        private Vector2 _filesScrollPos;
        [SerializeField] private string _historyFile;
        [SerializeField] private bool _historyWithMeta;
        [SerializeField] private bool _historyFolder;
        [SerializeField] private string _historyLocal;
        [NonSerialized] private History _history;
        [NonSerialized] private int _historyOffset;
        [SerializeField] private string _logRef;
        [NonSerialized] private List<(string Label, string Ref, string Name)> _choices;
        [NonSerialized] private string[] _choiceLabels;
        [NonSerialized] private HashSet<string> _foreign;
        [NonSerialized] private GitPushTarget? _pushTarget;
        [NonSerialized] private int _generation = -1;
        [NonSerialized] private string _filesFor;
        [SerializeField] private int _loadedCount;

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

        public static void ShowWindow() => Open(null, false, false, "Git log");

        public static void ShowHistory(string file, bool withMeta, bool folder, string local) =>
            Open(file, withMeta, folder, $"History: {Git.FileName(local ?? file)}", local);

        private static void Open(string file, bool withMeta, bool folder, string title, string local = null)
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
            }

            window.titleContent = new GUIContent(title);
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
            public GitPushTarget? Target;
            public HashSet<string> Foreign = new HashSet<string>();
        }

        private static FirstPage ReadFirstPage(Git git, string file, bool withMeta, bool folder, string logRef,
            int count, History cached)
        {
            var branch = git.CurrentBranch();
            if (file == null)
            {
                var page = new FirstPage { Branch = branch, Branches = git.ReadBranches(false).Branches };
                var tip = logRef != null && logRef != "refs/heads/" + branch ? git.CommitOf(logRef) : null;
                page.LogRef = tip != null ? logRef : null;
                page.Target = git.PushTargetOf(page.LogRef);
                var log = git.ReadLogPage(tip, 0, count, page.Target);
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
                var log = git.ReadLogPage(null, 0, 0, null);
                return new FirstPage { Result = log.Result, Commits = log.Commits, Head = log.Head, Branch = branch };
            }

            var remotes = git.RemoteRefs();
            var history = cached != null && cached.Head == head && cached.Remotes == remotes &&
                          cached.Branch == branch
                ? cached
                : null;
            if (history == null)
            {
                var read = git.FileHistory(head, file, withMeta, followRenames: !folder);
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

        private void OnEnable()
        {
            _historyFile = NullIfEmpty(_historyFile);
            _historyLocal = NullIfEmpty(_historyLocal);
            _logRef = NullIfEmpty(_logRef);
            _selected = NullIfEmpty(_selected);
            _selectedFile = NullIfEmpty(_selectedFile);
            GitOperations.RepositoryChanged += Refresh;
        }

        private static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

        private void OnDisable() => GitOperations.RepositoryChanged -= Refresh;

        private void OnFocus()
        {
            if (_generation != GitOperations.Generation)
            {
                Refresh();
            }
        }

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
            _generation = GitOperations.Generation;

            var git = _git;
            var count = Math.Max(PageSize, _loadedCount + (_loadingMore ? PageSize : 0));
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
                        _pushTarget = state.Target;
                        SetChoices(state.Branches);
                    }

                    if (!state.Result.IsSuccess)
                    {
                        _commits = null;
                        _head = null;
                        _history = null;
                        _aborted = state.Result.Message;
                        _generation = -1;
                        Repaint();
                        return;
                    }

                    _commits = state.Commits;
                    _head = state.Head;
                    _history = state.History;
                    _foreign = state.Foreign;
                    _historyOffset = _history != null ? Math.Min(count, _history.Commits.Count) : 0;
                    _hasMore = _history != null ? _historyOffset < _history.Commits.Count : _commits.Count == count;
                    _loadedCount = _history != null ? _historyOffset : _commits.Count;
                    _moreAborted = null;

                    if (_selected != null && _commits.Any(commit => commit.Hash == _selected))
                    {
                        if (_files == null && _filesFor != _selected)
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
                    _generation = -1;
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
            var target = _pushTarget;
            GitOperations.Read("Git: reading older commits", () =>
            {
                var read = history != null
                    ? git.ReadCommits(history.Commits.Skip(offset).Take(PageSize).ToList(), history.Unpushed)
                    : ToPage(git.ReadLogPage(head, skip, PageSize, target));
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

                        _loadedCount = history != null ? _historyOffset : _commits.Count;
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
            _filesAborted = null;
            _filesFor = hash;

            var git = _git;
            GitOperations.Read("Git: reading the commit", () => git.CommitFiles(hash), read =>
            {
                if (_filesFor == hash)
                {
                    _filesFor = null;
                }

                if (!this || _selected != hash)
                {
                    return;
                }

                if (!read.Result.IsSuccess)
                {
                    _filesAborted = read.Result.Message;
                    Repaint();
                    return;
                }

                var files = read.Files;
                var paths = OwnNames(hash);
                var folder = _historyFolder ? _historyFile + "/" : null;
                bool Own(string path) => path != null &&
                                         (paths.Contains(path) ||
                                          folder != null && path.StartsWith(folder, StringComparison.Ordinal));
                var own = paths == null
                    ? files
                    : files.Where(file => Own(file.Path)).ToList();
                _files = own.Count > 0 ? own : files;
                if (!_files.Exists(file => file.Path == _selectedFile))
                {
                    _selectedFile = null;
                }

                _filesScrollPos = Vector2.zero;
                Repaint();
            }, _ =>
            {
                if (_filesFor == hash)
                {
                    _filesFor = null;
                }

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
            _choices = new List<(string Label, string Ref, string Name)>
            {
                ($"{Texts.MenuLabel(_branch ?? "HEAD")} (current)", null, _branch)
            };
            if (branches != null)
            {
                _choices.AddRange(branches
                    .Where(branch => !branch.IsCurrent)
                    .OrderBy(branch => branch.IsRemote)
                    .ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(branch => (Texts.MenuLabel(branch.Name), branch.Ref, branch.Name)));
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

                if (_logRef != null && GUILayout.Button("Compare with Current", GUILayout.ExpandWidth(false)))
                {
                    var reference = _logRef;
                    var name = _choices[index].Ref == reference ? _choices[index].Name : reference;
                    EditorApplication.delayCall += () => CompareBranchWindow.ShowWindow(reference, name);
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void ShowBranch(string reference)
        {
            _logRef = reference;
            _loadedCount = 0;
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
            if (_commits != null)
            {
                _scrollPos = scroll.scrollPosition;
            }

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

            HistoryRows.Commit(rect, commit);
        }

        private void ShowCommitMenu(GitCommit commit)
        {
            var menu = new GenericMenu();
            var foreign = _foreign != null && _foreign.Contains(commit.Hash);
            Menus.Add(menu, "Cherry-Pick", foreign, () => ApplyCommit(commit, false));
            Menus.Add(menu, "Revert Commit", !foreign, () => ApplyCommit(commit, true));
            menu.AddSeparator("");
            Menus.Add(menu, "New Tag...", true, () => NewTag(commit));
            foreach (var tag in commit.Tags)
            {
                var name = tag;
                Menus.Add(menu, $"Delete Tag '{Texts.MenuLabel(name)}'", true, () => DeleteTag(name));
            }

            menu.ShowAsContext();
        }

        private void ApplyCommit(GitCommit commit, bool revert)
        {
            var git = _git ??= new Git();
            GitIdentityWindow.Ensure(git, () => GitOperations.Run(
                revert ? $"Git: reverting {commit.Hash}" : $"Git: cherry-picking {commit.Hash}",
                () => revert ? git.Revert(commit.Hash) : git.CherryPick(commit.Hash),
                Notification.Show,
                changesTree: true));
        }

        private void NewTag(GitCommit commit)
        {
            var git = _git ??= new Git();
            var answer = NewTagWindow.Ask($"{commit.Hash} {commit.Subject}");
            if (answer == null)
            {
                return;
            }

            var (name, message) = answer.Value;
            void Create() => GitOperations.Run($"Git: creating tag {name}",
                () => git.CreateTag(name, commit.Hash, message), Notification.Show);

            if (string.IsNullOrWhiteSpace(message))
            {
                Create();
            }
            else
            {
                GitIdentityWindow.Ensure(git, Create);
            }
        }

        private void DeleteTag(string name)
        {
            var git = _git ??= new Git();
            GitOperations.Read("Git: reading the remote", () => git.TagRemote(), found =>
            {
                var question = $"Delete tag '{name}'?";
                var remote = found.Remote;
                var choice = remote == null
                    ? EditorUtility.DisplayDialog("Delete tag",
                        found.Problem != null ? $"{question}\n\n{found.Problem}, so it can be deleted only here." : question,
                        "Delete", "Cancel") ? 0 : 1
                    : EditorUtility.DisplayDialogComplex("Delete tag",
                        $"{question}\n\nDeleting it on {remote} removes it for everyone who fetches from there.",
                        "Delete Here", "Cancel", $"Delete Here and on {remote}");
                if (choice == 1)
                {
                    return;
                }

                GitOperations.Run($"Git: deleting tag {name}", () => git.DeleteTag(name, choice == 2 ? remote : null),
                    Notification.Show);
            });
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
                if (_filesAborted != null)
                {
                    EditorGUILayout.HelpBox(_filesAborted, MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.LabelField("Reading the commit...");
                }

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
                var path = file.Path;
                var present = file.Status != GitStatus.Deleted;
                HistoryRows.File(rect, file, path, ref _selectedFile, () => ShowDiff(commit, path), menu =>
                {
                    menu.AddItem(new GUIContent("Show Diff"), false, () => ShowDiff(commit, path));
                    Menus.Add(menu, "Compare with Local", present, () => CompareWithLocal(commit, path));
                    Menus.Add(menu, "Get This Version...", present, () => GetVersion(commit, path));
                }, Repaint);
            }
        }

        private static void ShowDiff(string commit, string path)
        {
            EditorApplication.delayCall += () => DiffWindow.ShowCommitWindow(commit, path);
        }

        private ICollection<string> OwnNames(string commit)
        {
            if (_history == null)
            {
                return null;
            }

            var full = _history.Commits.Find(hash => hash.StartsWith(commit, StringComparison.Ordinal));
            if (full == null || !_history.Names.TryGetValue(full, out var names))
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
            DiffWindow.ShowCompareWindow(commit, pathAtCommit, LocalPath(commit, pathAtCommit));
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
                Notification.Show, changesTree: true);
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
                        EditorApplication.delayCall += () => AmendCommitWindow.ShowWindow();
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
                }

                Notification.Show(result);
            });
        }
    }
}
