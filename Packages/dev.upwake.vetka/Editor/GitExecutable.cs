using System;
using System.IO;
using System.Linq;

namespace Upwake.Vetka
{
    internal static class GitExecutable
    {
        public static string Find() => FindInPath() ?? FindInStandardPaths();

        public static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static string FindInPath()
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

        private static string FindInStandardPaths()
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
    }
}
