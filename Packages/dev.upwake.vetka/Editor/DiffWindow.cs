using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class DiffWindow : EditorWindow
    {
        internal enum LineKind
        {
            Context,
            Added,
            Removed,
            Hunk,
            Info
        }

        internal readonly struct DiffLine
        {
            public DiffLine(LineKind kind, string text, string oldNumber = "", string newNumber = "")
            {
                Kind = kind;
                Text = text;
                OldNumber = oldNumber;
                NewNumber = newNumber;
            }

            public LineKind Kind { get; }
            public string Text { get; }
            public string OldNumber { get; }
            public string NewNumber { get; }
        }

        private static readonly Regex HunkHeader = new Regex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");

        private const float NumberWidth = 44;

        [SerializeField] private string _path;
        [SerializeField] private bool _untracked;
        [SerializeField] private string _oldPath;
        [SerializeField] private string _commit;

        [NonSerialized] private Git _git;
        private List<DiffLine> _lines;
        private int _longestLine;
        [NonSerialized] private bool _loading;
        private Vector2 _scrollPos;
        private GUIStyle _textStyle;
        private GUIStyle _numberStyle;

        public static void ShowWindow(Git git, string path, bool untracked, string oldPath = null) =>
            Show(git, path, untracked, null, oldPath);

        public static void ShowCommitWindow(Git git, string commit, string path) => Show(git, path, false, commit);

        private static void Show(Git git, string path, bool untracked, string commit, string oldPath = null)
        {
            var window = GetWindow<DiffWindow>(utility: false, "Diff");
            window._git = git;
            window._path = path;
            window._untracked = untracked;
            window._commit = commit;
            window._oldPath = oldPath;
            window.minSize = new Vector2(500, 300);
            window.Refresh();
        }

        private void Refresh()
        {
            if (string.IsNullOrEmpty(_path))
            {
                return;
            }

            _git ??= new Git();
            _loading = true;

            var git = _git;
            var path = _path;
            var untracked = _untracked;
            var oldPath = string.IsNullOrEmpty(_oldPath) ? null : _oldPath;
            var commit = string.IsNullOrEmpty(_commit) ? null : _commit;
            GitOperations.Read(
                "Git: reading the diff",
                () => Parse(commit == null ? git.FileDiff(path, untracked, oldPath) : git.CommitFileDiff(commit, path)),
                lines => SetLines(path, commit, lines),
                reason => SetLines(path, commit, Parse(GitResult.Failure(reason))));
        }

        private void SetLines(string path, string commit, List<DiffLine> lines)
        {
            if (!this || path != _path || commit != (string.IsNullOrEmpty(_commit) ? null : _commit))
            {
                return;
            }

            _lines = lines;
            _longestLine = 0;
            foreach (var line in lines)
            {
                _longestLine = Mathf.Max(_longestLine, line.Text.Length);
            }

            _scrollPos = Vector2.zero;
            _loading = false;
            Repaint();
        }

        internal static List<DiffLine> Parse(GitResult result)
        {
            var lines = new List<DiffLine>();
            if (!result.IsSuccess)
            {
                foreach (var line in result.Message.Split('\n'))
                {
                    lines.Add(new DiffLine(LineKind.Info, line.TrimEnd('\r')));
                }

                return lines;
            }

            if (string.IsNullOrEmpty(result.Output))
            {
                lines.Add(new DiffLine(LineKind.Info, "No differences"));
                return lines;
            }

            var inHunk = false;
            var oldNumber = 0;
            var newNumber = 0;
            foreach (var rawLine in result.Output.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Replace("\t", "    ");

                if (line.StartsWith("diff --git "))
                {
                    if (lines.Count > 0)
                    {
                        lines.Add(new DiffLine(LineKind.Info, line));
                    }

                    inHunk = false;
                    continue;
                }

                var hunk = HunkHeader.Match(line);
                if (hunk.Success)
                {
                    inHunk = true;
                    oldNumber = int.Parse(hunk.Groups[1].Value);
                    newNumber = int.Parse(hunk.Groups[2].Value);
                    lines.Add(new DiffLine(LineKind.Hunk, line));
                    continue;
                }

                if (!inHunk)
                {
                    if (!line.StartsWith("diff --git") && !line.StartsWith("index ") &&
                        !line.StartsWith("--- ") && !line.StartsWith("+++ "))
                    {
                        lines.Add(new DiffLine(LineKind.Info, line));
                    }

                    continue;
                }

                var text = line.Length > 0 ? line.Substring(1) : "";
                switch (line.Length > 0 ? line[0] : ' ')
                {
                    case '+':
                        lines.Add(new DiffLine(LineKind.Added, text, "", (newNumber++).ToString()));
                        break;
                    case '-':
                        lines.Add(new DiffLine(LineKind.Removed, text, (oldNumber++).ToString()));
                        break;
                    case '\\':
                        lines.Add(new DiffLine(LineKind.Info, line));
                        break;
                    default:
                        lines.Add(new DiffLine(LineKind.Context, text, (oldNumber++).ToString(),
                            (newNumber++).ToString()));
                        break;
                }
            }

            return lines;
        }

        private void OnGUI()
        {
            if (_lines == null && !_loading)
            {
                Refresh();
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label(string.IsNullOrEmpty(_commit) ? _path ?? "" : $"{_path}   @ {_commit}",
                EditorStyles.toolbarButton);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(_loading))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton))
                {
                    Refresh();
                }
            }

            EditorGUILayout.EndHorizontal();

            if (_lines == null)
            {
                EditorGUILayout.LabelField("Reading the diff...");
                return;
            }

            DrawLines();
        }

        private void DrawLines()
        {
            EnsureStyles();

            var lineHeight = Mathf.Ceil(_textStyle.lineHeight) + 2;
            var charWidth = _textStyle.CalcSize(new GUIContent("M")).x;
            var textX = NumberWidth * 2 + 8;
            var contentWidth = Mathf.Max(position.width - 16, textX + _longestLine * charWidth + 16);

            _scrollPos = GUILayout.BeginScrollView(_scrollPos);
            var area = GUILayoutUtility.GetRect(contentWidth, _lines.Count * lineHeight);
            if (Event.current.type != EventType.Repaint)
            {
                GUILayout.EndScrollView();
                return;
            }

            var first = Mathf.Max(0, (int)(_scrollPos.y / lineHeight));
            var last = Mathf.Min(_lines.Count, first + (int)(position.height / lineHeight) + 2);
            for (var i = first; i < last; i++)
            {
                var line = _lines[i];
                var rect = new Rect(area.x, area.y + i * lineHeight, contentWidth, lineHeight);

                var background = Background(line.Kind);
                if (background.a > 0)
                {
                    EditorGUI.DrawRect(rect, background);
                }

                GUI.Label(new Rect(rect.x, rect.y, NumberWidth, lineHeight), line.OldNumber, _numberStyle);
                GUI.Label(new Rect(rect.x + NumberWidth, rect.y, NumberWidth, lineHeight), line.NewNumber,
                    _numberStyle);
                GUI.Label(new Rect(rect.x + textX, rect.y, contentWidth - textX, lineHeight), line.Text, _textStyle);
            }

            GUILayout.EndScrollView();
        }

        private void EnsureStyles()
        {
            if (_textStyle != null)
            {
                return;
            }

            var font = EditorGUIUtility.Load("Fonts/RobotoMono/RobotoMono-Regular.ttf") as Font;

            _textStyle = new GUIStyle(EditorStyles.label)
            {
                font = font,
                fontSize = 12,
                wordWrap = false,
                richText = false,
                padding = new RectOffset(0, 0, 1, 1)
            };

            _numberStyle = new GUIStyle(_textStyle)
            {
                alignment = TextAnchor.MiddleRight,
                padding = new RectOffset(0, 6, 1, 1),
                normal = { textColor = Color.gray }
            };
        }

        private static Color Background(LineKind kind)
        {
            var dark = EditorGUIUtility.isProSkin;
            return kind switch
            {
                LineKind.Added => dark ? new Color(0.2f, 0.45f, 0.2f, 0.45f) : new Color(0.7f, 0.95f, 0.7f, 0.8f),
                LineKind.Removed => dark ? new Color(0.55f, 0.2f, 0.2f, 0.45f) : new Color(1f, 0.75f, 0.75f, 0.8f),
                LineKind.Hunk => dark ? new Color(0.25f, 0.35f, 0.55f, 0.35f) : new Color(0.8f, 0.87f, 1f, 0.8f),
                _ => Color.clear
            };
        }
    }
}
