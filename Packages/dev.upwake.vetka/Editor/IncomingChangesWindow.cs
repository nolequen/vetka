using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Upwake.Vetka
{
    internal class IncomingChangesWindow : EditorWindow
    {
        private const int CommitLimit = 100;
        private const int FileLimit = 1000;

        [NonSerialized] private Git _git;
        [NonSerialized] private bool _loading;
        [NonSerialized] private bool _fetching;
        [NonSerialized] private bool _visible;
        [NonSerialized] private bool _skipRepositoryChange;
        [NonSerialized] private bool _operationRunning;
        [NonSerialized] private int _generation = -1;
        [NonSerialized] private GitIncoming _incoming;
        [NonSerialized] private IncomingCache _cache;
        [NonSerialized] private string _selectedCommit;
        [NonSerialized] private List<GitFileChange> _commitFiles;
        [NonSerialized] private string _commitFilesProblem;
        [NonSerialized] private UpdateStrategyChoice? _readStrategy;
        private string _selectedFile;
        private string _fetchNote;
        private Vector2 _scrollPos;

        public static void ShowWindow()
        {
            var window = GetWindow<IncomingChangesWindow>(false, "Incoming Changes", false);
            window.minSize = new Vector2(520, 360);
            window.Fetch();
            window.Focus();
        }

        private void OnEnable() => GitOperations.RepositoryChanged += OnRepositoryChanged;

        private void OnDisable() => GitOperations.RepositoryChanged -= OnRepositoryChanged;

        private void OnBecameInvisible() => _visible = false;

        private void OnBecameVisible()
        {
            if (_incoming != null && _generation != GitOperations.Generation)
            {
                Refresh();
            }
        }

        private void OnFocus()
        {
            if (_incoming != null && _generation != GitOperations.Generation)
            {
                Refresh();
            }
        }

        private void OnInspectorUpdate()
        {
            var running = GitOperations.GuardedTitle != null;
            if (running != _operationRunning)
            {
                _operationRunning = running;
                Repaint();
            }
        }

        private void OnRepositoryChanged()
        {
            if (_skipRepositoryChange)
            {
                _skipRepositoryChange = false;
                _generation = GitOperations.Generation;
                return;
            }

            if (!_visible)
            {
                _generation = -1;
                return;
            }

            Refresh();
        }

        private void Fetch()
        {
            if (_fetching)
            {
                return;
            }

            _git ??= new Git();
            _fetching = true;
            var git = _git;
            var strategy = GitProjectSettings.instance.UpdateStrategy;
            _readStrategy = strategy;
            var title = _incoming?.LocalUpstream == true || _incoming?.Upstream == null
                ? "Git: reading incoming changes"
                : $"Git: fetching {_incoming.Upstream}";
            GitOperations.Read(title, () =>
            {
                var fetched = git.FetchUpstream();
                return (fetched, read: fetched.IsCancelled ? null
                    : git.ReadIncoming(_cache, fetched.IsSuccess, strategy, CommitLimit, FileLimit));
            }, state =>
            {
                if (!this)
                {
                    return;
                }

                _fetching = false;
                _fetchNote = state.fetched.IsCancelled ? "Fetch cancelled"
                    : !state.fetched.IsSuccess ? $"Fetch failed, showing what the last fetch saw\n{state.fetched.Message}"
                    : state.fetched.Output.Length > 0 ? state.fetched.Output
                    : null;
                if (state.read != null)
                {
                    Show(state.read);
                    _skipRepositoryChange = true;
                }

                Repaint();
            }, reason =>
            {
                if (!this)
                {
                    return;
                }

                _fetching = false;
                _fetchNote = reason == GitOperations.CancelledReason ? "Fetch cancelled" : reason;
                Repaint();
            }, changesRefs: true);
        }

        private void Refresh()
        {
            if (_loading || _fetching)
            {
                return;
            }

            _git ??= new Git();
            _loading = true;
            _generation = GitOperations.Generation;
            var git = _git;
            var strategy = GitProjectSettings.instance.UpdateStrategy;
            _readStrategy = strategy;
            GitOperations.Read("Git: reading incoming changes",
                () => git.ReadIncoming(_cache, false, strategy, CommitLimit, FileLimit), read =>
                {
                    if (!this)
                    {
                        return;
                    }

                    _loading = false;
                    Show(read);
                    Repaint();
                }, reason =>
                {
                    if (!this)
                    {
                        return;
                    }

                    _loading = false;
                    _incoming ??= new GitIncoming { Result = GitResult.Failure(reason) };
                    _generation = -1;
                    Repaint();
                });
        }

        private void Show(GitIncoming read)
        {
            _incoming = read;
            _cache = read.Cache;
            if (!read.Result.IsSuccess)
            {
                _generation = -1;
            }

            if (_selectedCommit != null && read.Incoming.All(commit => commit.Hash != _selectedCommit))
            {
                _selectedCommit = null;
                _commitFiles = null;
                _commitFilesProblem = null;
            }
        }

        private void OnGUI()
        {
            _visible = true;
            if (!_loading && !_fetching &&
                (_incoming == null || _readStrategy != GitProjectSettings.instance.UpdateStrategy))
            {
                Refresh();
            }

            DrawHeader();
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scrollPos, GUILayout.ExpandHeight(true)))
            {
                _scrollPos = scroll.scrollPosition;
                if (_fetchNote != null)
                {
                    EditorGUILayout.HelpBox(_fetchNote, MessageType.Warning);
                }

                if (_incoming == null)
                {
                    EditorGUILayout.LabelField(_fetching ? "Fetching..." : "Reading incoming changes...");
                }
                else if (!_incoming.Result.IsSuccess)
                {
                    EditorGUILayout.HelpBox(_incoming.Result.Message, MessageType.Warning);
                }
                else
                {
                    DrawWarnings();
                    DrawCommits();
                    DrawFiles();
                }
            }

            DrawButtons();
        }

        private void DrawHeader()
        {
            var branch = _incoming?.Branch ?? "?";
            var upstream = _incoming?.Upstream ?? "?";
            var title = _incoming?.Branch == null ? "Incoming changes"
                : $"{branch} ← {upstream}{(_incoming.LocalUpstream ? " (local branch)" : "")}";
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            if (_incoming?.Summary != null)
            {
                var summary = _incoming.Action != null ? $"{_incoming.Summary}. {_incoming.Action}" : _incoming.Summary;
                EditorGUILayout.LabelField(summary, EditorStyles.wordWrappedMiniLabel);
            }

            var fetch = _fetching ? _incoming?.LocalUpstream == true ? "Reading..." : $"Fetching {upstream}..."
                : _loading ? "Reading..."
                : _incoming?.LastFetch != null ? $"Last fetch {Ago(_incoming.LastFetch.Value)}"
                : null;
            if (fetch != null)
            {
                EditorGUILayout.LabelField(fetch, EditorStyles.miniLabel);
            }
        }

        private void DrawWarnings()
        {
            foreach (var refusal in _incoming.Refusals)
            {
                EditorGUILayout.HelpBox(refusal, MessageType.Error);
            }

            foreach (var warning in _incoming.Warnings)
            {
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
            }

            var unsaved = UnsavedScenes().Where(path => _incoming.Files.Any(file => file.Path == path)).ToList();
            if (unsaved.Count > 0)
            {
                EditorGUILayout.HelpBox("These open scenes have unsaved changes and Update Project changes them. It " +
                                        "asks to save them first, then they are merged like other local changes:\n  " +
                                        string.Join("\n  ", unsaved), MessageType.Warning);
            }

            if (_incoming.Note != null)
            {
                EditorGUILayout.HelpBox(_incoming.Note, MessageType.Info);
            }
        }

        private void DrawCommits()
        {
            EditorGUILayout.LabelField($"Incoming commits ({_incoming.IncomingCount})", EditorStyles.boldLabel);
            if (_incoming.IncomingCount == 0)
            {
                EditorGUILayout.LabelField("No incoming commits", EditorStyles.miniLabel);
            }

            foreach (var commit in _incoming.Incoming)
            {
                var rect = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
                var current = Event.current;
                if (current.type == EventType.MouseDown && current.button == 0 && rect.Contains(current.mousePosition))
                {
                    SelectCommit(commit.Hash);
                    current.Use();
                }

                if (current.type == EventType.Repaint && commit.Hash == _selectedCommit)
                {
                    EditorGUI.DrawRect(rect, GitColors.Selection);
                }

                HistoryRows.Commit(rect, commit);
            }

            if (_incoming.IncomingCount > _incoming.Incoming.Count)
            {
                EditorGUILayout.LabelField($"...and {_incoming.IncomingCount - _incoming.Incoming.Count} more",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space();
        }

        private void SelectCommit(string hash)
        {
            if (hash == _selectedCommit)
            {
                _selectedCommit = null;
                _commitFiles = null;
                _commitFilesProblem = null;
                Repaint();
                return;
            }

            _selectedCommit = hash;
            _commitFiles = null;
            _commitFilesProblem = null;
            var git = _git ??= new Git();
            GitOperations.Read("Git: reading the commit", () => git.CommitFiles(hash), read =>
            {
                if (!this || _selectedCommit != hash)
                {
                    return;
                }

                if (read.Result.IsSuccess)
                {
                    _commitFiles = read.Files;
                }
                else
                {
                    _commitFilesProblem = read.Result.Message;
                }

                Repaint();
            }, _ =>
            {
                if (this && _selectedCommit == hash)
                {
                    _selectedCommit = null;
                    Repaint();
                }
            });
            Repaint();
        }

        private void DrawFiles()
        {
            if (_incoming.IncomingCount == 0)
            {
                return;
            }

            var commit = _selectedCommit;
            var files = commit == null ? _incoming.Files : _commitFiles;
            var count = commit == null ? _incoming.FileCount : files?.Count ?? 0;
            EditorGUILayout.LabelField(commit == null
                    ? $"Files Update Project changes ({count})"
                    : files == null ? $"Files of {commit}" : $"Files of {commit} ({count})",
                EditorStyles.boldLabel);
            if (files == null)
            {
                if (_commitFilesProblem != null)
                {
                    EditorGUILayout.HelpBox(_commitFilesProblem, MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.LabelField("Reading the commit...");
                }

                return;
            }

            if (files.Count == 0)
            {
                EditorGUILayout.LabelField("No files change", EditorStyles.miniLabel);
            }

            var unsaved = UnsavedScenes();
            foreach (var file in files.Take(FileLimit))
            {
                var rect = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
                var mark = _incoming.MarkOf(file);
                if (mark == IncomingMark.None && unsaved.Contains(file.Path))
                {
                    mark = IncomingMark.Unsaved;
                }

                var label = file.OldPath != null ? $"{file.Path}   (from {file.OldPath})" : file.Path;
                var shown = file;
                HistoryRows.File(rect, file, label, ref _selectedFile, () => ShowDiff(shown), menu =>
                {
                    menu.AddItem(new GUIContent("Show Diff"), false, () => ShowDiff(shown));
                    Menus.Add(menu, $"Compare {Texts.MenuLabel(_incoming.Upstream)} with Local",
                        shown.Status != GitStatus.Deleted, () => CompareWithLocal(shown));
                }, Repaint, MarkLabel(mark), MarkColor(mark));
            }

            var listed = Math.Min(files.Count, FileLimit);
            if (count > listed)
            {
                EditorGUILayout.LabelField($"...and {count - listed} more files", EditorStyles.miniLabel);
            }
        }

        private void ShowDiff(GitFileChange file)
        {
            var incoming = _incoming;
            var commit = _selectedCommit;
            if (incoming == null)
            {
                return;
            }

            EditorApplication.delayCall += () =>
            {
                if (commit != null)
                {
                    DiffWindow.ShowCommitWindow(commit, file.Path);
                }
                else if ((incoming.MarkOf(file) == IncomingMark.Conflict ||
                          incoming.MarkOf(file) == IncomingMark.MayConflict) && incoming.MergeBase != null)
                {
                    DiffWindow.ShowRangeWindow(incoming.MergeBase, incoming.Tip, file.Path, file.OldPath,
                        $"changed on {incoming.Upstream}");
                }
                else
                {
                    DiffWindow.ShowRangeWindow(incoming.DiffFrom, incoming.DiffTo, file.Path, file.OldPath,
                        incoming.DiffLabel);
                }
            };
        }

        private void CompareWithLocal(GitFileChange file)
        {
            var tip = _incoming?.Tip;
            if (tip != null)
            {
                DiffWindow.ShowCompareWindow(tip, file.Path, file.OldPath ?? file.Path);
            }
        }

        private void DrawButtons()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(_fetching))
            {
                var upstream = _incoming?.Upstream ?? "the upstream";
                var tooltip = _incoming?.LocalUpstream == true ? "Read again" : $"Fetch {upstream} and read again";
                if (GUILayout.Button(new GUIContent("Refresh", tooltip),
                        GUILayout.Width(80)))
                {
                    Fetch();
                }
            }

            var canUpdate = !_loading && !_fetching && GitOperations.GuardedTitle == null && _incoming != null &&
                            _incoming.Result.IsSuccess && _incoming.IncomingCount > 0;
            using (new EditorGUI.DisabledScope(!canUpdate))
            {
                if (GUILayout.Button("Update Project", GUILayout.Width(110)))
                {
                    EditorApplication.delayCall += GitMenu.UpdateProject;
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private HashSet<string> UnsavedScenes()
        {
            var scenes = new HashSet<string>(StringComparer.Ordinal);
            var prefix = _incoming?.Prefix ?? "";
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty && !string.IsNullOrEmpty(scene.path))
                {
                    scenes.Add(prefix + scene.path);
                }
            }

            return scenes;
        }

        private static string MarkLabel(IncomingMark mark) => mark switch
        {
            IncomingMark.Conflict => "conflict",
            IncomingMark.ConflictsWithYourChanges => "conflicts with your changes",
            IncomingMark.InTheWay => "in the way",
            IncomingMark.InUse => "in use",
            IncomingMark.HiddenChange => "hidden local change",
            IncomingMark.MayConflict => "may conflict",
            IncomingMark.ChangedLocally => "changed locally",
            IncomingMark.Unsaved => "unsaved in the editor",
            _ => null
        };

        private static Color MarkColor(IncomingMark mark) =>
            mark == IncomingMark.Conflict || mark == IncomingMark.ConflictsWithYourChanges
                ? GitColors.Danger
                : GitColors.Caution;

        private static string Ago(DateTime time)
        {
            var passed = DateTime.Now - time;
            return passed.TotalMinutes < 1 ? "just now"
                : passed.TotalHours < 1 ? $"{(int)passed.TotalMinutes} min ago"
                : passed.TotalDays < 1 ? $"{(int)passed.TotalHours} h ago"
                : time.ToString("yyyy-MM-dd HH:mm");
        }
    }
}
