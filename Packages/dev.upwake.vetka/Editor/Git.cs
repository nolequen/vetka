using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Upwake.Vetka
{
    internal partial class Git
    {
        private const string LogFieldSeparator = "\u001f";

        private readonly string _gitPath;

        private readonly string _projectRoot;

        private readonly IReadOnlyDictionary<string, string> _environment;

        private string _top;

        public Git() : this(null)
        {
        }

        public Git(string gitPath)
            : this(gitPath, Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory())
        {
        }

        internal Git(string gitPath, string workingDirectory, IReadOnlyDictionary<string, string> environment = null)
        {
            _gitPath = gitPath;
            _projectRoot = workingDirectory;
            _environment = environment;
        }

        private string GitPath => _gitPath ?? GitSettings.GitPath;

        private static bool Verbose => GitSettings.VerboseLogging;

        public static string GetSystemGitPath()
        {
            var gitFromPath = FindGitInPath();
            if (!string.IsNullOrEmpty(gitFromPath))
            {
                return gitFromPath;
            }

            var gitFromStandardPaths = FindGitInStandardPaths();
            return !string.IsNullOrEmpty(gitFromStandardPaths) ? gitFromStandardPaths : null;
        }

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static string FindGitInPath()
        {
            var name = IsWindows ? "git.exe" : "git";
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var entry in path.Split(Path.PathSeparator))
            {
                var directory = entry.Trim().Trim('"');
                if (directory.Length == 0)
                {
                    continue;
                }

                try
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            return null;
        }

        private static string FindGitInStandardPaths()
        {
            var localPrograms = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git");
            var scoop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims");
            var standardPaths = IsWindows
                ? new[]
                {
                    Path.Combine(localPrograms, "cmd", "git.exe"),
                    Path.Combine(scoop, "git.exe"),
                    @"C:\Program Files\Git\bin\git.exe",
                    @"C:\Program Files\Git\cmd\git.exe",
                    @"C:\Program Files (x86)\Git\bin\git.exe",
                    @"C:\Program Files (x86)\Git\cmd\git.exe",
                    @"C:\Git\bin\git.exe",
                    @"C:\Git\cmd\git.exe"
                }
                : new[]
                {
                    "/usr/bin/git",
                    "/usr/local/bin/git",
                    "/opt/homebrew/bin/git",
                    "/Applications/Xcode.app/Contents/Developer/usr/bin/git"
                };

            return standardPaths.FirstOrDefault(File.Exists);
        }

        public GitResult Version() => Run("--version");

        private GitResult TopLevel()
        {
            if (_top != null)
            {
                return GitResult.Success(_top);
            }

            var top = Run("rev-parse", "--show-toplevel");
            if (top.IsSuccess)
            {
                _top = top.Output;
            }

            return top;
        }

        private GitResult RunWithPathspecs(Dictionary<string, string> environment, IEnumerable<string> paths,
            params string[] arguments)
        {
            var list = paths.Distinct().ToList();
            if (list.Count == 0)
            {
                return GitResult.Success(string.Empty);
            }

            var pathspecFile = WriteTempFile(LiteralPathspecs(list));
            try
            {
                return RunWithEnvironment(environment, arguments.Append("--pathspec-from-file=" + pathspecFile).ToArray());
            }
            finally
            {
                DeleteTempFile(pathspecFile);
            }
        }

        private T WithTemporaryIndex<T>(Func<Dictionary<string, string>, T> run)
        {
            var indexPath = Run("rev-parse", "--git-path", "index");
            if (!indexPath.IsSuccess)
            {
                return default;
            }

            var source = Path.GetFullPath(Path.Combine(_projectRoot, indexPath.Output));
            if (!File.Exists(source))
            {
                return default;
            }

            var indexFile = Path.Combine(Path.GetTempPath(), $"Vetka-{Guid.NewGuid():N}.index");
            try
            {
                File.Copy(source, indexFile);
                return run(new Dictionary<string, string> { ["GIT_INDEX_FILE"] = indexFile });
            }
            catch (Exception exception)
            {
                if (Verbose)
                {
                    Debug.LogWarning($"Vetka: cannot use a temporary index: {exception.Message}");
                }

                return default;
            }
            finally
            {
                DeleteTempFile(indexFile);
            }
        }

        private static string Subject(string message)
        {
            var subject = message.Trim().Split('\r', '\n')[0].Trim();
            return subject.Length > 60 ? subject.Substring(0, 57) + "..." : subject;
        }

        private static string Counted(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

        private static string LiteralPathspecs(IEnumerable<string> paths) =>
            string.Join("\n", paths.Select(path => ":(top,literal)" + path));

        private static List<string> NulSeparated(string output) =>
            output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        private static string WriteTempFile(string content)
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, content, Utf8);
            return path;
        }

        private static void DeleteTempFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Vetka: cannot delete a temporary file {path}: {exception.Message}");
            }
        }

        internal static GitStatus ParseStatus(char index, char workTree)
        {
            if (index == '?' && workTree == '?')
            {
                return GitStatus.Untracked;
            }

            if (index == '!' && workTree == '!')
            {
                return GitStatus.Ignored;
            }

            if (index == 'U' || workTree == 'U' || (index == 'A' && workTree == 'A') ||
                (index == 'D' && workTree == 'D'))
            {
                return GitStatus.Unmerged;
            }

            if (workTree == 'D')
            {
                return GitStatus.Deleted;
            }

            return ParseStatus(index != ' ' ? index.ToString() : workTree.ToString());
        }

        internal static GitStatus ParseStatus(string value)
        {
            return value switch
            {
                "A" => GitStatus.Added,
                "M" => GitStatus.Modified,
                "D" => GitStatus.Deleted,
                "R" => GitStatus.Renamed,
                "C" => GitStatus.Copied,
                "T" => GitStatus.TypeChanged,
                "U" => GitStatus.Unmerged,
                "?" => GitStatus.Untracked,
                "!" => GitStatus.Ignored,
                "X" => GitStatus.Unknown,
                "B" => GitStatus.Broken,
                _ => GitStatus.Unknown
            };
        }
    }
}
