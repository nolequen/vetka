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
        private static bool _pushTags;
        private static PushTags _pushTagsMode;

        static GitSettings()
        {
            _gitPath = EditorPrefs.GetString(Prefix + "GitPath", "");
            _verboseLogging = EditorPrefs.GetBool(Prefix + "VerboseLogging", false);
            _showBranchInTitle = EditorPrefs.GetBool(Prefix + "ShowBranchInTitle", true);
            _showStatusInProject = EditorPrefs.GetBool(Prefix + "ShowStatusInProject", true);
            _pushTags = EditorPrefs.GetBool(Prefix + "PushTags", false);
            _pushTagsMode = EditorPrefs.GetInt(Prefix + "PushTagsMode", 0) == (int)PushTags.All
                ? PushTags.All
                : PushTags.CurrentBranch;

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

        public static bool PushTagsEnabled
        {
            get => _pushTags;
            set
            {
                _pushTags = value;
                EditorPrefs.SetBool(Prefix + "PushTags", value);
            }
        }

        public static PushTags PushTagsMode
        {
            get => _pushTagsMode;
            set
            {
                _pushTagsMode = value;
                EditorPrefs.SetInt(Prefix + "PushTagsMode", (int)value);
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

    internal enum PushTags
    {
        CurrentBranch,
        All
    }
}
