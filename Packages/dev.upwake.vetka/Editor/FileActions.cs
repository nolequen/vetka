using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class FileActions
    {
        internal sealed class Target
        {
            public Target(List<GitFileChange> selected, List<GitFileChange> changes, string file,
                GitFileChange? fileChange, bool busy, bool fromProject = false)
            {
                Selected = selected;
                Changes = changes;
                File = file;
                FileChange = fileChange;
                Busy = busy;
                FromProject = fromProject;
            }

            public List<GitFileChange> Selected { get; }
            public List<GitFileChange> Changes { get; }
            public string File { get; }
            public GitFileChange? FileChange { get; }
            public bool Busy { get; }
            public bool FromProject { get; }

            public List<GitFileChange> Untracked =>
                Changes.Where(change => change.Status == GitStatus.Untracked).ToList();

            public List<GitFileChange> Tracked =>
                Changes.Where(change => change.Status != GitStatus.Untracked).ToList();

            public List<GitFileChange> SelectedUntracked =>
                Selected.Where(change => change.Status == GitStatus.Untracked).ToList();

            public List<GitFileChange> SelectedTracked =>
                Selected.Where(change => change.Status != GitStatus.Untracked).ToList();
        }

        private sealed class Item
        {
            public string Name;
            public Func<Target, IEnumerable<GitFileChange>> Counted;
            public Func<Target, bool> IsEnabled;
            public Func<Target, bool> IsVisible;
            public Action<Target> Run;
        }

        private const string ShowDiffName = "Show Diff";
        private const string ShowHistoryName = "Show History";
        private const string BlameName = "Blame";
        private const string AddName = "Add to Git";
        private const string RollbackName = "Rollback...";
        private const string CreatePatchName = "Create Patch...";
        private const string ProjectMenu = "Assets/Git/";
        private const int ProjectMenuPriority = 2000;

        private static readonly Item[] Items =
        {
            new Item
            {
                Name = ShowDiffName,
                IsEnabled = target => target.FileChange.HasValue,
                Run = target => ShowDiff(target.FileChange.Value)
            },
            new Item
            {
                Name = ShowHistoryName,
                IsEnabled = target => target.File != null && (!target.FileChange.HasValue ||
                                                              target.FileChange.Value.Status != GitStatus.Untracked &&
                                                              target.FileChange.Value.Status != GitStatus.Added),
                Run = target => GitLogWindow.ShowHistory(new Git(), HistoryPath(target), target.FromProject)
            },
            new Item
            {
                Name = BlameName,
                IsEnabled = target => target.File != null && (!target.FileChange.HasValue ||
                                                              target.FileChange.Value.Status == GitStatus.Modified ||
                                                              target.FileChange.Value.Status == GitStatus.TypeChanged ||
                                                              target.FileChange.Value.Status == GitStatus.Unmerged),
                Run = target => BlameWindow.ShowWindow(new Git(), target.File)
            },
            new Item
            {
                Name = AddName,
                Counted = target => target.SelectedUntracked,
                IsVisible = target => target.Untracked.Count > 0,
                IsEnabled = target => !target.Busy && target.Untracked.Count > 0,
                Run = target => Add(target.Untracked.Select(change => change.Path).ToList())
            },
            new Item
            {
                Name = RollbackName,
                Counted = target => target.SelectedTracked,
                IsEnabled = target => !target.Busy && target.Tracked.Count > 0,
                Run = target => Rollback(target.Tracked, target.SelectedTracked)
            },
            new Item
            {
                Name = CreatePatchName,
                Counted = target => target.Selected,
                IsEnabled = target => target.Changes.Count > 0,
                Run = target => CreatePatch(WithOldPaths(target.Changes))
            }
        };

        public static void AddTo(GenericMenu menu, Target target)
        {
            foreach (var item in Items)
            {
                if (item.IsVisible != null && !item.IsVisible(target))
                {
                    continue;
                }

                var label = new GUIContent(Label(item, target));
                if (item.IsEnabled(target))
                {
                    menu.AddItem(label, false, () => EditorApplication.delayCall += () => item.Run(target));
                }
                else
                {
                    menu.AddDisabledItem(label);
                }
            }
        }

        private static string Label(Item item, Target target)
        {
            var count = item.Counted == null
                ? 0
                : item.Counted(target).Select(change => change.Path).Distinct().Count();
            if (count < 2)
            {
                return item.Name;
            }

            var dialog = item.Name.EndsWith("...");
            var name = dialog ? item.Name.Substring(0, item.Name.Length - 3) : item.Name;
            return $"{name} ({count} files){(dialog ? "..." : "")}";
        }

        public static Target ForSelection(IEnumerable<GitFileChange> selected, bool busy)
        {
            var chosen = selected.ToList();
            return chosen.Count == 1
                ? new Target(chosen, chosen, chosen[0].Path, chosen[0], busy)
                : new Target(chosen, chosen, null, null, busy);
        }

        private static Target ForProjectSelection()
        {
            var snapshot = ProjectStatus.Current;
            var assets = Selection.assetGUIDs
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct()
                .ToList();
            if (snapshot.Prefix == null || assets.Count == 0)
            {
                return new Target(new List<GitFileChange>(), new List<GitFileChange>(), null, null, false);
            }

            var changes = new Dictionary<string, GitFileChange>();
            foreach (var asset in assets)
            {
                var path = ProjectStatus.RepositoryPath(snapshot, asset);
                if (path == null)
                {
                    continue;
                }

                foreach (var candidate in new[] { path, path + ".meta" })
                {
                    if (snapshot.Changes.TryGetValue(candidate, out var change))
                    {
                        changes[change.Path] = change;
                    }
                }

                if (!AssetDatabase.IsValidFolder(asset))
                {
                    continue;
                }

                foreach (var change in snapshot.Changes.Values)
                {
                    if (change.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) ||
                        change.OldPath != null && change.OldPath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        changes[change.Path] = change;
                    }
                }
            }

            var list = changes.Values.ToList();
            var selected = list.Where(change => !change.Path.EndsWith(".meta") || !changes.ContainsKey(MetaPair(change.Path)))
                .ToList();
            var file = assets.Count == 1 ? ProjectStatus.RepositoryPath(snapshot, assets[0]) : null;
            if (file == null || AssetDatabase.IsValidFolder(assets[0]))
            {
                return new Target(selected, list, null, null, false);
            }

            GitFileChange? fileChange = null;
            if (snapshot.Changes.TryGetValue(file, out var own))
            {
                fileChange = own;
            }
            else if (snapshot.Changes.TryGetValue(file + ".meta", out var meta))
            {
                fileChange = meta;
            }

            return new Target(selected, list, file, fileChange, false, fromProject: true);
        }

        internal static string HistoryPath(Target target)
        {
            if (!target.FileChange.HasValue || target.FileChange.Value.OldPath == null)
            {
                return target.File;
            }

            var change = target.FileChange.Value;
            if (change.Path == target.File)
            {
                return change.OldPath;
            }

            return change.Path == target.File + ".meta" && change.OldPath.EndsWith(".meta")
                ? change.OldPath.Substring(0, change.OldPath.Length - ".meta".Length)
                : target.File;
        }

        private static string MetaPair(string path) =>
            path.EndsWith(".meta") ? path.Substring(0, path.Length - ".meta".Length) : path + ".meta";

        internal static List<string> WithOldPaths(IEnumerable<GitFileChange> changes) =>
            changes.SelectMany(change => change.OldPath == null ? new[] { change.Path } : new[] { change.Path, change.OldPath })
                .Distinct()
                .ToList();

        public static void ShowDiff(GitFileChange change)
        {
            DiffWindow.ShowWindow(new Git(), change.Path, change.Status == GitStatus.Untracked, change.OldPath);
        }

        public static void Add(List<string> paths)
        {
            var git = new Git();
            GitOperations.Run("Git: adding files", () => git.Add(paths), result =>
            {
                if (result.IsSuccess)
                {
                    ChangesSelection.instance.Set(paths, true);
                }
                else
                {
                    Notification.Show(result);
                }
            }, changesRepository: true);
        }

        private static void Rollback(List<GitFileChange> changes, List<GitFileChange> shown)
        {
            if (shown.Count == 0)
            {
                shown = changes;
            }

            var added = shown.Count(change => change.Status == GitStatus.Added);
            var count = shown.Select(change => change.Path).Distinct().Count();
            var files = count == 1 ? System.IO.Path.GetFileName(shown[0].Path) : $"{count} files";
            var git = new Git();
            RollbackDialog.Show($"Rollback {files}?", added, deleteAdded =>
                GitOperations.Run("Git: rolling back changes", () => git.Rollback(changes, deleteAdded), result =>
                {
                    if (result.IsSuccess)
                    {
                        ChangesSelection.instance.Forget(WithOldPaths(changes));
                    }

                    Notification.Show(result.IsSuccess
                        ? GitResult.Success($"{(count == 1 ? "1 file" : $"{count} files")} rolled back")
                        : result);
                }, refreshAssets: true, prepare: UnsavedChanges.SaveOrCancel));
        }

        private static void CreatePatch(List<string> files)
        {
            var path = EditorUtility.SaveFilePanel("Create patch", "", "changes.patch", "patch");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var git = new Git();
            GitOperations.Read("Git: creating a patch", () => git.CreatePatch(files, path), Notification.Show);
        }

        private static void RunFromProject(string name)
        {
            var target = ForProjectSelection();
            var item = Items.First(candidate => candidate.Name == name);
            if (item.IsEnabled(target))
            {
                EditorApplication.delayCall += () => item.Run(target);
            }
        }

        private static bool CanRunFromProject(string name) =>
            Items.First(candidate => candidate.Name == name).IsEnabled(ForProjectSelection());

        [MenuItem(ProjectMenu + ShowDiffName, false, ProjectMenuPriority)]
        private static void ProjectShowDiff() => RunFromProject(ShowDiffName);

        [MenuItem(ProjectMenu + ShowDiffName, true)]
        private static bool CanProjectShowDiff() => CanRunFromProject(ShowDiffName);

        [MenuItem(ProjectMenu + ShowHistoryName, false, ProjectMenuPriority + 1)]
        private static void ProjectShowHistory() => RunFromProject(ShowHistoryName);

        [MenuItem(ProjectMenu + ShowHistoryName, true)]
        private static bool CanProjectShowHistory() => CanRunFromProject(ShowHistoryName);

        [MenuItem(ProjectMenu + BlameName, false, ProjectMenuPriority + 2)]
        private static void ProjectBlame() => RunFromProject(BlameName);

        [MenuItem(ProjectMenu + BlameName, true)]
        private static bool CanProjectBlame() => CanRunFromProject(BlameName);

        [MenuItem(ProjectMenu + AddName, false, ProjectMenuPriority + 3)]
        private static void ProjectAdd() => RunFromProject(AddName);

        [MenuItem(ProjectMenu + AddName, true)]
        private static bool CanProjectAdd() => CanRunFromProject(AddName);

        [MenuItem(ProjectMenu + RollbackName, false, ProjectMenuPriority + 4)]
        private static void ProjectRollback() => RunFromProject(RollbackName);

        [MenuItem(ProjectMenu + RollbackName, true)]
        private static bool CanProjectRollback() => CanRunFromProject(RollbackName);

        [MenuItem(ProjectMenu + CreatePatchName, false, ProjectMenuPriority + 5)]
        private static void ProjectCreatePatch() => RunFromProject(CreatePatchName);

        [MenuItem(ProjectMenu + CreatePatchName, true)]
        private static bool CanProjectCreatePatch() => CanRunFromProject(CreatePatchName);
    }
}
