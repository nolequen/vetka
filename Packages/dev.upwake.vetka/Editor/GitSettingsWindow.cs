using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal class GitSettingsWindow : EditorWindow
    {
        [SerializeField] private bool _loaded;
        [SerializeField] private string _gitPath;
        [SerializeField] private UpdateStrategy _updateStrategy;
        [SerializeField] private bool _showBranchInTitle;
        [SerializeField] private bool _showStatusInProject;
        [SerializeField] private bool _verboseLogging;

        public static void ShowWindow()
        {
            var window = GetWindow<GitSettingsWindow>(utility: true, "Git settings");
            window.minSize = new Vector2(500, 150);
            window.maxSize = window.minSize;
        }

        private void OnEnable()
        {
            if (_loaded)
            {
                return;
            }

            _gitPath = GitSettings.GitPath;
            _updateStrategy = GitSettings.UpdateStrategyValue;
            _showBranchInTitle = GitSettings.ShowBranchInTitle;
            _showStatusInProject = GitSettings.ShowStatusInProject;
            _verboseLogging = GitSettings.VerboseLogging;
            _loaded = true;
        }

        private static string GitPathOf(string text) => (text ?? "").Trim().Trim('"').Trim();

        private void OnGUI()
        {
            EditorGUILayout.BeginVertical();
            EditorGUILayout.BeginHorizontal();

            _gitPath = EditorGUILayout.TextField("Path to Git executable:", _gitPath);

            if (GUILayout.Button("Browse", GUILayout.Width(75)))
            {
                var selectedPath = EditorUtility.OpenFilePanel("Select Git Executable", "", "exe");
                if (!string.IsNullOrEmpty(selectedPath))
                {
                    _gitPath = selectedPath;
                    GUI.FocusControl(null);
                }
            }

            if (GUILayout.Button("Test", GUILayout.Width(75)))
            {
                var git = new Git(GitPathOf(_gitPath));
                GitOperations.Read(
                    "Git: checking the executable",
                    () => git.Version(),
                    version => EditorUtility.DisplayDialog(
                        version.IsSuccess ? "Success" : "Failed", version.Message, "OK"
                    )
                );
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            _updateStrategy = (UpdateStrategy)EditorGUILayout.EnumPopup("Update strategy (this project):",
                _updateStrategy);

            EditorGUILayout.EndHorizontal();

            _showBranchInTitle = EditorGUILayout.ToggleLeft("Show branch in the main window title",
                _showBranchInTitle);
            _showStatusInProject = EditorGUILayout.ToggleLeft("Show Git status in the Project window",
                _showStatusInProject);
            _verboseLogging = EditorGUILayout.ToggleLeft("Verbose logging (every git command in the Console)",
                _verboseLogging);

            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            var changed = GitPathOf(_gitPath) != GitSettings.GitPath ||
                          _updateStrategy != GitSettings.UpdateStrategyValue ||
                          _showBranchInTitle != GitSettings.ShowBranchInTitle ||
                          _showStatusInProject != GitSettings.ShowStatusInProject ||
                          _verboseLogging != GitSettings.VerboseLogging;
            using (new EditorGUI.DisabledScope(!changed))
            {
                if (GUILayout.Button("Save", GUILayout.Width(80)))
                {
                    GitSettings.GitPath = GitPathOf(_gitPath);
                    GitSettings.UpdateStrategyValue = _updateStrategy;
                    GitSettings.ShowBranchInTitle = _showBranchInTitle;
                    GitSettings.ShowStatusInProject = _showStatusInProject;
                    GitSettings.VerboseLogging = _verboseLogging;
                    BranchTitle.Refresh();
                    ProjectStatus.Refresh();
                    EditorApplication.delayCall += Close;
                }
            }

            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            {
                EditorApplication.delayCall += Close;
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }
    }
}
