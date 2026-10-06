using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Upwake.Vetka.Tests
{
    internal abstract class ProjectFolderTests
    {
        private readonly string _projectFolder;

        protected ProjectFolderTests(string projectFolder)
        {
            _projectFolder = projectFolder;
        }

        [SetUp]
        public void UseProjectFolder() => TestRepository.ProjectFolder = _projectFolder;

        [TearDown]
        public void ResetProjectFolder() => TestRepository.ProjectFolder = "";
    }

    internal sealed class TestRepository : IDisposable
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        private readonly string _base;
        private readonly bool _ownsBase;
        private readonly Dictionary<string, string> _variables;

        private TestRepository(string root, string baseDirectory, bool ownsBase)
        {
            Root = root;
            _base = baseDirectory;
            _ownsBase = ownsBase;
            _variables = IsolatedVariables(baseDirectory);
        }

        public string Root { get; }

        public string BaseDirectory => _base;

        public string GlobalConfig => _variables["GIT_CONFIG_GLOBAL"];

        public static string ProjectFolder { get; set; } = "";

        public Git Git => NewGit(_variables);

        public Git GitWithVariable(string name, string value) =>
            GitWithVariables(new Dictionary<string, string> { [name] = value });

        public Git GitWithVariables(IReadOnlyDictionary<string, string> variables) => NewGit(WithVariables(variables));

        public static Git GitAt(string directory) => new Git(GitExecutable, directory, IsolatedVariables(directory));

        private Git NewGit(IReadOnlyDictionary<string, string> variables)
        {
            var project = Path.Combine(Root, ProjectFolder);
            Directory.CreateDirectory(project);
            return new Git(GitExecutable, project, variables);
        }

        private Dictionary<string, string> WithVariables(IReadOnlyDictionary<string, string> variables)
        {
            var all = new Dictionary<string, string>(_variables);
            foreach (var variable in variables)
            {
                all[variable.Key] = variable.Value;
            }

            return all;
        }

        private static Dictionary<string, string> IsolatedVariables(string baseDirectory)
        {
            var home = Path.Combine(baseDirectory, ".home");
            Directory.CreateDirectory(home);
            return new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["XDG_CONFIG_HOME"] = Path.Combine(home, ".config"),
                ["GIT_CONFIG_GLOBAL"] = Path.Combine(home, ".gitconfig"),
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_ATTR_NOSYSTEM"] = "1"
            };
        }

        public static string GitExecutable
        {
            get
            {
                var path = GitSettings.GitPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    path = Git.GetSystemGitPath();
                }

                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Assert.Ignore("Git is not installed.");
                }

                return path;
            }
        }

        public static TestRepository Create(string name = "repo")
        {
            var baseDirectory = NewBaseDirectory();
            var repository = new TestRepository(Path.Combine(baseDirectory, name), baseDirectory, true);
            Directory.CreateDirectory(repository.Root);
            repository.RunGit("init", "-q", "-b", "main");
            repository.Configure();
            return repository;
        }

        public static TestRepository CreateBare(string name = "remote.git")
        {
            var baseDirectory = NewBaseDirectory();
            var repository = new TestRepository(Path.Combine(baseDirectory, name), baseDirectory, true);
            Directory.CreateDirectory(repository.Root);
            repository.RunGit("init", "-q", "--bare", "-b", "main");
            return repository;
        }

        public TestRepository Clone(string name)
        {
            var clone = new TestRepository(Path.Combine(_base, name), _base, false);
            Run(_base, _variables, "clone", "-q", "-c", "core.autocrlf=false", Root, clone.Root);
            clone.Configure();
            return clone;
        }

        public string RunGit(params string[] arguments)
        {
            var (exitCode, output, error) = Run(Root, _variables, arguments);
            Assert.AreEqual(0, exitCode, $"git {string.Join(" ", arguments)} failed:\n{error}\n{output}");
            return output;
        }

        public int TryRunGit(params string[] arguments) => Run(Root, _variables, arguments).exitCode;

        public (int exitCode, string output, string error) RunGitWith(IReadOnlyDictionary<string, string> variables,
            params string[] arguments) => Run(Root, WithVariables(variables), arguments);

        public void Write(string path, string content) => WriteBytes(path, Utf8.GetBytes(content));

        public void WriteBytes(string path, byte[] content)
        {
            var file = FullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllBytes(file, content);
        }

        public string Read(string path) => File.ReadAllText(FullPath(path), Utf8);

        public byte[] ReadBytes(string path) => File.ReadAllBytes(FullPath(path));

        public bool Exists(string path) => File.Exists(FullPath(path)) || Directory.Exists(FullPath(path));

        public void Delete(string path) => File.Delete(FullPath(path));

        public IDisposable InUse(string path)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Assert.Ignore("Only Windows keeps a file that is in use from being replaced");
            }

            return File.Open(FullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public void CommitAll(string message)
        {
            RunGit("add", "-A");
            RunGit("commit", "-q", "-m", message);
        }

        public string Head => RunGit("rev-parse", "HEAD");

        public string CurrentBranch => RunGit("rev-parse", "--abbrev-ref", "HEAD");

        public string Snapshot()
        {
            var snapshot = new StringBuilder();
            snapshot.AppendLine("HEAD " + RunGit("rev-parse", "HEAD"));
            snapshot.AppendLine("BRANCH " + RunGit("rev-parse", "--abbrev-ref", "HEAD"));
            snapshot.AppendLine("BRANCHES " + RunGit("for-each-ref", "--format=%(refname)", "refs/heads"));
            snapshot.AppendLine("STATUS " + RunGit("status", "--porcelain=v1", "--untracked-files=all"));
            snapshot.AppendLine("UNSTAGED " + RunGit("diff"));
            snapshot.AppendLine("STAGED " + RunGit("diff", "--cached"));

            var files = Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
                .Select(file => file.Substring(Root.Length + 1).Replace('\\', '/'))
                .Where(file => !file.StartsWith(".git/"))
                .OrderBy(file => file, StringComparer.Ordinal);
            foreach (var file in files)
            {
                snapshot.AppendLine($"FILE {file}: {Convert.ToBase64String(ReadBytes(file))}");
            }

            return snapshot.ToString();
        }

        public void Dispose()
        {
            if (!_ownsBase || !Directory.Exists(_base))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(_base, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_base, true);
        }

        private void Configure()
        {
            RunGit("config", "user.name", "Test");
            RunGit("config", "user.email", "test@example.com");
            RunGit("config", "core.autocrlf", "false");
            RunGit("config", "commit.gpgsign", "false");
        }

        private string FullPath(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

        private static string NewBaseDirectory()
        {
            var directory = Path.Combine(Path.GetTempPath(), "VetkaTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static (int exitCode, string output, string error) Run(string workingDirectory,
            IReadOnlyDictionary<string, string> variables, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = GitExecutable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("core.quotepath=false");
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var variable in startInfo.Environment.Keys.Where(key => key.StartsWith("GIT_")).ToList())
            {
                startInfo.Environment.Remove(variable);
            }

            foreach (var variable in variables)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }

            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var process = Process.Start(startInfo);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, output.Result.Trim(), error.Result.Trim());
        }
    }
}
