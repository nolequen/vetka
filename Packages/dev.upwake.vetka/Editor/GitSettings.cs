using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    [InitializeOnLoad]
    internal static class GitSettings
    {
        private const string GitPathKey = "Vetka_GitPath";
        private const string VerboseLoggingKey = "Vetka_VerboseLogging";
        private const string ShowBranchInTitleKey = "Vetka_ShowBranchInTitle";
        private const string ShowStatusInProjectKey = "Vetka_ShowStatusInProject";
        private const string PushTagsKey = "Vetka_PushTags";
        private const string PushTagsModeKey = "Vetka_PushTagsMode";

        private static volatile string _gitPath;
        private static volatile bool _verboseLogging;
        private static bool _showBranchInTitle;
        private static bool _showStatusInProject;
        private static bool _pushTags;
        private static PushTags _pushTagsMode;

        static GitSettings()
        {
            _gitPath = EditorPrefs.GetString(GitPathKey, "");
            _verboseLogging = EditorPrefs.GetBool(VerboseLoggingKey, false);
            _showBranchInTitle = EditorPrefs.GetBool(ShowBranchInTitleKey, true);
            _showStatusInProject = EditorPrefs.GetBool(ShowStatusInProjectKey, true);
            _pushTags = EditorPrefs.GetBool(PushTagsKey, false);
            _pushTagsMode = EditorPrefs.GetInt(PushTagsModeKey, 0) == (int)PushTags.All
                ? PushTags.All
                : PushTags.CurrentBranch;

            if (!string.IsNullOrEmpty(_gitPath) && System.IO.File.Exists(_gitPath))
            {
                return;
            }

            var systemGitPath = Git.GetSystemGitPath();
            if (string.IsNullOrEmpty(systemGitPath))
            {
                return;
            }

            if (string.IsNullOrEmpty(_gitPath))
            {
                GitPath = systemGitPath;
            }
            else
            {
                _gitPath = systemGitPath;
            }
        }

        public static string GitPath
        {
            get => _gitPath;
            set => EditorPrefs.SetString(GitPathKey, _gitPath = value ?? "");
        }

        public static bool VerboseLogging
        {
            get => _verboseLogging;
            set => EditorPrefs.SetBool(VerboseLoggingKey, _verboseLogging = value);
        }

        public static bool ShowBranchInTitle
        {
            get => _showBranchInTitle;
            set => EditorPrefs.SetBool(ShowBranchInTitleKey, _showBranchInTitle = value);
        }

        public static bool ShowStatusInProject
        {
            get => _showStatusInProject;
            set => EditorPrefs.SetBool(ShowStatusInProjectKey, _showStatusInProject = value);
        }

        public static bool PushTagsEnabled
        {
            get => _pushTags;
            set => EditorPrefs.SetBool(PushTagsKey, _pushTags = value);
        }

        public static PushTags PushTagsMode
        {
            get => _pushTagsMode;
            set => EditorPrefs.SetInt(PushTagsModeKey, (int)(_pushTagsMode = value));
        }
    }

    internal enum UpdateStrategy
    {
        Merge,
        Rebase
    }

    internal enum UpdateStrategyChoice
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
