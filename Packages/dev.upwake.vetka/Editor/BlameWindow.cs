using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class BlameWindow : EditorWindow
    {
        private const int AuthorLength = 18;

        [SerializeField] private string _path;
        [SerializeField] private string _localPath;
        [SerializeField] private string _revision;

        [NonSerialized] private Git _git;
        private List<GitBlameLine> _lines;
        private List<GUIContent> _annotations;
        private List<bool> _evenBlock;
        private string _error;
        private int _longestLine;
        [NonSerialized] private bool _loading;
        private Vector2 _scrollPos;

        private static readonly SkinStyle AnnotationStyle = new SkinStyle(() => new GUIStyle(EditorStyles.label)
        {
            fontSize = 11,
            wordWrap = false,
            clipping = TextClipping.Clip,
            padding = new RectOffset(6, 4, 1, 1),
            normal = { textColor = Color.gray }
        });

        public static void ShowWindow(string path)
        {
            var window = GetWindow<BlameWindow>(utility: false, "Blame");
            window._localPath = path;
            window.minSize = new Vector2(500, 300);
            window.wantsMouseMove = true;
            window.ShowRevision(null, path);
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
            var revision = string.IsNullOrEmpty(_revision) ? null : _revision;
            GitOperations.Read("Git: reading the blame", () => git.Blame(path, revision),
                blame => ShowLines(path, revision, blame.Result, blame.Lines),
                reason => ShowLines(path, revision, GitResult.Failure(reason), new List<GitBlameLine>()));
        }

        private void ShowRevision(string revision, string path)
        {
            _revision = revision;
            _path = path;
            _lines = null;
            _scrollPos = Vector2.zero;
            _error = null;
            Refresh();
        }

        private void ShowLines(string path, string revision, GitResult result, List<GitBlameLine> lines)
        {
            if (!this || path != _path || revision != (string.IsNullOrEmpty(_revision) ? null : _revision))
            {
                return;
            }

            _error = result.IsSuccess ? null : result.Message;
            SetLines(lines);
            _loading = false;
            Repaint();
        }

        private void SetLines(List<GitBlameLine> lines)
        {
            _lines = lines;
            _annotations = new List<GUIContent>(lines.Count);
            _evenBlock = new List<bool>(lines.Count);
            _longestLine = 0;

            var even = false;
            string previous = null;
            foreach (var line in lines)
            {
                _longestLine = Mathf.Max(_longestLine, line.Text.Length);
                var first = line.Hash != previous;
                if (first)
                {
                    even = !even;
                    previous = line.Hash;
                }

                _evenBlock.Add(even);
                _annotations.Add(first ? Annotation(line) : GUIContent.none);
            }
        }

        private static GUIContent Annotation(GitBlameLine line)
        {
            if (!line.IsCommitted)
            {
                return new GUIContent("Not committed", "Local changes that are not committed yet");
            }

            var author = Texts.Shortened(line.Author ?? "", AuthorLength);

            var date = line.Date.ToString("yyyy-MM-dd");
            return new GUIContent($"{date}  {author}",
                $"{line.Hash.Substring(0, 8)}  {line.Author}  {line.Date:yyyy-MM-dd HH:mm}\n{line.Summary}");
        }

        private void OnGUI()
        {
            if (_lines == null && !_loading)
            {
                Refresh();
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            var revision = string.IsNullOrEmpty(_revision) ? null : _revision;
            GUILayout.Label(revision == null ? _path ?? "" : $"{_path}   @ {Short(revision)}",
                EditorStyles.toolbarButton);
            GUILayout.FlexibleSpace();
            if (revision != null && !string.IsNullOrEmpty(_localPath) &&
                GUILayout.Button("Local", EditorStyles.toolbarButton))
            {
                var local = _localPath;
                EditorApplication.delayCall += () =>
                {
                    if (this)
                    {
                        ShowRevision(null, local);
                    }
                };
            }

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
                EditorGUILayout.LabelField("Reading the blame...", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            if (_error != null)
            {
                EditorGUILayout.HelpBox(_error, MessageType.Info);
                return;
            }

            DrawLines();
        }

        private void DrawLines()
        {
            var annotationWidth =
                AnnotationStyle.Style.CalcSize(new GUIContent("0000-00-00  " + new string('W', 12))).x;
            var textX = annotationWidth + CodeView.NumberWidth + 8;
            var current = Event.current;
            CodeView.Draw(position, ref _scrollPos, _lines.Count, _longestLine, textX, (i, rect) =>
            {
                var line = _lines[i];
                var annotationRect = new Rect(rect.x, rect.y, annotationWidth, rect.height);

                if (current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(annotationRect, AnnotationBackground(line, _evenBlock[i]));
                }

                var annotation = _annotations[i];
                if (annotation != GUIContent.none && line.IsCommitted)
                {
                    EditorGUIUtility.AddCursorRect(annotationRect, MouseCursor.Link);
                    if (current.type == EventType.MouseDown && current.button == 0 &&
                        annotationRect.Contains(current.mousePosition))
                    {
                        var hash = line.Hash;
                        var fileName = line.FileName;
                        EditorApplication.delayCall += () => DiffWindow.ShowCommitWindow(hash, fileName);
                        current.Use();
                    }
                }

                if (line.IsCommitted && annotationRect.Contains(current.mousePosition))
                {
                    if (current.type == EventType.MouseDown && current.button == 1)
                    {
                        current.Use();
                    }
                    else if (current.type == EventType.ContextClick)
                    {
                        ShowRevisionMenu(line);
                        current.Use();
                    }
                }

                GUI.Label(annotationRect, annotation, AnnotationStyle);
                GUI.Label(new Rect(rect.x + annotationWidth, rect.y, CodeView.NumberWidth, rect.height),
                    line.Number.ToString(), CodeView.Number);
                GUI.Label(new Rect(rect.x + textX, rect.y, rect.width - textX, rect.height), line.Text, CodeView.Text);
            });
        }

        private void ShowRevisionMenu(GitBlameLine line)
        {
            var hash = line.Hash;
            var fileName = line.FileName;
            var previous = line.Previous;
            var previousFileName = line.PreviousFileName;
            var menu = new GenericMenu();
            Menus.Add(menu, "Show Diff", true, () => DiffWindow.ShowCommitWindow(hash, fileName));
            Menus.Add(menu, $"Blame Revision {Short(hash)}", true, () => ShowRevisionLater(hash, fileName));
            Menus.Add(menu, "Blame Previous Revision", previous != null,
                () => ShowRevisionLater(previous, previousFileName));
            menu.ShowAsContext();
        }

        private void ShowRevisionLater(string revision, string path)
        {
            if (this)
            {
                ShowRevision(revision, path);
            }
        }

        private static string Short(string hash) => hash.Length > 8 ? hash.Substring(0, 8) : hash;

        private static Color AnnotationBackground(GitBlameLine line, bool even)
        {
            var dark = EditorGUIUtility.isProSkin;
            if (!line.IsCommitted)
            {
                return GitColors.Highlight;
            }

            return even
                ? dark ? new Color(1f, 1f, 1f, 0.04f) : new Color(0f, 0f, 0f, 0.04f)
                : dark ? new Color(1f, 1f, 1f, 0.08f) : new Color(0f, 0f, 0f, 0.08f);
        }
    }
}
