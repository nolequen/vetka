using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upwake.Vetka
{
    internal partial class Git
    {
        internal bool TrialMerges { get; set; } = true;

        internal bool RestoreTrials { get; set; } = true;

        private const int InUseChecks = 5000;

        private const string PartialNote =
            "Some file contents are not downloaded yet, press Refresh to download them for the full check";

        private string _emptyTree;

        public GitResult FetchUpstream()
        {
            var branch = CurrentBranch();
            if (string.IsNullOrEmpty(branch) || branch == "HEAD" || OperationInProgress().Name != null)
            {
                return GitResult.Success("");
            }

            var source = ResolveUpstream(branch);
            if (source.Local || source.Problem != null && !source.Missing)
            {
                return GitResult.Success("");
            }

            var before = CommitOf(source.Ref);
            var fetched = Interruptible("Fetch", () => Run("fetch", "--progress", "--no-tags",
                "--recurse-submodules=no", "--no-auto-maintenance", source.Remote, $"+{source.MergeRef}:{source.Ref}"));
            if (BranchMissing(fetched))
            {
                Run("update-ref", "-d", source.Ref);
                return GitResult.Success("");
            }

            if (!fetched.IsSuccess)
            {
                return fetched;
            }

            var after = CommitOf(source.Ref);
            if (before == null || after == null || before == after ||
                Run("merge-base", "--is-ancestor", before, after).IsSuccess)
            {
                return GitResult.Success("");
            }

            var gone = CountCommits(before, "--not", after) ?? 0;
            return GitResult.Success($"{source.Name} was rewritten on the remote: it no longer has " +
                                     $"{Texts.Counted(gone, "commit")} it had before. Update Project keeps those that " +
                                     $"{branch} has");
        }

        public GitIncoming ReadIncoming(IncomingCache cache, bool network, UpdateStrategyChoice strategy,
            int commitLimit, int fileLimit)
        {
            var incoming = new GitIncoming { LastFetch = LastFetch() };
            var location = ProjectLocation();
            incoming.Prefix = location.Prefix ?? "";
            var branch = CurrentBranch(out var error);
            if (branch == null)
            {
                incoming.Result = error.IsSuccess ? GitResult.Failure("Cannot read the current branch") : error;
                return incoming;
            }

            var busy = RefuseDuringOperation(UpdateProjectName);
            if (busy.HasValue)
            {
                incoming.Result = busy.Value;
                return incoming;
            }

            if (branch == "HEAD")
            {
                incoming.Result = GitResult.Failure("Cannot show incoming changes: no branch is checked out");
                return incoming;
            }

            incoming.Branch = branch;
            var source = ResolveUpstream(branch);
            incoming.Upstream = source.Name;
            incoming.LocalUpstream = source.Local;
            if (source.Problem != null)
            {
                incoming.Result = GitResult.Failure(source.Problem);
                return incoming;
            }

            incoming.Tip = CommitOf(source.Ref);
            if (incoming.Tip == null)
            {
                incoming.Result = GitResult.Failure(source.NotStored
                    ? $"{source.Name} has not been fetched yet, press Refresh"
                    : $"{source.Name} cannot be found");
                return incoming;
            }

            incoming.Head = HeadCommit();
            if (incoming.Head != null)
            {
                var mergeBase = Run("merge-base", incoming.Head, incoming.Tip);
                if (!mergeBase.IsSuccess)
                {
                    incoming.Result = mergeBase.ExitCode != 1 ? mergeBase
                        : Run("rev-parse", "--is-shallow-repository").Output == "true"
                            ? GitResult.Failure($"This shallow clone does not have the common history of {branch} " +
                                                $"and {source.Name}, fetch more history first")
                            : GitResult.Failure($"{branch} and {source.Name} have no common history, " +
                                                "Update Project cannot merge them");
                    return incoming;
                }

                incoming.MergeBase = Lines(mergeBase.Output)[0];
            }

            var only = CommitsOnlyIn(incoming.Tip, incoming.Head, commitLimit);
            if (!only.Result.IsSuccess)
            {
                incoming.Result = only.Result;
                return incoming;
            }

            incoming.IncomingCount = only.Count;
            incoming.Incoming = only.Commits;
            var local = LocalCommitsText(incoming, source);
            incoming.Summary = incoming.IncomingCount == 0
                ? $"{branch} is up to date with {source.Name}{local}"
                : Texts.Counted(incoming.IncomingCount, "incoming commit") + local;
            if (incoming.IncomingCount == 0)
            {
                return incoming;
            }

            var fastForward = incoming.Head == null ||
                              incoming.Head == incoming.MergeBase &&
                              (strategy == UpdateStrategyChoice.Rebase || !NoFastForward(branch));
            incoming.Action = fastForward ? $"Update Project will fast-forward {branch}"
                : strategy == UpdateStrategyChoice.Merge ? $"Update Project will merge them into {branch}"
                : strategy == UpdateStrategyChoice.Rebase ? $"Update Project will rebase {branch} onto them"
                : null;

            var drivers = MergeDrivers();
            var committed = cache != null && cache.Head == incoming.Head && cache.Tip == incoming.Tip
                ? cache
                : Analyze(incoming, drivers, network);
            incoming.Cache = committed.Partial ? null : committed;
            incoming.ResultTree = committed.ResultTree;
            incoming.DiffFrom = committed.Exact ? incoming.Head ?? EmptyTree() : incoming.MergeBase;
            incoming.DiffTo = committed.Exact ? committed.ResultTree : incoming.Tip;
            incoming.DiffLabel = committed.Exact ? "after Update Project" : $"changed on {source.Name}";
            incoming.Conflicted = committed.Conflicted.ToList();
            incoming.Note = committed.Note;

            var marks = new Dictionary<string, IncomingMark>(StringComparer.Ordinal);
            var files = committed.Files.ToList();
            var rowPaths = new HashSet<string>(files.Select(file => file.Path)
                .Concat(files.Where(file => file.OldPath != null).Select(file => file.OldPath)), StringComparer.Ordinal);

            void Mark(IEnumerable<string> paths, IncomingMark mark)
            {
                var marked = new HashSet<string>(paths, StringComparer.Ordinal);
                foreach (var file in files.Where(file => marked.Contains(file.Path) ||
                                                         file.OldPath != null && marked.Contains(file.OldPath)))
                {
                    if (!marks.TryGetValue(file.Path, out var current) || mark < current)
                    {
                        marks[file.Path] = mark;
                    }
                }
            }

            Mark(committed.Conflicted, IncomingMark.Conflict);
            Mark(committed.MayConflict, IncomingMark.MayConflict);
            Mark(committed.BothSides, IncomingMark.ChangedLocally);
            if (committed.Conflicted.Count > 0)
            {
                incoming.Warnings.Add(FileList("Update Project will stop on a conflict in these files and roll " +
                                               "everything back", committed.Conflicted) +
                                      "\nVetka cannot resolve conflicts, merge these changes with another Git tool");
            }
            else if (committed.ConflictsUnlisted)
            {
                incoming.Warnings.Add("Update Project will likely stop on a conflict and roll everything back");
            }

            var mayConflict = new List<string>(committed.MayConflict);
            var changedLocally = new List<string>(committed.BothSides);
            if (incoming.Head != null)
            {
                var status = Offline(network, "status", "--porcelain=v1", "-z", "--untracked-files=no", "--no-renames");
                if (status.IsSuccess)
                {
                    var entries = StatusEntries(status.Output).ToList();
                    var overlap = entries.Where(entry => !(entry.Index == 'A' && entry.WorkTree == 'D'))
                        .Select(entry => entry.Path).Where(rowPaths.Contains).Distinct().ToList();
                    var stagedNew = entries.Where(entry => entry.Index == 'A' && entry.WorkTree != 'D')
                        .Select(entry => entry.Path).ToList();
                    var trial = committed.Exact && RestoreTrials;
                    if (overlap.Count > 0 || trial && stagedNew.Count > 0)
                    {
                        var (yours, failure) = trial
                            ? RestoreTrial(incoming.Head, committed.ResultTree,
                                overlap.Concat(stagedNew).Distinct().ToList(), overlap, drivers, network)
                            : (null, null);
                        if (yours != null)
                        {
                            var ownDriver = drivers.Count > 0
                                ? WithOwnDriver(overlap.Where(path => !yours.Contains(path)).ToList(), drivers)
                                : new List<string>();
                            Mark(ownDriver, IncomingMark.MayConflict);
                            mayConflict.AddRange(ownDriver.Where(path => !mayConflict.Contains(path)));
                            Mark(yours, IncomingMark.ConflictsWithYourChanges);
                            if (yours.Count > 0)
                            {
                                incoming.Warnings.Add(FileList("Your uncommitted changes to these files conflict with " +
                                                               "the incoming ones, Update Project will roll " +
                                                               "everything back", yours) +
                                                      "\nRoll back or stash your changes to them, update, then make " +
                                                      "the changes again");
                            }
                        }
                        else
                        {
                            if (!network && MissingObjects(failure))
                            {
                                incoming.Note ??= PartialNote;
                            }

                            Mark(overlap, IncomingMark.ChangedLocally);
                            changedLocally.AddRange(overlap.Where(path => !changedLocally.Contains(path)));
                        }
                    }
                }
            }

            if (mayConflict.Count > 0)
            {
                incoming.Warnings.Add(FileList("These files are changed on both sides and use their own merge " +
                                               "driver, which this check does not run. Update Project may stop on " +
                                               "them and roll everything back", mayConflict));
            }

            if (changedLocally.Count > 0)
            {
                incoming.Warnings.Add(FileList("These files are also changed locally, Update Project may stop on a " +
                                               "conflict in them and roll everything back", changedLocally));
            }

            AddRefusals(incoming, strategy, files, Mark);
            var hidden = HiddenChanges(rowPaths);
            Mark(hidden, IncomingMark.HiddenChange);
            if (hidden.Count > 0)
            {
                incoming.Warnings.Add(FileList("These files are marked as unchanged in Git (skip-worktree or " +
                                               "assume-unchanged) but differ on disk, Update Project will fail " +
                                               "on them", hidden) +
                                      "\nClear the mark with git update-index and commit or roll back the files " +
                                      "first");
            }

            var top = TopLevel();
            if (top.IsSuccess)
            {
                var used = FileLocks.InUse(top.Output, rowPaths.OrderBy(path => path, StringComparer.Ordinal),
                    InUseChecks);
                Mark(used, IncomingMark.InUse);
                if (used.Count > 0)
                {
                    incoming.Warnings.Add(FileList("These files are in use by another program, Update Project " +
                                                   "cannot replace them and will roll everything back", used) +
                                          $"\n{InUseHint}");
                }
            }

            incoming.FileCount = files.Count;
            incoming.Marks = marks;
            incoming.Files = files
                .OrderBy(file => marks.TryGetValue(file.Path, out var mark) ? mark : IncomingMark.None)
                .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .Take(fileLimit)
                .ToList();
            return incoming;
        }

        private void AddRefusals(GitIncoming incoming, UpdateStrategyChoice strategy, List<GitFileChange> files,
            Action<IEnumerable<string>, IncomingMark> mark)
        {
            if (incoming.Head != null && incoming.Head != incoming.MergeBase)
            {
                if (strategy == UpdateStrategyChoice.Rebase)
                {
                    var flattened = RebaseRefusal(incoming.Tip, incoming.Upstream);
                    if (flattened.HasValue)
                    {
                        incoming.Refusals.Add(flattened.Value.Message);
                    }
                }
                else if (FastForwardOnly(incoming.Branch))
                {
                    incoming.Refusals.Add($"Update Project will fail: Git is set to allow only fast-forward merges " +
                                          $"here and {incoming.Branch} has its own commits");
                }
            }

            var index = incoming.Head == null ? IndexEntries() : null;
            var incomingBase = strategy == UpdateStrategyChoice.Merge ? incoming.MergeBase : null;
            var inTheWay = UntrackedInTheWay(incoming.Head, incoming.Tip, incomingBase, index);
            var staged = index != null ? StagedDifferently(incoming.Tip, index) : new List<string>();
            if (inTheWay.Count > 0)
            {
                incoming.Refusals.Add(InTheWayText(UpdateProjectName, inTheWay));
            }

            if (staged.Count > 0)
            {
                incoming.Refusals.Add(StagedInTheWayText(UpdateProjectName, staged));
            }

            var blocked = new HashSet<string>(inTheWay.Concat(staged), StringComparer.Ordinal);
            if (blocked.Count == 0)
            {
                return;
            }

            bool Blocked(string path)
            {
                if (blocked.Contains(path))
                {
                    return true;
                }

                for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
                {
                    if (blocked.Contains(path.Substring(0, slash)))
                    {
                        return true;
                    }
                }

                return false;
            }

            mark(files.Select(file => file.Path).Where(Blocked), IncomingMark.InTheWay);
        }

        private string LocalCommitsText(GitIncoming incoming, UpstreamSource source)
        {
            if (incoming.Head == null)
            {
                return "";
            }

            var target = source.Local ? null : PushTarget();
            if (!source.Local && (!target.HasValue || target.Value.TrackingRef == source.Ref))
            {
                var outgoing = CountCommits(OutgoingRange(target, "HEAD").ToArray()) ?? 0;
                return outgoing > 0 ? $", {Texts.Counted(outgoing, "commit")} to push" : "";
            }

            var ahead = CountCommits($"{incoming.Tip}..HEAD") ?? 0;
            return ahead > 0 ? $", {Texts.Counted(ahead, "commit")} not in {source.Name}" : "";
        }

        private IncomingCache Analyze(GitIncoming incoming, List<string> drivers, bool network)
        {
            var result = new IncomingCache { Head = incoming.Head, Tip = incoming.Tip };
            var head = incoming.Head;
            var tip = incoming.Tip;
            if (head == null || head == incoming.MergeBase)
            {
                result.ResultTree = tip;
                result.Exact = true;
                result.Files = Changes(head ?? EmptyTree(), tip, head != null, network, result);
                return result;
            }

            var top = TopLevel();
            if (TrialMerges && top.IsSuccess)
            {
                var trial = RunWithEnvironment(NeutralDrivers(drivers), OfflineArguments(network, new[]
                {
                    "-C", top.Output, "merge-tree", "--write-tree", "--name-only", "-z", "--no-messages", head, tip
                }));
                var parsed = ParseTrialMerge(trial);
                if (parsed.HasValue)
                {
                    result.Exact = true;
                    result.ResultTree = parsed.Value.Tree;
                    result.Conflicted = parsed.Value.Conflicts;
                    result.ConflictsUnlisted = trial.ExitCode == 1 && parsed.Value.Conflicts.Count == 0;
                }
                else if (trial.ExitCode == 129)
                {
                    result.Note = "Git 2.38 or newer is needed for the exact conflict check";
                }
                else
                {
                    result.Partial = true;
                    result.Note = !network && MissingObjects(trial.Message)
                        ? PartialNote
                        : $"The conflict check failed: {trial.Message}";
                }
            }

            if (!result.Exact)
            {
                var remote = Changes(incoming.MergeBase, tip, true, network, result);
                result.Files = remote;
                var ours = ChangedPaths(incoming.MergeBase, head) ?? new List<string>();
                var theirs = new HashSet<string>(remote.Select(file => file.Path)
                    .Concat(remote.Where(file => file.OldPath != null).Select(file => file.OldPath)), StringComparer.Ordinal);
                result.BothSides = ours.Where(theirs.Contains).Distinct().ToList();
                return result;
            }

            if (drivers.Count > 0)
            {
                var ours = ChangedPaths(incoming.MergeBase, head) ?? new List<string>();
                var theirs = new HashSet<string>(ChangedPaths(incoming.MergeBase, tip) ?? new List<string>(),
                    StringComparer.Ordinal);
                var conflicted = new HashSet<string>(result.Conflicted, StringComparer.Ordinal);
                result.MayConflict = WithOwnDriver(ours.Where(path => theirs.Contains(path) && !conflicted.Contains(path))
                    .Distinct().ToList(), drivers);
            }

            var files = Changes(head, result.ResultTree, true, network, result);
            var listed = new HashSet<string>(files.Select(file => file.Path), StringComparer.Ordinal);
            var missing = result.Conflicted.Concat(result.MayConflict).Where(path => !listed.Contains(path)).ToList();
            if (missing.Count > 0)
            {
                var upstreamChanges = Changes(incoming.MergeBase, tip, true, network, result);
                foreach (var path in missing)
                {
                    var change = upstreamChanges.FirstOrDefault(file => file.Path == path);
                    files.Add(change.Path != null ? change : new GitFileChange(GitStatus.Modified, path));
                }
            }

            result.Files = files;
            return result;
        }

        private List<GitFileChange> Changes(string from, string to, bool renames, bool network, IncomingCache result)
        {
            var arguments = new List<string> { "diff", "--name-status", "-z", renames ? "--find-renames" : "--no-renames",
                from, to, "--" };
            var diff = Offline(network, arguments.ToArray());
            if (!diff.IsSuccess && renames && !network && MissingObjects(diff.Message))
            {
                result.Partial = true;
                result.Note = PartialNote;
                arguments[3] = "--no-renames";
                diff = Offline(false, arguments.ToArray());
            }

            if (diff.IsSuccess)
            {
                return FileChanges(diff.Output);
            }

            result.Partial = true;
            result.Note ??= $"The changed files cannot be read: {diff.Message}";
            return new List<GitFileChange>();
        }

        private (List<string> Conflicts, string Error) RestoreTrial(string head, string resultTree,
            List<string> paths, List<string> changed, List<string> drivers, bool network)
        {
            var objects = GitDirPath("objects");
            var top = TopLevel();
            if (objects == null || !top.IsSuccess)
            {
                return (null, "The repository cannot be read");
            }

            var directory = Path.Combine(Path.GetTempPath(), $"Vetka-{Guid.NewGuid():N}");
            var indexFile = directory + ".index";
            var environment = new Dictionary<string, string>
            {
                ["GIT_INDEX_FILE"] = indexFile,
                ["GIT_OBJECT_DIRECTORY"] = directory,
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = objects
            };
            try
            {
                Directory.CreateDirectory(directory);
                var read = RunWithEnvironment(environment, "read-tree", head);
                var added = read.IsSuccess ? RunWithPathspecs(environment, paths, "add", "-A", "-f") : read;
                var snapshot = added.IsSuccess ? RunWithEnvironment(environment, "write-tree") : added;
                if (!snapshot.IsSuccess)
                {
                    return (null, snapshot.Message);
                }

                if (network)
                {
                    foreach (var part in PathParts(changed))
                    {
                        Run(new[] { "diff", "--dirstat=files,0", head, resultTree }.Concat(PathFilter(part)).ToArray());
                    }
                }

                var merging = NeutralDrivers(drivers);
                merging["GIT_OBJECT_DIRECTORY"] = directory;
                merging["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = objects;
                var trial = RunWithEnvironment(merging, OfflineArguments(network, new[]
                {
                    "-C", top.Output, "merge-tree", "--write-tree", "--name-only", "-z", "--no-messages",
                    $"--merge-base={head}^{{tree}}", $"{resultTree}^{{tree}}", snapshot.Output
                }));
                var parsed = ParseTrialMerge(trial);
                return parsed.HasValue ? (parsed.Value.Conflicts, null) : (null, trial.Message);
            }
            catch (Exception exception)
            {
                return (null, exception.Message);
            }
            finally
            {
                DeleteTempFile(indexFile);
                DeleteTempDirectory(directory);
            }
        }

        private static void DeleteTempDirectory(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(directory, true);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning($"Vetka: cannot delete {directory}: {exception.Message}");
            }
        }

        internal static (string Tree, List<string> Conflicts)? ParseTrialMerge(GitResult result)
        {
            if (result.ExitCode != 0 && result.ExitCode != 1)
            {
                return null;
            }

            var tokens = result.Output.Split('\0');
            var tree = tokens[0].Trim();
            if (!IsObjectId(tree))
            {
                return null;
            }

            return (tree, tokens.Skip(1).Where(token => token.Length > 0).Distinct().ToList());
        }

        private static bool IsObjectId(string text) =>
            (text.Length == 40 || text.Length == 64) && text.All(Uri.IsHexDigit);

        private List<string> MergeDrivers()
        {
            const string prefix = "merge.";
            const string suffix = ".driver";
            var config = Run("config", "--name-only", "--get-regexp", @"^merge\..+\.driver$");
            return config.IsSuccess
                ? Lines(config.Output)
                    .Where(key => key.Length > prefix.Length + suffix.Length)
                    .Select(key => key.Substring(prefix.Length, key.Length - prefix.Length - suffix.Length))
                    .Distinct()
                    .ToList()
                : new List<string>();
        }

        private static Dictionary<string, string> NeutralDrivers(List<string> drivers)
        {
            var variables = new Dictionary<string, string>();
            if (drivers.Count == 0)
            {
                return variables;
            }

            variables["GIT_CONFIG_COUNT"] = drivers.Count.ToString();
            for (var i = 0; i < drivers.Count; i++)
            {
                variables[$"GIT_CONFIG_KEY_{i}"] = $"merge.{drivers[i]}.driver";
                variables[$"GIT_CONFIG_VALUE_{i}"] = "true";
            }

            return variables;
        }

        private static bool MissingObjects(string error) =>
            error != null && (error.Contains("not allowed") || error.Contains("promisor") ||
                              error.Contains("could not fetch"));

        private List<string> WithOwnDriver(List<string> paths, List<string> drivers)
        {
            var found = new List<string>();
            var top = TopLevel();
            if (paths.Count == 0 || !top.IsSuccess)
            {
                return found;
            }

            var custom = new HashSet<string>(drivers, StringComparer.Ordinal);
            foreach (var part in PathParts(paths))
            {
                var attributes = Run(new[] { "-C", top.Output, "check-attr", "-z", "merge", "--" }.Concat(part).ToArray());
                if (!attributes.IsSuccess)
                {
                    continue;
                }

                var tokens = attributes.Output.Split('\0');
                for (var i = 0; i + 2 < tokens.Length; i += 3)
                {
                    if (custom.Contains(tokens[i + 2]))
                    {
                        found.Add(tokens[i]);
                    }
                }
            }

            return found;
        }

        private GitResult Offline(bool network, params string[] arguments) => Run(OfflineArguments(network, arguments));

        private static string[] OfflineArguments(bool network, string[] arguments) =>
            network ? arguments : new[] { "-c", "protocol.allow=never" }.Concat(arguments).ToArray();

        private bool FastForwardOnly(string branch) => MergeSetting(branch, "only", "--ff-only");

        private bool NoFastForward(string branch) => MergeSetting(branch, "false", "--no-ff");

        private bool MergeSetting(string branch, string ff, string option)
        {
            var value = Run("config", "--get", "merge.ff").Output.Trim().ToLowerInvariant();
            var matches = ff == "false" ? value == "false" || value == "no" || value == "off" || value == "0"
                : value == ff;
            return matches || Run("config", "--get", $"branch.{branch}.mergeoptions").Output
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Contains(option);
        }

        private List<string> HiddenChanges(HashSet<string> paths)
        {
            var top = TopLevel();
            if (!top.IsSuccess || paths.Count == 0)
            {
                return new List<string>();
            }

            var listed = Run("ls-files", "-z", "-v", "--full-name", "--", ":/");
            var flagged = NulSeparated(listed.Output)
                .Where(record => record.Length > 2 && (record[0] == 'S' || char.IsLower(record[0])))
                .Select(record => record.Substring(2))
                .Where(path => paths.Contains(path) && File.Exists(Path.Combine(top.Output, path)))
                .Distinct()
                .ToList();
            if (flagged.Count == 0)
            {
                return flagged;
            }

            return WithTemporaryIndex(environment =>
            {
                var changed = new List<string>();
                foreach (var part in PathParts(flagged))
                {
                    var cleared = RunWithEnvironment(environment,
                        new[] { "-C", top.Output, "update-index", "--no-skip-worktree", "--" }.Concat(part).ToArray());
                    if (cleared.IsSuccess)
                    {
                        cleared = RunWithEnvironment(environment,
                            new[] { "-C", top.Output, "update-index", "--no-assume-unchanged", "--" }.Concat(part)
                                .ToArray());
                    }

                    var diff = cleared.IsSuccess
                        ? RunWithEnvironment(environment,
                            new[] { "diff-files", "--name-only", "-z" }.Concat(PathFilter(part)).ToArray())
                        : cleared;
                    if (diff.IsSuccess)
                    {
                        changed.AddRange(NulSeparated(diff.Output));
                    }
                }

                return changed;
            }) ?? new List<string>();
        }

        private DateTime? LastFetch()
        {
            var path = GitDirPath("FETCH_HEAD");
            return path != null && File.Exists(path) ? File.GetLastWriteTime(path) : (DateTime?)null;
        }

        private string EmptyTree()
        {
            if (_emptyTree == null)
            {
                var tree = Run("hash-object", "-t", "tree", "/dev/null");
                _emptyTree = tree.IsSuccess ? tree.Output : "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
            }

            return _emptyTree;
        }

        private sealed class IndexEntry
        {
            public char Tag;
            public string Oid;
            public int Stage;
            public string Path;
        }

        private List<IndexEntry> IndexEntries()
        {
            var listed = Run("ls-files", "-z", "-s", "-v", "--full-name", "--", ":/");
            if (!listed.IsSuccess)
            {
                return null;
            }

            var entries = new List<IndexEntry>();
            foreach (var record in listed.Output.Split('\0'))
            {
                var tab = record.IndexOf('\t');
                if (record.Length < 3 || tab < 2)
                {
                    continue;
                }

                var fields = record.Substring(2, tab - 2).Split(' ');
                entries.Add(new IndexEntry
                {
                    Tag = record[0],
                    Oid = fields.Length > 1 ? fields[1] : "",
                    Stage = fields.Length > 2 && int.TryParse(fields[2], out var stage) ? stage : 0,
                    Path = record.Substring(tab + 1)
                });
            }

            return entries;
        }

        private List<string> StagedDifferently(string target, List<IndexEntry> index)
        {
            var tree = Run("ls-tree", "-r", "-z", "--full-tree", target);
            if (!tree.IsSuccess)
            {
                return new List<string>();
            }

            var incoming = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var record in tree.Output.Split('\0'))
            {
                var tab = record.IndexOf('\t');
                var fields = tab > 0 ? record.Substring(0, tab).Split(' ') : Array.Empty<string>();
                if (fields.Length == 3)
                {
                    incoming[record.Substring(tab + 1)] = fields[2];
                }
            }

            return index
                .Where(entry => incoming.TryGetValue(entry.Path, out var oid) && (entry.Stage != 0 || oid != entry.Oid))
                .Select(entry => entry.Path)
                .Distinct()
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }

        private static string InTheWayText(string description, List<string> paths) =>
            FileList($"{description} is not possible, it would overwrite these files that Git does not track", paths) +
            "\nMove or delete them and try again";

        private static string StagedInTheWayText(string description, List<string> paths) =>
            FileList($"{description} is not possible, it would overwrite these staged files", paths) +
            "\nMove them out of the project and unstage them, then try again";

        private string FirstUpdateRefusal(string description, string target)
        {
            var index = IndexEntries();
            var inTheWay = UntrackedInTheWay(null, target, null, index);
            if (inTheWay.Count > 0)
            {
                return InTheWayText(description, inTheWay);
            }

            var staged = index != null ? StagedDifferently(target, index) : new List<string>();
            return staged.Count > 0 ? StagedInTheWayText(description, staged) : null;
        }
    }
}
