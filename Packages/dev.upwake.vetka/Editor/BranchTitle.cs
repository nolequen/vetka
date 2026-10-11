using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    [InitializeOnLoad]
    internal static class BranchTitle
    {
        private const double CheckIntervalSeconds = 1;

        private static string _headPath;
        private static string _currentBranch;
        private static double _nextCheck;

        static BranchTitle()
        {
            EditorApplication.updateMainWindowTitle += UpdateTitle;
            EditorApplication.update += Poll;
            GitOperations.RepositoryChanged += Refresh;
            Refresh();
        }

        internal static bool InRepository => FindHead(Git.ProjectRoot) != null;

        public static void Refresh()
        {
            _currentBranch = GitSettings.ShowBranchInTitle ? ReadBranch() : null;
            EditorApplication.UpdateMainWindowTitle();
        }

        private static void Poll()
        {
            if (!GitSettings.ShowBranchInTitle || EditorApplication.timeSinceStartup < _nextCheck)
            {
                return;
            }

            _nextCheck = EditorApplication.timeSinceStartup + CheckIntervalSeconds;

            var branch = ReadBranch();
            if (branch != _currentBranch)
            {
                _currentBranch = branch;
                EditorApplication.UpdateMainWindowTitle();
            }
        }

        private static void UpdateTitle(ApplicationTitleDescriptor descriptor)
        {
            if (string.IsNullOrEmpty(_currentBranch))
            {
                return;
            }

            var project = descriptor.projectName;
            descriptor.title = !string.IsNullOrEmpty(project) && descriptor.title.StartsWith(project)
                ? $"{project} [{_currentBranch}]{descriptor.title.Substring(project.Length)}"
                : $"[{_currentBranch}] {descriptor.title}";
        }

        private static string ReadBranch()
        {
            try
            {
                _headPath ??= FindHead(Git.ProjectRoot);
                return _headPath != null && File.Exists(_headPath) ? BranchName(File.ReadAllText(_headPath)) : null;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Vetka: cannot read the current branch: {exception.Message}");
                _headPath = null;
                return null;
            }
        }

        internal static string BranchName(string headContent)
        {
            var head = headContent.Trim();
            if (head.StartsWith("ref: refs/heads/"))
            {
                var branch = head.Substring("ref: refs/heads/".Length);
                return branch == ".invalid" ? null : branch;
            }

            if (head.StartsWith("ref: "))
            {
                return head.Substring("ref: ".Length);
            }

            return head.Length >= 7 ? $"detached at {head.Substring(0, 7)}" : null;
        }

        internal static string FindHead(string directory)
        {
            while (!string.IsNullOrEmpty(directory))
            {
                var dotGit = Path.Combine(directory, ".git");
                if (Directory.Exists(dotGit))
                {
                    return Path.Combine(dotGit, "HEAD");
                }

                if (File.Exists(dotGit))
                {
                    var line = File.ReadAllText(dotGit).Trim();
                    if (line.StartsWith("gitdir:"))
                    {
                        var gitDir = line.Substring("gitdir:".Length).Trim();
                        return Path.Combine(Path.GetFullPath(Path.Combine(directory, gitDir)), "HEAD");
                    }
                }

                directory = Path.GetDirectoryName(directory);
            }

            return null;
        }
    }
}
