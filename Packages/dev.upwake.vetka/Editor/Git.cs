using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System;
using Debug = UnityEngine.Debug;
using UnityEngine;

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
            : this(gitPath, ProjectRoot ?? Directory.GetCurrentDirectory())
        {
        }

        internal Git(string gitPath, string workingDirectory, IReadOnlyDictionary<string, string> environment = null)
        {
            _gitPath = gitPath;
            _projectRoot = workingDirectory;
            _environment = environment;
        }

        internal static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName;

        private string GitPath => _gitPath ?? GitSettings.GitPath;

        private static bool Verbose => GitSettings.VerboseLogging;

        public GitResult Version()
        {
            var version = Run("--version");
            return version.IsSuccess && !IsSupportedVersion(version.Output)
                ? GitResult.Failure($"{version.Output}\nVetka needs Git 2.31 or newer")
                : version;
        }

        internal static bool IsSupportedVersion(string output)
        {
            var match = System.Text.RegularExpressions.Regex.Match(output, @"^git version (\d+)\.(\d+)");
            if (!match.Success)
            {
                return true;
            }

            var major = int.Parse(match.Groups[1].Value);
            var minor = int.Parse(match.Groups[2].Value);
            return major > 2 || major == 2 && minor >= 31;
        }

        private GitResult TopLevel()
        {
            if (_top != null)
            {
                return GitResult.Success(_top);
            }

            var top = Run("rev-parse", "--show-toplevel");
            if (!top.IsSuccess)
            {
                return top;
            }

            _top = Path.GetFullPath(top.Output);
            return GitResult.Success(_top);
        }

        private string GitDirPath(string name)
        {
            var path = Run("rev-parse", "--git-path", name);
            return path.IsSuccess ? Path.GetFullPath(Path.Combine(_projectRoot, path.Output)) : null;
        }

        private List<string> ChangedPaths(params string[] arguments) => ChangedPaths(null, arguments);

        private List<string> ChangedPaths(IReadOnlyDictionary<string, string> environment, params string[] arguments)
        {
            var diff = RunWithEnvironment(environment,
                new[] { "diff", "--name-only", "-z", "--no-renames" }.Concat(arguments).ToArray());
            return diff.IsSuccess ? NulSeparated(diff.Output) : null;
        }

        private int? CountCommits(params string[] range)
        {
            var count = Run(new[] { "rev-list", "--count" }.Concat(range).ToArray());
            return count.IsSuccess && int.TryParse(count.Output, out var parsed) ? parsed : (int?)null;
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
            var source = GitDirPath("index");
            if (source == null || !File.Exists(source))
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
            return Texts.Shortened(message.Trim().Split('\r', '\n')[0].Trim(), 60);
        }

        private static string LiteralPathspecs(IEnumerable<string> paths) =>
            string.Join("\n", paths.Select(path => ":(top,literal)" + path));

        private static List<string> NulSeparated(string output) =>
            output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        private static IEnumerable<(string Status, string Path, string OldPath)> NameStatus(
            IReadOnlyList<string> tokens, int start = 0)
        {
            for (var i = start; i < tokens.Count;)
            {
                var status = tokens[i].Trim('\n', '\r');
                if (status.Length == 0)
                {
                    i++;
                    continue;
                }

                if (i + 1 >= tokens.Count)
                {
                    yield break;
                }

                if ((status[0] == 'R' || status[0] == 'C') && i + 2 < tokens.Count)
                {
                    yield return (status, tokens[i + 2], tokens[i + 1]);
                    i += 3;
                    continue;
                }

                yield return (status, tokens[i + 1], null);
                i += 2;
            }
        }

        private static List<GitFileChange> FileChanges(string output) =>
            Sorted(NameStatus(output.Split('\0'))
                .Select(entry => new GitFileChange(ParseStatus(entry.Status.Substring(0, 1)), entry.Path, entry.OldPath))
                .ToList());

        private static List<string> Lines(string output) =>
            output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();

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
                Debug.LogWarning($"Vetka: cannot delete {path}: {exception.Message}");
            }
        }

        internal static GitStatus ParseStatus(char index, char workTree)
        {
            if (index == '?' && workTree == '?')
            {
                return GitStatus.Untracked;
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

        private static GitStatus ParseStatus(string value)
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
                _ => GitStatus.Unknown
            };
        }

        private static readonly (string Path, string Name)[] OperationsInProgress =
        {
            ("MERGE_HEAD", "a merge"),
            ("CHERRY_PICK_HEAD", "a cherry-pick"),
            ("REVERT_HEAD", "a revert"),
            ("rebase-merge", "a rebase"),
            ("rebase-apply", "a rebase")
        };

        private GitResult? RefuseDuringOperation(string description, string allowed = null)
        {
            var (state, inProgress) = OperationInProgress();
            if (!state.IsSuccess)
            {
                return state;
            }

            return inProgress == null || inProgress == allowed
                ? (GitResult?)null
                : GitResult.Failure(
                    $"{description} is not possible while {inProgress} is in progress, finish or abort it first");
        }

        private (GitResult State, string Name) OperationInProgress()
        {
            var arguments = new List<string> { "rev-parse" };
            foreach (var operation in OperationsInProgress)
            {
                arguments.Add("--git-path");
                arguments.Add(operation.Path);
            }

            var paths = Run(arguments.ToArray());
            if (!paths.IsSuccess)
            {
                return (paths, null);
            }

            var lines = Lines(paths.Output);
            for (var i = 0; i < OperationsInProgress.Length && i < lines.Count; i++)
            {
                var path = Path.GetFullPath(Path.Combine(_projectRoot, lines[i]));
                if (File.Exists(path) || Directory.Exists(path))
                {
                    return (paths, OperationsInProgress[i].Name);
                }
            }

            return (paths, null);
        }

        private List<string> Conflicts() => NulSeparated(Run("diff", "--name-only", "-z", "--diff-filter=U").Output);

        private const string StagingKept =
            "The staged version could not be restored, it is kept in the stash, see git stash list";

        private const string InUseHint =
            "Close the programs that use them and try again, a native plugin loaded by Unity needs an editor restart";

        private const string UnwrittenHint =
            "Check the free disk space and the folder permissions, for Git LFS files check the connection to the LFS server";

        private const string ModifiedHint =
            "Git sees them as changed because of their line endings or Git LFS settings, " +
            "commit them as they are or run git add --renormalize .";

        private const string StuckHint = ModifiedHint + " and try again";

        private static string ConflictList(List<string> conflicts) => FileList("Conflicts", conflicts);

        private static string FileList(string title, IEnumerable<string> files)
        {
            var all = files.ToList();
            var list = new StringBuilder(title + ":");
            foreach (var file in all.Take(10))
            {
                list.Append("\n  ").Append(file);
            }

            if (all.Count > 10)
            {
                list.Append($"\n  and {all.Count - 10} more");
            }

            return list.ToString();
        }

        public string HeadCommit() => CommitOf("HEAD");

        public string CommitOf(string reference)
        {
            var commit = Run("rev-parse", "-q", "--verify", reference + "^{commit}");
            return commit.IsSuccess ? commit.Output : null;
        }

        private static IEnumerable<string> PathFilter(IEnumerable<string> paths) =>
            new[] { "--" }.Concat(paths.Select(path => ":(top,literal)" + path));

        public string CurrentBranch() => CurrentBranch(out _);

        public string CurrentBranch(out GitResult error)
        {
            const string branches = "refs/heads/";
            var head = Run("symbolic-ref", "-q", "HEAD");
            error = head;
            if (head.IsSuccess)
            {
                return head.Output.StartsWith(branches) ? head.Output.Substring(branches.Length) : head.Output;
            }

            return head.ExitCode == 1 ? "HEAD" : null;
        }

        public string UpstreamBranch() => UpstreamBranch(out _);

        private string UpstreamBranch(out GitResult error)
        {
            error = Run("rev-parse", "--abbrev-ref", "@{u}");
            return error.IsSuccess ? error.Output : null;
        }

        private bool HasHead() => Run("rev-parse", "-q", "--verify", "HEAD").IsSuccess;
    }
}
