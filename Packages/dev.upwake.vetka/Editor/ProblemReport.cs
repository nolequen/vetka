using System;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class ProblemReport
    {
        private const string NewIssue = "https://github.com/nolequen/vetka/issues/new?template=bug-report.yml";

        public static void Open()
        {
            var unity = UnityVersion(EditorApplication.applicationPath, Application.unityVersion);
            var os = SystemInfo.operatingSystem;
            var git = new Git();
            GitOperations.Read(
                "Git: checking the version",
                () => git.Version(),
                version => Application.OpenURL(Url(unity, VetkaInfo.Version, GitValue(version), os)),
                _ => Application.OpenURL(Url(unity, VetkaInfo.Version, GitValue(null), os)));
        }

        internal static string GitValue(GitResult? version)
        {
            if (version == null)
            {
                return "not found";
            }

            return version.Value.IsSuccess
                ? version.Value.Output.Trim()
                : version.Value.Message.Split('\n')[0].Trim();
        }

        internal static string UnityVersion(string applicationPath, string unityVersion)
        {
            var editor = applicationPath.Substring(applicationPath.LastIndexOfAny(new[] { '/', '\\' }) + 1);
            return editor.StartsWith("Tuanjie", StringComparison.OrdinalIgnoreCase)
                ? $"Tuanjie {unityVersion}"
                : unityVersion;
        }

        internal static string Url(string unity, string vetka, string git, string os)
        {
            return $"{NewIssue}&unity={Uri.EscapeDataString(unity)}&vetka={Uri.EscapeDataString(vetka)}" +
                   $"&git={Uri.EscapeDataString(git)}&os={Uri.EscapeDataString(os)}";
        }
    }
}
