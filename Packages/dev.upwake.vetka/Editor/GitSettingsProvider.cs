using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class GitSettingsProvider
    {
        internal const string Path = "Preferences/Vetka";

        private sealed class Styles
        {
            public static readonly GUIContent GitPath = new GUIContent("Path to Git executable");

            public static readonly GUIContent UpdateStrategy = new GUIContent("Update strategy (this project)",
                "Merge or rebase the incoming changes on Update Project, or ask every time");

            public static readonly GUIContent ShowBranchInTitle =
                new GUIContent("Show branch in the main window title");

            public static readonly GUIContent ShowStatusInProject =
                new GUIContent("Show Git status in the Project window");

            public static readonly GUIContent VerboseLogging =
                new GUIContent("Verbose logging", "Every git command in the Console");
        }

        [SettingsProvider]
        public static SettingsProvider Create() =>
            new SettingsProvider(Path, SettingsScope.User,
                SettingsProvider.GetSearchKeywordsFromGUIContentProperties<Styles>()
                    .Concat(new[] { "git", "merge", "rebase", "branch", "status", "logging" }))
            {
                label = "Vetka",
                guiHandler = _ => Draw()
            };

        private static string GitPathOf(string text) => (text ?? "").Trim().Trim('"').Trim();

        private static void Draw()
        {
            var labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 250;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(10);
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Space(10);
                    DrawGitPath();
                    DrawOptions();
                }
            }

            EditorGUIUtility.labelWidth = labelWidth;
        }

        private static void DrawGitPath()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var path = GitPathOf(EditorGUILayout.DelayedTextField(Styles.GitPath, GitSettings.GitPath));
                if (path != GitSettings.GitPath)
                {
                    SetGitPath(path);
                }

                if (GUILayout.Button("Browse", GUILayout.Width(75)))
                {
                    var selected = EditorUtility.OpenFilePanel("Select Git Executable", "", "exe");
                    if (!string.IsNullOrEmpty(selected))
                    {
                        SetGitPath(GitPathOf(selected));
                        GUI.FocusControl(null);
                    }
                }

                if (GUILayout.Button("Test", GUILayout.Width(75)))
                {
                    var git = new Git(GitSettings.GitPath);
                    GitOperations.Read(
                        "Git: checking the executable",
                        () => git.Version(),
                        version => EditorUtility.DisplayDialog(
                            version.IsSuccess ? "Success" : "Failed", version.Message, "OK"));
                }
            }
        }

        private static void SetGitPath(string path)
        {
            GitSettings.GitPath = path;
            BranchTitle.Refresh();
            ProjectStatus.Refresh();
        }

        private static void DrawOptions()
        {
            var strategy = (UpdateStrategy)EditorGUILayout.EnumPopup(Styles.UpdateStrategy,
                GitSettings.UpdateStrategyValue);
            if (strategy != GitSettings.UpdateStrategyValue)
            {
                GitSettings.UpdateStrategyValue = strategy;
            }

            var showBranch = EditorGUILayout.Toggle(Styles.ShowBranchInTitle, GitSettings.ShowBranchInTitle);
            if (showBranch != GitSettings.ShowBranchInTitle)
            {
                GitSettings.ShowBranchInTitle = showBranch;
                BranchTitle.Refresh();
            }

            var showStatus = EditorGUILayout.Toggle(Styles.ShowStatusInProject, GitSettings.ShowStatusInProject);
            if (showStatus != GitSettings.ShowStatusInProject)
            {
                GitSettings.ShowStatusInProject = showStatus;
                ProjectStatus.Refresh();
            }

            var verbose = EditorGUILayout.Toggle(Styles.VerboseLogging, GitSettings.VerboseLogging);
            if (verbose != GitSettings.VerboseLogging)
            {
                GitSettings.VerboseLogging = verbose;
            }
        }
    }
}
