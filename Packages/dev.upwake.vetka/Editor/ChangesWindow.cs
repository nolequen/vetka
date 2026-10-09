using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class ChangesWindow : EditorWindow
    {
        private sealed class FileEntry
        {
            public string Path;
            public string OldPath;
            public GitStatus Status;
            public bool Checked;
            public GUIContent Name;
            public GUIContent Note;
            public GUIContent Directory;
        }

        private sealed class Row
        {
            public FileEntry Entry;
            public bool Unversioned;
        }

        private List<FileEntry> _changes;
        private List<FileEntry> _unversioned;
        private List<Row> _rows = new List<Row>();
        [NonSerialized] private Git _git;
        private Vector2 _filesScrollPos = Vector2.up;
        [SerializeField] private string _message = "";
        [SerializeField] private float _messageHeight = 60;
        [NonSerialized] private float _dragOffset;
        [NonSerialized] private string _aborted;
        [NonSerialized] private bool _committing;
        [NonSerialized] private bool _pushing;
        private readonly HashSet<string> _selected = new HashSet<string>();
        private string _anchor;
        [SerializeField] private bool _changesExpanded = true;
        [SerializeField] private bool _unversionedExpanded = true;

        private const float Indent = 14;
        private const string MessageControl = "CommitMessage";

        private static readonly Color Grey = new Color(0.5f, 0.5f, 0.5f);

        private static GUIStyle _foldoutStyle;
        private static GUIStyle _greyStyle;
        private static GUIStyle _hintStyle;
        private static GUIStyle _messageStyle;
        private static GUIStyle _paddingStyle;

        private static GUIStyle FoldoutStyle => _foldoutStyle ??= new GUIStyle(EditorStyles.foldout)
        {
            fontStyle = FontStyle.Bold
        };

        private static GUIStyle GreyStyle => _greyStyle ??= new GUIStyle(EditorStyles.label)
        {
            normal = { textColor = Grey }
        };

        private static GUIStyle HintStyle => _hintStyle ??= new GUIStyle(EditorStyles.label)
        {
            normal = { textColor = Grey },
            alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(4, 4, 3, 0)
        };

        private static GUIStyle MessageStyle => _messageStyle ??= new GUIStyle(EditorStyles.textArea)
        {
            wordWrap = true,
            padding = new RectOffset(EditorStyles.textArea.padding.left, 22, EditorStyles.textArea.padding.top,
                EditorStyles.textArea.padding.bottom)
        };

        private static GUIStyle PaddingStyle => _paddingStyle ??= new GUIStyle
        {
            padding = new RectOffset(6, 6, 4, 6)
        };

        private static Color SeparatorColor =>
            EditorGUIUtility.isProSkin ? new Color(0.1f, 0.1f, 0.1f) : new Color(0.6f, 0.6f, 0.6f);

        private static GUIContent _refreshContent;

        private static GUIContent RefreshContent =>
            _refreshContent ??= new GUIContent(EditorGUIUtility.IconContent("Refresh").image, "Refresh");

        private void ShowButton(Rect rect)
        {
            if (GUI.Button(rect, RefreshContent, EditorStyles.iconButton))
            {
                _aborted = null;
                Refresh();
            }
        }

        public static void ShowWindow(Git git, string message = null)
        {
            var window = Open(focus: true);
            window._git = git;
            if (message != null)
            {
                window._message = message;
            }

            Refresh();
        }

        internal static void Dock() => Open(focus: false);

        private static ChangesWindow Open(bool focus)
        {
            var window = GetWindow<ChangesWindow>("Changes", focus, DockTargets());
            window.titleContent = TitleContent;
            return window;
        }

        private static GUIContent TitleContent => new GUIContent("Changes", Icon("Changes"));

        private static readonly Dictionary<string, string> IconGuids = new Dictionary<string, string>
        {
            ["Changes"] = "04d82bace87f3fd458b5635f874b4643",
            ["d_Changes"] = "dced1513d6d34cf4d80839d9c970ced2",
            ["History"] = "46b537f0ae2a49a43be71bf02f9b53f3",
            ["d_History"] = "b9f2aa213cb613045a80e4609e33801e"
        };

        private static Texture2D Icon(string name) => AssetDatabase.LoadAssetAtPath<Texture2D>(
            AssetDatabase.GUIDToAssetPath(IconGuids[(EditorGUIUtility.isProSkin ? "d_" : "") + name]));

        private static GUIContent _historyContent;
        private static bool _historyProSkin;

        private static GUIContent HistoryContent
        {
            get
            {
                if (_historyContent == null || _historyContent.image == null ||
                    _historyProSkin != EditorGUIUtility.isProSkin)
                {
                    _historyContent = new GUIContent(Icon("History"), "Commit message history (Ctrl+M)");
                    _historyProSkin = EditorGUIUtility.isProSkin;
                }

                return _historyContent;
            }
        }

        internal static Type[] DockTargets()
        {
            var open = Resources.FindObjectsOfTypeAll<EditorWindow>().Select(window => window.GetType()).ToList();
            return new[] { "UnityEditor.ConsoleWindow", "UnityEditor.ProjectBrowser" }
                .Select(name => open.FirstOrDefault(type => type.FullName == name))
                .Where(type => type != null)
                .ToArray();
        }

        private void OnEnable()
        {
            titleContent = TitleContent;
            ProjectStatus.Updated += OnStatusUpdated;
            if (ProjectStatus.Loaded)
            {
                OnStatusUpdated();
            }
        }

        private void OnDisable() => ProjectStatus.Updated -= OnStatusUpdated;

        private void OnFocus() => Refresh();

        private void OnBecameVisible() => Refresh();

        private static void Refresh() => ProjectStatus.Refresh();

        private void OnStatusUpdated()
        {
            if (!ProjectStatus.Loaded)
            {
                _aborted = ProjectStatus.Aborted;
                if (_aborted != null)
                {
                    _changes = null;
                    _unversioned = null;
                    _rows = new List<Row>();
                }

                Repaint();
                return;
            }

            var changes = ProjectStatus.Current.Entries;
            var selection = ChangesSelection.instance;
            _changes = Entries(changes.Where(change => change.Status != GitStatus.Untracked), selection, true);
            var tracked = new HashSet<string>(_changes.Select(entry => entry.Path));
            _unversioned = Entries(changes.Where(change => change.Status == GitStatus.Untracked &&
                                                           !tracked.Contains(change.Path)), selection, false);
            var present = new HashSet<string>(_changes.Concat(_unversioned).Select(entry => entry.Path));
            selection.Retain(present, DateTime.UtcNow);
            _selected.IntersectWith(present);
            if (_anchor != null && !present.Contains(_anchor))
            {
                _anchor = null;
            }

            _aborted = null;
            RebuildRows();
            Repaint();
        }

        private static List<FileEntry> Entries(IEnumerable<GitFileChange> changes, ChangesSelection selection,
            bool checkedByDefault) =>
            changes
                .Select(change => Entry(change, selection.IsChecked(change.Path, checkedByDefault)))
                .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static FileEntry Entry(GitFileChange change, bool isChecked)
        {
            var tooltip = change.OldPath == null ? change.Path : $"{change.OldPath} → {change.Path}";
            var directory = DirectoryOf(change.Path);
            string note = null;
            if (change.OldPath != null)
            {
                var oldDirectory = DirectoryOf(change.OldPath);
                note = oldDirectory == directory
                    ? $"– renamed from {System.IO.Path.GetFileName(change.OldPath)}"
                    : $"– moved from {(oldDirectory.Length == 0 ? "the root" : oldDirectory + "/")}";
            }

            return new FileEntry
            {
                Path = change.Path,
                OldPath = change.OldPath,
                Status = change.Status,
                Checked = isChecked,
                Name = new GUIContent(System.IO.Path.GetFileName(change.Path), tooltip),
                Note = note == null ? null : new GUIContent(note, tooltip),
                Directory = directory.Length == 0 ? null : new GUIContent(directory, tooltip)
            };
        }

        private void RebuildRows()
        {
            var rows = new List<Row>();
            if (_changes.Count > 0)
            {
                rows.Add(new Row());
                if (_changesExpanded)
                {
                    rows.AddRange(_changes.Select(entry => new Row { Entry = entry }));
                }
            }

            if (_unversioned.Count > 0)
            {
                rows.Add(new Row { Unversioned = true });
                if (_unversionedExpanded)
                {
                    rows.AddRange(_unversioned.Select(entry => new Row { Entry = entry, Unversioned = true }));
                }
            }

            _rows = rows;
        }

        private void OnGUI()
        {
            if (_changes == null && _aborted == null)
            {
                if (ProjectStatus.Loaded)
                {
                    OnStatusUpdated();
                }
                else if (!ProjectStatus.Loading)
                {
                    Refresh();
                }
            }

            using (new EditorGUILayout.VerticalScope(PaddingStyle))
            {
                DrawFiles();
                DrawSplitter();
                DrawMessage();
                DrawButtons();
            }
        }

        private void DrawFiles()
        {
            using var scroll = new EditorGUILayout.ScrollViewScope(_filesScrollPos, GUILayout.ExpandHeight(true));
            _filesScrollPos = scroll.scrollPosition;

            if (_changes == null)
            {
                if (_aborted != null)
                {
                    EditorGUILayout.HelpBox($"{_aborted}\nPress Refresh to try again", MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.LabelField("Reading changes...", EditorStyles.centeredGreyMiniLabel);
                }

                return;
            }

            var rows = _rows;
            if (rows.Count == 0)
            {
                EditorGUILayout.LabelField("No changes", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var lineHeight = EditorGUIUtility.singleLineHeight;
            var rowHeight = lineHeight + EditorGUIUtility.standardVerticalSpacing;
            var area = GUILayoutUtility.GetRect(0, rows.Count * rowHeight, GUILayout.ExpandWidth(true));
            var first = Mathf.Max(0, (int)(_filesScrollPos.y / rowHeight));
            var last = Mathf.Min(rows.Count, first + (int)(position.height / rowHeight) + 2);
            for (var i = first; i < last; i++)
            {
                var rect = new Rect(area.x, area.y + i * rowHeight, area.width, lineHeight);
                var row = rows[i];
                if (row.Entry != null)
                {
                    DrawFile(rect, row.Entry);
                }
                else if (row.Unversioned)
                {
                    var expanded = DrawGroupHeader(rect, _unversionedExpanded, "Unversioned Files",
                        _unversioned.Count, () => FileActions.Add(_unversioned.Select(entry => entry.Path).ToList()));
                    if (expanded != _unversionedExpanded)
                    {
                        _unversionedExpanded = expanded;
                        RebuildRows();
                    }
                }
                else
                {
                    var expanded = DrawGroupHeader(rect, _changesExpanded, "Changes", _changes.Count, null);
                    if (expanded != _changesExpanded)
                    {
                        _changesExpanded = expanded;
                        RebuildRows();
                    }
                }
            }
        }

        private bool DrawGroupHeader(Rect rect, bool expanded, string title, int count, Action addAll)
        {
            var buttonRect = new Rect(rect.xMax - 56, rect.y + 1, 56, rect.height - 2);
            var foldoutRect = addAll == null ? rect : new Rect(rect.x, rect.y, buttonRect.x - rect.x - 4, rect.height);

            var label = new GUIContent(title);
            expanded = EditorGUI.Foldout(foldoutRect, expanded, label, true, FoldoutStyle);

            var countX = rect.x + FoldoutStyle.CalcSize(label).x + 6;
            GUI.Label(new Rect(countX, rect.y, Mathf.Max(0, foldoutRect.xMax - countX), rect.height),
                count == 1 ? "1 file" : $"{count} files", GreyStyle);

            if (addAll != null)
            {
                using (new EditorGUI.DisabledScope(_committing))
                {
                    if (GUI.Button(buttonRect, "Add All", EditorStyles.miniButton))
                    {
                        addAll();
                    }
                }
            }

            return expanded;
        }

        private const float MinMessageHeight = 36;

        private float MessageHeight(float wanted) =>
            Mathf.Clamp(wanted, MinMessageHeight, Mathf.Max(MinMessageHeight, position.height - 150));

        private void DrawSplitter()
        {
            var area = GUILayoutUtility.GetRect(1, 11, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(new Rect(area.x, area.y + 4, area.width, 1), SeparatorColor);
            EditorGUIUtility.AddCursorRect(area, MouseCursor.ResizeVertical);

            var id = GUIUtility.GetControlID(FocusType.Passive);
            var current = Event.current;
            switch (current.GetTypeForControl(id))
            {
                case EventType.MouseDown when current.button == 0 && area.Contains(current.mousePosition):
                    GUIUtility.hotControl = id;
                    _dragOffset = current.mousePosition.y + MessageHeight(_messageHeight);
                    current.Use();
                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == id:
                    _messageHeight = MessageHeight(_dragOffset - current.mousePosition.y);
                    current.Use();
                    Repaint();
                    break;
                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0;
                    current.Use();
                    break;
            }
        }

        private void DrawFile(Rect rect, FileEntry entry)
        {
            var toggleRect = new Rect(rect.x + Indent, rect.y, 16, rect.height);
            var labelRect = new Rect(toggleRect.xMax + 2, rect.y, Mathf.Max(0, rect.xMax - toggleRect.xMax - 2),
                rect.height);

            var current = Event.current;
            if (current.type == EventType.MouseDown && labelRect.Contains(current.mousePosition))
            {
                if (current.button == 0 && current.clickCount == 2)
                {
                    ShowDiff(entry);
                    current.Use();
                }
                else if (current.button == 0)
                {
                    Select(entry.Path, current.shift, EditorGUI.actionKey);
                    current.Use();
                    Repaint();
                }
                else if (current.button == 1)
                {
                    if (!_selected.Contains(entry.Path))
                    {
                        Select(entry.Path, false, false);
                    }

                    current.Use();
                    Repaint();
                }
            }

            if (current.type == EventType.ContextClick && labelRect.Contains(current.mousePosition))
            {
                ShowContextMenu();
                current.Use();
            }

            if (current.type == EventType.Repaint && _selected.Contains(entry.Path))
            {
                EditorGUI.DrawRect(rect, GitColors.Selection);
            }

            var value = EditorGUI.Toggle(toggleRect, entry.Checked);

            var x = DrawSegment(labelRect, labelRect.x, entry.Name,
                GitColors.StatusStyle(EditorStyles.label, entry.Status));

            if (entry.Note != null)
            {
                x = DrawSegment(labelRect, x, entry.Note, GreyStyle) + 4;
            }

            if (entry.Directory != null)
            {
                DrawSegment(labelRect, x, entry.Directory, GreyStyle);
            }

            if (value != entry.Checked)
            {
                entry.Checked = value;
                ChangesSelection.instance.Set(new[] { entry.Path }, value);
            }
        }

        private static string DirectoryOf(string path) =>
            System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";

        private static float DrawSegment(Rect row, float x, GUIContent content, GUIStyle style)
        {
            var width = Mathf.Min(style.CalcSize(content).x, row.xMax - x);
            if (width > 0)
            {
                GUI.Label(new Rect(x, row.y, width, row.height), content, style);
            }

            return x + Mathf.Max(0, width) + 2;
        }

        private void Select(string path, bool range, bool toggle)
        {
            if (range && _anchor != null)
            {
                var order = (_changesExpanded ? _changes : new List<FileEntry>())
                    .Concat(_unversionedExpanded ? _unversioned : new List<FileEntry>())
                    .Select(entry => entry.Path)
                    .ToList();
                var from = order.IndexOf(_anchor);
                var to = order.IndexOf(path);
                if (from >= 0 && to >= 0)
                {
                    if (!toggle)
                    {
                        _selected.Clear();
                    }

                    for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                    {
                        _selected.Add(order[i]);
                    }

                    return;
                }
            }

            if (toggle)
            {
                if (!_selected.Remove(path))
                {
                    _selected.Add(path);
                }
            }
            else
            {
                _selected.Clear();
                _selected.Add(path);
            }

            _anchor = path;
        }

        private void ShowContextMenu()
        {
            var all = (_changesExpanded ? _changes : new List<FileEntry>())
                .Concat(_unversionedExpanded ? _unversioned : new List<FileEntry>())
                .ToList();
            var selected = all.Where(item => _selected.Contains(item.Path)).Select(Change);
            var menu = new GenericMenu();
            FileActions.AddTo(menu, FileActions.ForSelection(selected, _committing));
            menu.ShowAsContext();
        }

        private static GitFileChange Change(FileEntry entry) => new GitFileChange(entry.Status, entry.Path, entry.OldPath);

        private static void ShowDiff(FileEntry entry)
        {
            var change = Change(entry);
            EditorApplication.delayCall += () => FileActions.ShowDiff(change);
        }

        private void DrawMessage()
        {
            var rect = GUILayoutUtility.GetRect(GUIContent.none, MessageStyle,
                GUILayout.Height(MessageHeight(_messageHeight)), GUILayout.ExpandWidth(true));
            var historyRect = new Rect(rect.xMax - 20, rect.y + 2, 18, 18);

            var current = Event.current;
            var shortcut = current.type == EventType.KeyDown && current.keyCode == KeyCode.M && EditorGUI.actionKey &&
                           GUI.GetNameOfFocusedControl() == MessageControl;
            if (shortcut || current.type == EventType.MouseDown && current.button == 0 &&
                historyRect.Contains(current.mousePosition))
            {
                ShowMessageHistory(shortcut ? new Rect(rect.x, rect.y, rect.width, 0) : historyRect);
                current.Use();
            }

            GUI.SetNextControlName(MessageControl);
            _message = EditorGUI.TextArea(rect, _message, MessageStyle);

            if (string.IsNullOrEmpty(_message) && GUI.GetNameOfFocusedControl() != MessageControl)
            {
                GUI.Label(rect, "Commit message", HintStyle);
            }

            GUI.Label(historyRect, HistoryContent, EditorStyles.iconButton);
        }

        private void ShowMessageHistory(Rect anchor)
        {
            var menu = new GenericMenu();
            var messages = CommitMessageHistory.instance.Messages;
            if (messages.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("No commit messages yet"));
            }

            foreach (var message in messages)
            {
                var chosen = message;
                menu.AddItem(new GUIContent(MenuText(message)), false, () =>
                {
                    _message = chosen;
                    GUIUtility.keyboardControl = 0;
                    Repaint();
                });
            }

            menu.DropDown(anchor);
        }

        private static string MenuText(string message)
        {
            var line = message.Split('\r', '\n')[0].Trim().Replace('/', '\u2215');
            return line.Length > 80 ? line.Substring(0, 77) + "..." : line;
        }

        private void DrawButtons()
        {
            var canCommit = !_committing &&
                            !string.IsNullOrWhiteSpace(_message) &&
                            _changes != null &&
                            (_changes.Exists(entry => entry.Checked) || _unversioned.Exists(entry => entry.Checked));

            GUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (_committing)
                {
                    DrawCommitting();
                }

                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!canCommit))
                {
                    if (GUILayout.Button("Commit", GUILayout.MinWidth(70), GUILayout.ExpandWidth(false)))
                    {
                        Commit(push: false);
                    }

                    if (GUILayout.Button("Commit and Push", GUILayout.ExpandWidth(false)))
                    {
                        Commit(push: true);
                    }
                }
            }
        }

        private void DrawCommitting()
        {
            var frame = (int)(EditorApplication.timeSinceStartup * 10) % 12;
            var spinner = EditorGUIUtility.IconContent($"WaitSpin{frame:00}").image;
            EditorGUIUtility.SetIconSize(new Vector2(16, 16));
            GUILayout.Label(new GUIContent(_pushing ? " Committing and pushing..." : " Committing...", spinner),
                GreyStyle, GUILayout.ExpandWidth(false));
            EditorGUIUtility.SetIconSize(Vector2.zero);
            Repaint();
        }

        private List<string> SelectedFiles() =>
            _changes == null
                ? new List<string>()
                : FileActions.WithOldPaths(_changes.Concat(_unversioned).Where(entry => entry.Checked).Select(Change));

        private static string CommitTitle(bool push) => push ? "Git: committing and pushing" : "Git: committing";

        private void Commit(bool push)
        {
            if (_committing)
            {
                return;
            }

            _committing = true;
            _pushing = push;
            Repaint();
            var git = _git ??= new Git();
            var files = SelectedFiles();
            var message = _message;
            var progress = GitOperations.StartProgress(CommitTitle(push), push);

            EditorApplication.delayCall += () =>
                GitIdentityWindow.Ensure(git, () => Commit(git, files, message, push, progress), () =>
                {
                    GitOperations.DropProgress(progress);
                    CommitStopped();
                });
        }

        private void CommitStopped()
        {
            if (!this)
            {
                return;
            }

            _committing = false;
            Repaint();
        }

        private void Commit(Git git, List<string> files, string message, bool push, int progress)
        {
            CommitMessageHistory.instance.Remember(message);
            var committed = false;
            GitOperations.Run(
                CommitTitle(push),
                () =>
                {
                    if (push)
                    {
                        return git.CommitAndPush(files, message, out committed);
                    }

                    var commit = git.Commit(files, message);
                    committed = commit.IsSuccess;
                    return commit;
                },
                result =>
                {
                    Notification.Show(result);

                    if (!this)
                    {
                        if (!committed)
                        {
                            ShowWindow(git, message);
                        }

                        return;
                    }

                    _committing = false;
                    if (committed)
                    {
                        ChangesSelection.instance.Forget(files);
                    }

                    if (committed && _message == message)
                    {
                        _message = "";
                        GUIUtility.keyboardControl = 0;
                    }

                    Repaint();
                },
                refreshAssets: true,
                reportsProgress: push,
                prepare: UnsavedChanges.SaveOrCancel,
                progressId: progress
            );
        }
    }
}
