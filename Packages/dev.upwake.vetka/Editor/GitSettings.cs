using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    [InitializeOnLoad]
    internal static class GitSettings
    {
        private const string Prefix = "Vetka_";

        private static volatile string _gitPath;
        private static volatile bool _verboseLogging;
        private static bool _showBranchInTitle;
        private static bool _showStatusInProject;

        static GitSettings()
        {
            _gitPath = EditorPrefs.GetString(Prefix + "GitPath", "");
            _verboseLogging = EditorPrefs.GetBool(Prefix + "VerboseLogging", false);
            _showBranchInTitle = EditorPrefs.GetBool(Prefix + "ShowBranchInTitle", true);
            _showStatusInProject = EditorPrefs.GetBool(Prefix + "ShowStatusInProject", true);

            if (!string.IsNullOrEmpty(_gitPath))
            {
                return;
            }

            var systemGitPath = Git.GetSystemGitPath();
            if (!string.IsNullOrEmpty(systemGitPath))
            {
                GitPath = systemGitPath;
            }
        }

        public static string GitPath
        {
            get => _gitPath;
            set
            {
                _gitPath = value ?? "";
                EditorPrefs.SetString(Prefix + "GitPath", _gitPath);
            }
        }

        public static bool VerboseLogging
        {
            get => _verboseLogging;
            set
            {
                _verboseLogging = value;
                EditorPrefs.SetBool(Prefix + "VerboseLogging", value);
            }
        }

        public static bool ShowBranchInTitle
        {
            get => _showBranchInTitle;
            set
            {
                _showBranchInTitle = value;
                EditorPrefs.SetBool(Prefix + "ShowBranchInTitle", value);
            }
        }

        public static bool ShowStatusInProject
        {
            get => _showStatusInProject;
            set
            {
                _showStatusInProject = value;
                EditorPrefs.SetBool(Prefix + "ShowStatusInProject", value);
            }
        }

        public static UpdateStrategy UpdateStrategyValue
        {
            get => GitProjectSettings.instance.UpdateStrategy;
            set => GitProjectSettings.instance.UpdateStrategy = value;
        }
    }

    internal enum UpdateStrategy
    {
        Merge,
        Rebase,
        Ask
    }
}
