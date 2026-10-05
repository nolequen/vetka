using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    [InitializeOnLoad]
    internal static class ProjectStatus
    {
        internal sealed class Snapshot
        {
            public readonly Dictionary<string, GitStatus> Files =
                new Dictionary<string, GitStatus>(StringComparer.OrdinalIgnoreCase);

            public readonly HashSet<string> Folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public readonly Dictionary<string, GitFileChange> Changes =
                new Dictionary<string, GitFileChange>(StringComparer.OrdinalIgnoreCase);

            public readonly List<GitFileChange> Entries = new List<GitFileChange>();

            public readonly Dictionary<string, string> PackageRoots = new Dictionary<string, string>();

            public string Prefix;

            public string Top;

            public string Error;
        }

        private const float BadgeSize = 14;
        private const float DotSize = 6;

        private static Snapshot _current = new Snapshot();

        internal static Snapshot Current => _current;
        internal static bool Loaded { get; private set; }
        internal static bool Loading => _loading;
        internal static string Aborted { get; private set; }
        internal static event Action Updated;
        private static bool _loading;
        private static volatile bool _started;
        private static bool _refreshAgain;
        private static GUIStyle _letterStyle;

        private static GUIStyle LetterStyle => _letterStyle ??= new GUIStyle(EditorStyles.miniBoldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 9,
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0)
        };

        static ProjectStatus()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            EditorApplication.projectWindowItemOnGUI += OnItemGUI;
            EditorApplication.projectChanged += Refresh;
            EditorApplication.focusChanged += OnEditorFocusChanged;
            GitOperations.RepositoryChanged += Refresh;
            EditorApplication.delayCall += Refresh;
        }

        public static void Refresh()
        {
            if (_loading)
            {
                _refreshAgain |= _started;
                return;
            }

            _loading = true;
            _started = false;
            var git = new Git();
            GitOperations.Read("Git: reading changes", () =>
            {
                _started = true;
                return Read(git);
            }, snapshot =>
            {
                _current = snapshot.Error == null ? snapshot : new Snapshot();
                Loaded = snapshot.Error == null;
                Aborted = snapshot.Error;
                _loading = false;
                if (_refreshAgain)
                {
                    _refreshAgain = false;
                    Refresh();
                }

                EditorApplication.RepaintProjectWindow();
                Updated?.Invoke();
            }, reason =>
            {
                _loading = false;
                _refreshAgain = false;
                Aborted = reason;
                Updated?.Invoke();
            });
        }

        private static void OnEditorFocusChanged(bool focused)
        {
            if (focused)
            {
                Refresh();
            }
        }

        private static Snapshot Read(Git git)
        {
            var location = git.ProjectLocation();
            if (!location.Result.IsSuccess)
            {
                return new Snapshot { Error = location.Result.Message };
            }

            var changes = git.WorkingTreeChanges();
            return changes.Result.IsSuccess
                ? Map(changes.Changes, location.Prefix, location.Top)
                : new Snapshot { Error = changes.Result.Message };
        }

        internal static Snapshot Map(IEnumerable<GitFileChange> changes, string prefix, string top = null)
        {
            var snapshot = new Snapshot { Prefix = prefix, Top = top };
            var metaOnly = new Dictionary<string, GitStatus>(StringComparer.OrdinalIgnoreCase);
            var all = changes.ToList();
            var tracked = new HashSet<string>(all.Where(change => change.Status != GitStatus.Untracked)
                .Select(change => change.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var change in all)
            {
                snapshot.Entries.Add(change);
                if (change.Status == GitStatus.Untracked && tracked.Contains(change.Path))
                {
                    continue;
                }

                snapshot.Changes[change.Path] = change;
                if (change.OldPath != null)
                {
                    AddFolders(snapshot.Folders, change.OldPath);
                }

                var path = change.Path;
                AddFolders(snapshot.Folders, path);
                if (change.Status == GitStatus.Deleted)
                {
                    continue;
                }

                if (path.EndsWith(".meta"))
                {
                    metaOnly[path.Substring(0, path.Length - ".meta".Length)] = change.Status;
                }
                else
                {
                    snapshot.Files[path] = change.Status;
                }
            }

            foreach (var meta in metaOnly)
            {
                if (!snapshot.Files.ContainsKey(meta.Key))
                {
                    snapshot.Files[meta.Key] = meta.Value == GitStatus.Untracked || meta.Value == GitStatus.Added
                        ? meta.Value
                        : GitStatus.Modified;
                }
            }

            return snapshot;
        }

        internal static string RepositoryPath(Snapshot snapshot, string assetPath)
        {
            if (snapshot.Prefix == null)
            {
                return null;
            }

            const string packages = "Packages/";
            if (!assetPath.StartsWith(packages))
            {
                return snapshot.Prefix + assetPath;
            }

            var slash = assetPath.IndexOf('/', packages.Length);
            var root = slash < 0 ? assetPath : assetPath.Substring(0, slash);
            if (!snapshot.PackageRoots.TryGetValue(root, out var repositoryRoot))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(root);
                repositoryRoot = package == null || string.IsNullOrEmpty(package.resolvedPath) ||
                                 string.IsNullOrEmpty(snapshot.Top)
                    ? snapshot.Prefix + root
                    : PackageRoot(snapshot.Top, package.resolvedPath);
                snapshot.PackageRoots[root] = repositoryRoot;
            }

            return repositoryRoot == null ? null : repositoryRoot + assetPath.Substring(root.Length);
        }

        internal static string PackageRoot(string top, string resolvedPath)
        {
            var relative = System.IO.Path.GetRelativePath(top, resolvedPath).Replace('\\', '/');
            return relative == "." || relative.StartsWith("..") || System.IO.Path.IsPathRooted(relative)
                ? null
                : relative;
        }

        private static void AddFolders(HashSet<string> folders, string path)
        {
            var separator = path.LastIndexOf('/');
            while (separator > 0)
            {
                path = path.Substring(0, separator);
                if (!folders.Add(path))
                {
                    return;
                }

                separator = path.LastIndexOf('/');
            }
        }

        internal static string Letter(GitStatus status)
        {
            return status switch
            {
                GitStatus.Added => "A",
                GitStatus.Copied => "A",
                GitStatus.Untracked => "U",
                GitStatus.Renamed => "R",
                GitStatus.Unmerged => "!",
                _ => "M"
            };
        }

        private static void OnItemGUI(string guid, Rect rect)
        {
            var snapshot = _current;
            if (Event.current.type != EventType.Repaint || !GitSettings.ShowStatusInProject ||
                snapshot.Files.Count == 0 && snapshot.Folders.Count == 0)
            {
                return;
            }

            var asset = AssetDatabase.GUIDToAssetPath(guid);
            var path = string.IsNullOrEmpty(asset) ? null : RepositoryPath(snapshot, asset);
            if (path == null)
            {
                return;
            }

            if (snapshot.Files.TryGetValue(path, out var status))
            {
                DrawBadge(rect, Letter(status), GitColors.Status(status));
            }
            else if (snapshot.Folders.Contains(path))
            {
                DrawDot(rect, GitColors.Status(GitStatus.Modified));
            }
        }

        private static Rect BadgeRect(Rect rect, float size) =>
            rect.height > 20
                ? new Rect(rect.xMax - size - 1, rect.y + 1, size, size)
                : new Rect(rect.xMax - size - 4, rect.y + (rect.height - size) / 2, size, size);

        private static void DrawBadge(Rect rect, string letter, Color color)
        {
            var badge = BadgeRect(rect, BadgeSize);
            DrawCircle(badge, new Color(color.r, color.g, color.b, 0.22f));
            GUI.Label(badge, letter, GitColors.TextStyle(LetterStyle, color));
        }

        private static void DrawDot(Rect rect, Color color)
        {
            var area = BadgeRect(rect, BadgeSize);
            DrawCircle(new Rect(area.center.x - DotSize / 2, area.center.y - DotSize / 2, DotSize, DotSize), color);
        }

        private static void DrawCircle(Rect rect, Color color) =>
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, color, 0, rect.width / 2);
    }
}
