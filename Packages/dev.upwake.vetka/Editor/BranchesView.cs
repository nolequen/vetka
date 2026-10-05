using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal sealed class BranchesView
    {
        private enum RowKind
        {
            Action,
            Separator,
            Header,
            Branch
        }

        private sealed class Row
        {
            public RowKind Kind;
            public string Label;
            public string Detail;
            public Action Activate;
            public GitBranch Branch;
        }

        private const float RowHeight = 20;
        private const string SearchField = "GitBranchesSearch";
        private const float HeaderHeight = 34;

        private readonly Git _git;
        private readonly Action _repaint;
        private bool _loading;
        private string _aborted;
        private Rect _area;
        private List<GitBranch> _branches;
        private List<string> _recent;
        private string _filter = "";
        private int _highlight = -1;
        private Vector2 _scrollPos;
        private bool _focused;
        private List<Row> _rows = new List<Row>();
        private float[] _offsets = new float[0];
        private float _contentHeight;
        private bool _rowsDirty = true;

        private GUIStyle _rowStyle;
        private GUIStyle _boldRowStyle;
        private GUIStyle _detailStyle;
        private GUIStyle _placeholderStyle;

        public BranchesView(Git git, Action repaint)
        {
            _git = git;
            _repaint = repaint;
        }

        private static bool RecentExpanded
        {
            get => SessionState.GetBool("Vetka.Branches.Recent", true);
            set => SessionState.SetBool("Vetka.Branches.Recent", value);
        }

        private static bool LocalExpanded
        {
            get => SessionState.GetBool("Vetka.Branches.Local", false);
            set => SessionState.SetBool("Vetka.Branches.Local", value);
        }

        private static bool RemoteExpanded
        {
            get => SessionState.GetBool("Vetka.Branches.Remote", false);
            set => SessionState.SetBool("Vetka.Branches.Remote", value);
        }

        public void Reload()
        {
            if (_loading)
            {
                return;
            }

            _loading = true;
            _aborted = null;
            var git = _git;
            GitOperations.Read(
                "Git: reading branches",
                () => (branches: git.ReadBranches(), recent: git.RecentBranches()),
                state =>
                {
                    _loading = false;
                    if (!state.branches.Result.IsSuccess)
                    {
                        _branches = null;
                        _aborted = state.branches.Result.Message;
                        _rowsDirty = true;
                        _repaint();
                        return;
                    }

                    _branches = state.branches.Branches;
                    _recent = state.recent;
                    _rowsDirty = true;
                    _repaint();
                },
                reason =>
                {
                    _loading = false;
                    _aborted = reason;
                    _rowsDirty = true;
                    _repaint();
                });
        }

        public void OnGUI(Rect rect)
        {
            _area = rect;
            EnsureStyles();
            if (_rowsDirty)
            {
                _rows = BuildRows();
                _offsets = Offsets(_rows, out _contentHeight);
                _rowsDirty = false;
            }

            HandleKeyboard();

            GUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(6);
            GUI.SetNextControlName(SearchField);
            var filter = EditorGUILayout.TextField(_filter, EditorStyles.toolbarSearchField);
            if (filter != _filter)
            {
                _filter = filter;
                _highlight = -1;
                _rowsDirty = true;
            }

            if (string.IsNullOrEmpty(_filter))
            {
                var fieldRect = GUILayoutUtility.GetLastRect();
                GUI.Label(new Rect(fieldRect.x + 16, fieldRect.y, fieldRect.width - 16, fieldRect.height),
                    "Search for branches and actions", _placeholderStyle);
            }

            GUILayout.Space(6);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(4);

            if (!_focused)
            {
                EditorGUI.FocusTextInControl(SearchField);
                _focused = true;
            }

            var highlight = _highlight;
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            var area = GUILayoutUtility.GetRect(1, _contentHeight, GUILayout.ExpandWidth(true));
            var visibleBottom = _scrollPos.y + _area.height;
            for (var i = 0; i < _rows.Count; i++)
            {
                var top = _offsets[i];
                var height = Height(_rows[i]);
                if (top + height < _scrollPos.y)
                {
                    continue;
                }

                if (top > visibleBottom)
                {
                    break;
                }

                DrawRow(i, _rows[i], new Rect(area.x, area.y + top, area.width, height));
            }

            if (_branches == null)
            {
                EditorGUILayout.LabelField("   " + (_loading ? "Reading branches..." : _aborted ?? "Reading branches..."),
                    _detailStyle);
            }

            EditorGUILayout.EndScrollView();

            if (Event.current.type == EventType.MouseMove && _highlight != highlight)
            {
                _repaint();
            }
        }

        private static float Height(Row row) => row.Kind == RowKind.Separator ? RowHeight / 2 : RowHeight;

        private static float[] Offsets(List<Row> rows, out float total)
        {
            var offsets = new float[rows.Count];
            total = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                offsets[i] = total;
                total += Height(rows[i]);
            }

            return offsets;
        }

        private List<Row> BuildRows()
        {
            var rows = new List<Row>();
            var filter = _filter.Trim();

            bool Matches(string text) =>
                filter.Length == 0 || text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            var currentBranch = _branches?.FirstOrDefault(branch => branch.IsCurrent) ?? default;
            var current = currentBranch.Name;
            var actions = new (string label, Action action)[]
            {
                ("Update Project...", GitMenu.UpdateProject),
                ("Commit...", GitMenu.OpenChangesWindow),
                ("Push...", GitMenu.OpenPushWindow),
                ("New Branch...", () => BranchActions.NewBranch(_git, current ?? "HEAD", currentBranch.Ref ?? "HEAD", Reload))
            };

            foreach (var (label, action) in actions.Where(item => Matches(item.label)))
            {
                rows.Add(new Row { Kind = RowKind.Action, Label = label, Activate = () => Run(action) });
            }

            if (_branches == null)
            {
                return rows;
            }

            var locals = _branches.Where(branch => !branch.IsRemote).ToList();
            var recent = (_recent ?? new List<string>())
                .Where(name => name != current)
                .Select(name => locals.FirstOrDefault(branch => branch.Name == name))
                .Where(branch => branch.Name != null)
                .Take(5);
            var currentLocal = locals.Where(branch => branch.IsCurrent);

            AddSection(rows, "Recent", currentLocal.Concat(recent).ToList(), RecentExpanded, false, Matches);
            AddSection(rows, "Local", locals, LocalExpanded, true, Matches);
            AddSection(rows, "Remote", _branches.Where(branch => branch.IsRemote).ToList(), RemoteExpanded,
                true, Matches);

            return rows;
        }

        private void AddSection(List<Row> rows, string title, List<GitBranch> branches, bool expanded,
            bool favoritesFirst, Func<string, bool> matches)
        {
            var favorites = GitProjectSettings.instance;
            var shown = branches.Where(branch => matches(branch.Name)).ToList();
            if (favoritesFirst)
            {
                shown = shown.OrderBy(branch => favorites.IsFavorite(branch) ? 0 : 1).ToList();
            }
            var filtering = _filter.Trim().Length > 0;
            if (shown.Count == 0)
            {
                return;
            }

            if (rows.Count > 0 && rows[rows.Count - 1].Kind != RowKind.Separator)
            {
                rows.Add(new Row { Kind = RowKind.Separator });
            }

            var isExpanded = expanded || filtering;
            var sectionTitle = title;
            rows.Add(new Row
            {
                Kind = RowKind.Header,
                Label = (isExpanded ? "▾  " : "▸  ") + title,
                Activate = () => Toggle(sectionTitle)
            });

            if (!isExpanded)
            {
                return;
            }

            foreach (var branch in shown)
            {
                var tracking = string.Join(" ",
                    new[] { branch.Upstream, branch.Track }.Where(part => !string.IsNullOrEmpty(part)));
                rows.Add(new Row
                {
                    Kind = RowKind.Branch,
                    Label = branch.Name,
                    Detail = tracking,
                    Branch = branch
                });
            }
        }

        private static void Toggle(string section)
        {
            switch (section)
            {
                case "Recent":
                    RecentExpanded = !RecentExpanded;
                    break;
                case "Local":
                    LocalExpanded = !LocalExpanded;
                    break;
                case "Remote":
                    RemoteExpanded = !RemoteExpanded;
                    break;
            }
        }

        private void DrawRow(int index, Row row, Rect rect)
        {
            if (row.Kind == RowKind.Separator)
            {
                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(new Rect(rect.x + 6, rect.center.y, rect.width - 12, 1),
                        new Color(0.5f, 0.5f, 0.5f, 0.35f));
                }

                return;
            }

            var current = Event.current;

            if (current.type == EventType.MouseMove && rect.Contains(current.mousePosition))
            {
                _highlight = index;
            }

            var starRow = new Rect(rect.x + 4, rect.y, rect.width, rect.height);
            if (row.Kind == RowKind.Branch && StarIcon.Toggle(starRow))
            {
                GitProjectSettings.instance.ToggleFavorite(row.Branch);
                _rowsDirty = true;
                _repaint();
                return;
            }

            if (current.type == EventType.MouseDown && rect.Contains(current.mousePosition) &&
                (current.button == 0 || current.button == 1 && row.Kind == RowKind.Branch))
            {
                _highlight = index;
                Activate(row, rect);
                current.Use();
            }

            if (current.type != EventType.Repaint)
            {
                return;
            }

            if (index == _highlight)
            {
                EditorGUI.DrawRect(rect, GitColors.Selection);
            }

            var style = row.Kind == RowKind.Header || row.Branch.IsCurrent ? _boldRowStyle : _rowStyle;
            var indent = row.Kind == RowKind.Branch ? 26 : 8;
            GUI.Label(new Rect(rect.x + indent, rect.y, rect.width - indent, rect.height), row.Label, style);

            if (row.Kind == RowKind.Branch)
            {
                StarIcon.Draw(starRow, GitProjectSettings.instance.IsFavorite(row.Branch));
                GUI.Label(new Rect(rect.x, rect.y, rect.width - 20, rect.height), row.Detail ?? "", _detailStyle);
                GUI.Label(new Rect(rect.xMax - 16, rect.y, 12, rect.height), "›", _detailStyle);
            }
        }

        private void Activate(Row row, Rect rect)
        {
            if (row.Kind == RowKind.Branch)
            {
                var menu = new GenericMenu();
                BranchActions.AddMenuItems(menu, _git, row.Branch, _branches, Reload);
                menu.DropDown(new Rect(rect.xMax - 20, rect.y, 20, rect.height));
                return;
            }

            row.Activate?.Invoke();
            _rowsDirty = true;
            _repaint();
        }

        private static void Run(Action action)
        {
            EditorApplication.delayCall += () => action();
        }

        private void HandleKeyboard()
        {
            var current = Event.current;
            if (current.type != EventType.KeyDown)
            {
                return;
            }

            switch (current.keyCode)
            {
                case KeyCode.DownArrow:
                    _highlight = NextSelectable(_highlight, 1);
                    ScrollTo(_highlight);
                    current.Use();
                    break;
                case KeyCode.UpArrow:
                    _highlight = NextSelectable(_highlight, -1);
                    ScrollTo(_highlight);
                    current.Use();
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    var index = _highlight >= 0 ? _highlight : NextSelectable(-1, 1);
                    if (index >= 0 && index < _rows.Count)
                    {
                        Activate(_rows[index], RowRect(index));
                    }

                    current.Use();
                    break;
            }
        }

        private int NextSelectable(int from, int step)
        {
            for (var i = from + step; i >= 0 && i < _rows.Count; i += step)
            {
                if (_rows[i].Kind != RowKind.Separator)
                {
                    return i;
                }
            }

            return from;
        }

        private Rect RowRect(int index)
        {
            var y = index < _offsets.Length ? _offsets[index] : 0;
            return new Rect(0, y - _scrollPos.y + HeaderHeight, _area.width, RowHeight);
        }

        private void ScrollTo(int index)
        {
            if (index < 0)
            {
                return;
            }

            var y = RowRect(index).y + _scrollPos.y - HeaderHeight;
            var visible = _area.height - HeaderHeight;
            if (y < _scrollPos.y)
            {
                _scrollPos.y = y;
            }
            else if (y + RowHeight > _scrollPos.y + visible)
            {
                _scrollPos.y = y + RowHeight - visible;
            }
        }

        private void EnsureStyles()
        {
            if (_rowStyle != null)
            {
                return;
            }

            _rowStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft };
            _boldRowStyle = new GUIStyle(_rowStyle) { fontStyle = FontStyle.Bold };
            _detailStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = Color.gray }
            };
            _placeholderStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = Color.gray } };
        }
    }
}
