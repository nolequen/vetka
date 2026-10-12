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

        [SerializeField] private string _path;
        [SerializeField] private string _oldPath;
        [SerializeField] private string _commit;
        [SerializeField] private bool _compare;
        [SerializeField] private string _from;
        [SerializeField] private string _label;

        [NonSerialized] private Git _git;
        private List<DiffLine> _lines;
        private int _longestLine;
        [NonSerialized] private bool _loading;
        private Vector2 _scrollPos;

        public static void ShowWindow(string path, string oldPath = null) => Show(path, null, oldPath);

        public static void ShowCommitWindow(string commit, string path) => Show(path, commit);

        public static void ShowCompareWindow(string commit, string pathAtCommit, string localPath) =>
            Show(localPath, commit, pathAtCommit, true);

        public static void ShowRangeWindow(string from, string to, string path, string oldPath, string label) =>
            Show(path, to, oldPath, false, from, label);

        private static void Show(string path, string commit, string oldPath = null, bool compare = false,
            string from = null, string label = null)
        {
            var window = GetWindow<DiffWindow>(utility: false, "Diff");
            window._path = path;
            window._commit = commit;
            window._oldPath = oldPath;
            window._compare = compare;
            window._from = from;
            window._label = label;
            window._lines = null;
            window._scrollPos = Vector2.zero;
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
            var oldPath = string.IsNullOrEmpty(_oldPath) ? null : _oldPath;
            var commit = string.IsNullOrEmpty(_commit) ? null : _commit;
            var compare = _compare;
            var from = string.IsNullOrEmpty(_from) ? null : _from;
            GitOperations.Read(
                "Git: reading the diff",
                () => Parse(from != null ? git.RangeFileDiff(from, commit, path, oldPath)
                    : compare ? git.CompareWithLocal(commit, oldPath ?? path, path)
                    : commit == null ? git.FileDiff(path, oldPath)
                    : git.CommitFileDiff(commit, path)),
                lines => SetLines(path, commit, compare, from, lines),
                reason => SetLines(path, commit, compare, from, Parse(GitResult.Failure(reason))));
        }

        private void SetLines(string path, string commit, bool compare, string from, List<DiffLine> lines)
        {
            if (!this || path != _path || commit != (string.IsNullOrEmpty(_commit) ? null : _commit) ||
                compare != _compare || from != (string.IsNullOrEmpty(_from) ? null : _from))
            {
                return;
            }

            _lines = lines;
            _longestLine = 0;
            foreach (var line in lines)
            {
                _longestLine = Mathf.Max(_longestLine, line.Text.Length);
            }

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
            GUILayout.Label(!string.IsNullOrEmpty(_from) ? $"{_path}   {_label}"
                : string.IsNullOrEmpty(_commit) ? _path ?? ""
                : _compare ? $"{_oldPath ?? _path}   @ {_commit}   vs   local {_path}"
                : $"{_path}   @ {_commit}", EditorStyles.toolbarButton);
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
            const float numberWidth = CodeView.NumberWidth;
            const float textX = numberWidth * 2 + 8;
            CodeView.Draw(position, ref _scrollPos, _lines.Count, _longestLine, textX, (i, rect) =>
            {
                var line = _lines[i];
                var background = Background(line.Kind);
                if (background.a > 0)
                {
                    EditorGUI.DrawRect(rect, background);
                }

                GUI.Label(new Rect(rect.x, rect.y, numberWidth, rect.height), line.OldNumber, CodeView.Number);
                GUI.Label(new Rect(rect.x + numberWidth, rect.y, numberWidth, rect.height), line.NewNumber,
                    CodeView.Number);
                GUI.Label(new Rect(rect.x + textX, rect.y, rect.width - textX, rect.height), line.Text, CodeView.Text);
            });
        }

        private static Color Background(LineKind kind)
        {
            var dark = EditorGUIUtility.isProSkin;
            return kind switch
            {
                LineKind.Added => dark ? new Color(0.2f, 0.45f, 0.2f, 0.45f) : new Color(0.7f, 0.95f, 0.7f, 0.8f),
                LineKind.Removed => dark ? new Color(0.55f, 0.2f, 0.2f, 0.45f) : new Color(1f, 0.75f, 0.75f, 0.8f),
                LineKind.Hunk => GitColors.Highlight,
                _ => Color.clear
            };
        }
    }
}
