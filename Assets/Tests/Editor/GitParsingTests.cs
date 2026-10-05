using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Upwake.Vetka.Tests
{
    internal class GitParsingTests
    {
        [TestCase('?', '?', GitStatus.Untracked)]
        [TestCase('!', '!', GitStatus.Ignored)]
        [TestCase(' ', 'M', GitStatus.Modified)]
        [TestCase('M', ' ', GitStatus.Modified)]
        [TestCase('A', ' ', GitStatus.Added)]
        [TestCase(' ', 'D', GitStatus.Deleted)]
        [TestCase('A', 'D', GitStatus.Deleted)]
        [TestCase('M', 'D', GitStatus.Deleted)]
        [TestCase('A', 'M', GitStatus.Added)]
        [TestCase('U', 'U', GitStatus.Unmerged)]
        [TestCase('A', 'A', GitStatus.Unmerged)]
        [TestCase('D', 'D', GitStatus.Unmerged)]
        public void ParseStatus_MapsPorcelainCodes(char index, char workTree, GitStatus expected)
        {
            Assert.AreEqual(expected, Git.ParseStatus(index, workTree));
        }

        [Test]
        public void Windows_DoNotKeepBusyStateAcrossADomainReload()
        {
            var transient = new HashSet<string>
            {
                "_git", "_loading", "_loaded", "_pushing", "_committing", "_commitRequested", "_amendRequested",
                "_closing", "_aborted", "_focused"
            };
            var windows = typeof(Git).Assembly.GetTypes()
                .Where(type => typeof(UnityEditor.EditorWindow).IsAssignableFrom(type) && type != typeof(GitSettingsWindow));
            foreach (var window in windows)
            {
                foreach (var field in window.GetFields(System.Reflection.BindingFlags.Instance |
                                                       System.Reflection.BindingFlags.NonPublic)
                             .Where(field => transient.Contains(field.Name)))
                {
                    Assert.IsTrue(field.IsNotSerialized, $"{window.Name}.{field.Name} survives a domain reload");
                }
            }
        }

        [Test]
        public void ReadProgress_ReportsStagesAndKeepsOnlyMessages()
        {
            const string stderr =
                "Enumerating objects: 4, done.\n" +
                "Counting objects:  50% (2/4)\r" +
                "Counting objects: 100% (4/4)\r" +
                "Counting objects: 100% (4/4), done.\n" +
                "Delta compression using up to 24 threads\n" +
                "remote: Compressing objects:  50% (1/2)        \r" +
                "remote: Compressing objects: 100% (2/2), done.        \n" +
                "Writing objects:  33% (1/3)\r" +
                "Writing objects: 100% (3/3), 240 bytes | 240.00 KiB/s, done.\n" +
                "Total 3 (delta 1), reused 0 (delta 0)\n" +
                "   4b06a16..e11e688  HEAD -> main\n";

            var reports = new List<string>();
            var message = Git.ReadProgress(new StringReader(stderr),
                (progress, description) =>
                    reports.Add($"{progress.ToString("0.00", CultureInfo.InvariantCulture)} {description}"));

            CollectionAssert.AreEqual(new[]
            {
                "0.50 Counting objects 50%",
                "1.00 Counting objects 100%",
                "1.00 Counting objects 100%",
                "0.50 Compressing objects 50%",
                "1.00 Compressing objects 100%",
                "0.33 Writing objects 33%",
                "1.00 Writing objects 100%"
            }, reports);
            Assert.AreEqual("Total 3 (delta 1), reused 0 (delta 0)\n   4b06a16..e11e688  HEAD -> main",
                message.Replace("\r\n", "\n").Trim());
        }

        [Test]
        public void ReadProgress_KeepsErrorsIntact()
        {
            const string stderr =
                "fatal: 'nowhere' does not appear to be a git repository\n" +
                "fatal: Could not read from remote repository.\n";

            var reports = new List<string>();
            var message = Git.ReadProgress(new StringReader(stderr), (_, description) => reports.Add(description));

            Assert.IsEmpty(reports);
            Assert.AreEqual(
                "fatal: 'nowhere' does not appear to be a git repository\nfatal: Could not read from remote repository.",
                message.Replace("\r\n", "\n").Trim());
        }

        [Test]
        public void DiffParse_NumbersLinesAndSkipsHeaders()
        {
            const string diff =
                "diff --git a/f.txt b/f.txt\n" +
                "index 1111111..2222222 100644\n" +
                "--- a/f.txt\n" +
                "+++ b/f.txt\n" +
                "@@ -1,3 +1,4 @@\n" +
                " a\n" +
                "-b\n" +
                "+B\n" +
                " c\n" +
                "+d\n" +
                "\\ No newline at end of file";

            var lines = DiffWindow.Parse(new GitResult(0, diff, ""));

            Assert.AreEqual(new[]
            {
                "Hunk|||@@ -1,3 +1,4 @@",
                "Context|1|1|a",
                "Removed|2||b",
                "Added||2|B",
                "Context|3|3|c",
                "Added||4|d",
                "Info|||\\ No newline at end of file"
            }, Describe(lines));
        }

        [Test]
        public void DiffParse_DeletedFileStartsAtLineOne()
        {
            const string diff =
                "diff --git a/gone.txt b/gone.txt\n" +
                "deleted file mode 100644\n" +
                "index 286c5f5..0000000\n" +
                "--- a/gone.txt\n" +
                "+++ /dev/null\n" +
                "@@ -1 +0,0 @@\n" +
                "-gone";

            var lines = DiffWindow.Parse(new GitResult(0, diff, ""));

            Assert.AreEqual(new[]
            {
                "Info|||deleted file mode 100644",
                "Hunk|||@@ -1 +0,0 @@",
                "Removed|1||gone"
            }, Describe(lines));
        }

        [Test]
        public void DiffParse_BinaryEmptyAndFailedResults()
        {
            var binary = DiffWindow.Parse(new GitResult(0,
                "diff --git a/i.png b/i.png\nindex 1..2 100644\nBinary files a/i.png and b/i.png differ", ""));
            var empty = DiffWindow.Parse(new GitResult(0, "", ""));
            var failed = DiffWindow.Parse(new GitResult(128, "", "fatal: bad revision\nsecond line"));

            Assert.AreEqual(new[] { "Info|||Binary files a/i.png and b/i.png differ" }, Describe(binary));
            Assert.AreEqual(new[] { "Info|||No differences" }, Describe(empty));
            Assert.AreEqual(new[] { "Info|||bad revision", "Info|||second line" }, Describe(failed));
        }

        [TestCase("Reading", "status", "--porcelain")]
        [TestCase("Reading", "-C", "E:/repo", "diff", "--no-index")]
        [TestCase("Reading", "--version")]
        [TestCase("Network", "fetch", "--progress")]
        [TestCase("Network", "push", "origin", "HEAD")]
        [TestCase("Changing", "merge", "--no-edit", "origin/main")]
        [TestCase("Changing", "stash", "list")]
        public void CommandKind_DecidesHowLongACommandMayRun(string expected, params string[] arguments)
        {
            Assert.AreEqual(expected, Git.Kind(arguments).ToString());
        }

        [Test]
        public void PatchedPaths_ListsChangedFilesAndRenameSources()
        {
            var patch = Path.Combine(Path.GetTempPath(), $"Vetka-{System.Guid.NewGuid():N}.patch");
            File.WriteAllText(patch,
                "diff --git a/x.txt b/y z.txt\nsimilarity index 100%\nrename from x.txt\nrename to y z.txt\n" +
                "diff --git \"a/q\\\"\\321\\217.txt\" b/r.txt\nrename from \"q\\\"\\321\\217.txt\"\nrename to r.txt\n");
            try
            {
                var paths = Git.PatchedPaths("1\t1\tSub Dir/a b.txt\u00000\t0\ty z.txt\u00000\t0\tr.txt\u0000", patch);

                CollectionAssert.AreEquivalent(new[] { "Sub Dir/a b.txt", "y z.txt", "r.txt", "x.txt", "q\"я.txt" },
                    paths);
            }
            finally
            {
                File.Delete(patch);
            }
        }

        [TestCase(true, "merge", "--abort")]
        [TestCase(true, "stash", "pop", "--index")]
        [TestCase(true, "rebase", "--abort")]
        [TestCase(true, "-C", "E:/repo", "rm", "--cached")]
        [TestCase(false, "stash", "push", "-m", "x")]
        [TestCase(false, "rebase", "origin/main")]
        [TestCase(false, "merge", "--no-stat", "--no-edit", "origin/main")]
        public void IndexLock_RepeatsOnlyCommandsThatStopBeforeChangingAnything(bool expected,
            params string[] arguments)
        {
            Assert.AreEqual(expected, Git.MayRepeat(arguments));
        }

        [Test]
        public void OperationContext_AcceptsCancelOnlyInsideAnInterruptiblePart()
        {
            var context = new OperationContext(null);
            Assert.IsFalse(context.TryCancel());

            var accepted = OperationContext.With(context,
                () => OperationContext.Interruptible(() => context.TryCancel(), out _));

            Assert.IsTrue(accepted);
            Assert.IsTrue(context.IsCancelled);
            Assert.IsFalse(context.ShouldStop);
        }

        [TestCase("ref: refs/heads/main\n", "main")]
        [TestCase("ref: refs/heads/фича/x", "фича/x")]
        [TestCase("ref: refs/remotes/origin/main", "refs/remotes/origin/main")]
        [TestCase("0123456789abcdef0123456789abcdef01234567\n", "detached at 0123456")]
        [TestCase("", null)]
        [TestCase("ref: refs/heads/.invalid\n", null)]
        public void BranchName_ReadsHeadFile(string head, string expected)
        {
            Assert.AreEqual(expected, BranchTitle.BranchName(head));
        }

        [Test]
        public void FindHead_WalksUpToTheRepositoryAndFollowsGitdirFiles()
        {
            var root = Path.Combine(Path.GetTempPath(), "VetkaTests", Path.GetRandomFileName());
            try
            {
                var repository = Path.Combine(root, "repo");
                var project = Path.Combine(repository, "Game", "UnityProject");
                Directory.CreateDirectory(Path.Combine(repository, ".git"));
                Directory.CreateDirectory(project);

                var worktree = Path.Combine(root, "worktree");
                var worktreeGitDir = Path.Combine(repository, ".git", "worktrees", "wt");
                Directory.CreateDirectory(worktree);
                Directory.CreateDirectory(worktreeGitDir);
                File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../repo/.git/worktrees/wt\n");

                Assert.AreEqual(Path.Combine(repository, ".git", "HEAD"), BranchTitle.FindHead(project));
                Assert.AreEqual(Path.Combine(worktreeGitDir, "HEAD"), BranchTitle.FindHead(worktree));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ProjectStatus_ShowsAFileRemovedFromGitButKeptOnDiskAsDeleted(bool deletedFirst)
        {
            var deleted = new GitFileChange(GitStatus.Deleted, "Assets/c.txt");
            var untracked = new GitFileChange(GitStatus.Untracked, "Assets/c.txt");

            var snapshot = ProjectStatus.Map(deletedFirst ? new[] { deleted, untracked } : new[] { untracked, deleted },
                "");

            Assert.AreEqual(GitStatus.Deleted, snapshot.Changes["Assets/c.txt"].Status);
            Assert.IsFalse(snapshot.Files.ContainsKey("Assets/c.txt"));
        }

        [Test]
        public void Reads_OutsideARepository_ReportTheGitError()
        {
            var directory = Path.Combine(Path.GetTempPath(), "VetkaTests", System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var git = TestRepository.GitAt(directory);

                Assert.IsFalse(git.ReadLog().Result.IsSuccess);
                Assert.IsFalse(git.StashList().Result.IsSuccess);
                Assert.IsFalse(git.ReadBranches().Result.IsSuccess);
                var update = git.UpdateProject(UpdateStrategy.Merge);
                Assert.IsFalse(update.IsSuccess);
                StringAssert.DoesNotContain("no branch is checked out", update.Message);
                StringAssert.Contains("not a git repository", git.PushDestination().Problem);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ProjectStatus_MapsRepositoryPathsToAssetsAndFolders()
        {
            var snapshot = ProjectStatus.Map(new[]
            {
                new GitFileChange(GitStatus.Modified, "Game/Assets/Scripts/Player.cs"),
                new GitFileChange(GitStatus.Modified, "Game/Assets/Art/Hero.png.meta"),
                new GitFileChange(GitStatus.Untracked, "Game/Assets/New/Enemy.cs"),
                new GitFileChange(GitStatus.Untracked, "Game/Assets/New/Enemy.cs.meta"),
                new GitFileChange(GitStatus.Untracked, "Game/Assets/New.meta"),
                new GitFileChange(GitStatus.Deleted, "Game/Assets/Old/Gone.cs"),
                new GitFileChange(GitStatus.Renamed, "Game/Assets/Moved/Thing.cs", "Game/Assets/From/Thing.cs"),
                new GitFileChange(GitStatus.Modified, "Tools/build.sh")
            }, "Game/");

            CollectionAssert.AreEquivalent(new Dictionary<string, GitStatus>
            {
                ["Game/Assets/Scripts/Player.cs"] = GitStatus.Modified,
                ["Game/Assets/Art/Hero.png"] = GitStatus.Modified,
                ["Game/Assets/New/Enemy.cs"] = GitStatus.Untracked,
                ["Game/Assets/New"] = GitStatus.Untracked,
                ["Game/Assets/Moved/Thing.cs"] = GitStatus.Renamed,
                ["Tools/build.sh"] = GitStatus.Modified
            }, snapshot.Files);
            CollectionAssert.AreEquivalent(new[]
            {
                "Game", "Game/Assets", "Game/Assets/Scripts", "Game/Assets/Art", "Game/Assets/New", "Game/Assets/Old",
                "Game/Assets/Moved", "Game/Assets/From", "Tools"
            }, snapshot.Folders);
            Assert.AreEqual(8, snapshot.Entries.Count);
            Assert.AreEqual("Tools/build.sh", snapshot.Entries[7].Path);
            Assert.AreEqual("Game/Assets/Scripts/Player.cs",
                ProjectStatus.RepositoryPath(snapshot, "Assets/Scripts/Player.cs"));
            Assert.IsNull(ProjectStatus.RepositoryPath(new ProjectStatus.Snapshot(), "Assets/Scripts/Player.cs"));
        }

        [TestCase("E:/repo", @"E:\repo\Game\Packages\Some Folder", "Game/Packages/Some Folder")]
        [TestCase("E:/repo", @"E:\repo\Shared\com.studio.tools", "Shared/com.studio.tools")]
        [TestCase("E:/repo", @"E:\elsewhere\package", null)]
        [TestCase("E:/repo", @"D:\package", null)]
        public void PackageRoot_MapsThePackageFolderIntoTheRepository(string top, string resolved, string expected)
        {
            Assert.AreEqual(expected, ProjectStatus.PackageRoot(top, resolved));
        }

        [Test]
        public void DiffParse_SecondFileHeadersAreNotContent()
        {
            var diff = new GitResult(0, string.Join("\n",
                "diff --git a/old.txt b/old.txt",
                "deleted file mode 100644",
                "index 1111111..0000000",
                "--- a/old.txt",
                "+++ /dev/null",
                "@@ -1,2 +0,0 @@",
                "-a",
                "-b",
                "diff --git a/new.txt b/new.txt",
                "new file mode 100644",
                "index 0000000..2222222",
                "--- /dev/null",
                "+++ b/new.txt",
                "@@ -0,0 +1 @@",
                "+c"), "");

            var lines = DiffWindow.Parse(diff);

            CollectionAssert.AreEqual(new[]
            {
                "Info deleted file mode 100644 /", "Hunk @@ -1,2 +0,0 @@ /", "Removed a 1/", "Removed b 2/",
                "Info diff --git a/new.txt b/new.txt /", "Info new file mode 100644 /", "Hunk @@ -0,0 +1 @@ /",
                "Added c /1"
            }, lines.Select(line => $"{line.Kind} {line.Text} {line.OldNumber}/{line.NewNumber}"));
        }

        [Test]
        public void ParseBlame_UnderstandsSha256Hashes()
        {
            var first = new string('a', 64);
            var output = string.Join("\n",
                $"{first} 1 1 2",
                "author Alice",
                "author-time 1700000000",
                "summary First commit",
                "filename a.txt",
                "\tline one",
                $"{first} 2 2",
                "\tline two");

            var lines = Git.ParseBlame(output);

            Assert.AreEqual(2, lines.Count);
            Assert.AreEqual(first, lines[1].Hash);
            Assert.IsTrue(lines[1].IsCommitted);
            Assert.AreEqual("Alice", lines[1].Author);
            Assert.AreEqual("First commit", lines[1].Summary);
            Assert.AreEqual("line two", lines[1].Text);
        }

        [Test]
        public void FileActions_UseExactlyTheSelectedFiles()
        {
            var all = new[]
            {
                new GitFileChange(GitStatus.Modified, "Assets/a.prefab"),
                new GitFileChange(GitStatus.Modified, "Assets/a.prefab.meta"),
                new GitFileChange(GitStatus.Untracked, "Assets/b.cs"),
                new GitFileChange(GitStatus.Untracked, "Assets/b.cs.meta")
            };

            var single = FileActions.ForSelection(new[] { all[0] }, false);
            CollectionAssert.AreEqual(new[] { "Assets/a.prefab" }, single.Changes.Select(change => change.Path));
            Assert.AreEqual("Assets/a.prefab", single.File);
            Assert.AreEqual(1, single.Tracked.Count);
            Assert.AreEqual(0, single.Untracked.Count);

            var pair = FileActions.ForSelection(new[] { all[1], all[0] }, true);
            Assert.IsNull(pair.File);
            Assert.AreEqual(2, pair.Changes.Count);
            Assert.IsTrue(pair.Busy);

            var mixed = FileActions.ForSelection(new[] { all[0], all[2] }, false);
            Assert.IsNull(mixed.File);
            Assert.IsFalse(mixed.FileChange.HasValue);
            CollectionAssert.AreEqual(new[] { "Assets/b.cs" }, mixed.Untracked.Select(change => change.Path));
            CollectionAssert.AreEqual(new[] { "Assets/a.prefab" }, mixed.Tracked.Select(change => change.Path));
        }

        [Test]
        public void CommitMessageHistory_PutsTheNewestFirstWithoutDuplicates()
        {
            var messages = CommitMessageHistory.Prepend(new[] { "b", "a", "c" }, "  a \n", 20);
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, messages);

            var limited = CommitMessageHistory.Prepend(new[] { "1", "2", "3" }, "new", 3);
            CollectionAssert.AreEqual(new[] { "new", "1", "2" }, limited);

            Assert.IsNull(CommitMessageHistory.Prepend(new[] { "x" }, "   ", 20));
            Assert.IsNull(CommitMessageHistory.Prepend(new[] { "x" }, null, 20));
        }

        [TestCase(GitStatus.Modified, "M")]
        [TestCase(GitStatus.Added, "A")]
        [TestCase(GitStatus.Untracked, "U")]
        [TestCase(GitStatus.Renamed, "R")]
        [TestCase(GitStatus.Unmerged, "!")]
        public void ProjectStatus_Letters(GitStatus status, string letter)
        {
            Assert.AreEqual(letter, ProjectStatus.Letter(status));
        }

        [Test]
        public void GitResultMessage_PrefersOutputThenErrorThenAFallback()
        {
            Assert.AreEqual("out", new GitResult(0, "out", "err").Message);
            Assert.AreEqual("err", new GitResult(0, "", "err").Message);
            Assert.AreEqual("Done", new GitResult(0, "", "").Message);
            Assert.AreEqual("err\nout", new GitResult(1, "out", "err").Message);
            Assert.AreEqual("git exited with code 3", new GitResult(3, "", "").Message);
        }

        [Test]
        public void GitResultMessage_DropsHintsAndErrorPrefixesFromFailures()
        {
            var result = new GitResult(128,
                "",
                "fatal: pathspec 'a.txt' did not match any files\r\nhint: use --force\n\nerror: second\n  indented");

            Assert.AreEqual("pathspec 'a.txt' did not match any files\nsecond\n  indented", result.Message);
        }

        [Test]
        public void GitIdentity_IsCompleteOnlyWithNameAndEmail()
        {
            Assert.IsTrue(new GitIdentity("Name", "mail@example.com").IsComplete);
            Assert.IsFalse(new GitIdentity("Name", " ").IsComplete);
            Assert.IsFalse(new GitIdentity(null, "mail@example.com").IsComplete);
        }

        [Test]
        public void Notification_ShortensOnlyLongMessages()
        {
            Assert.AreEqual("short\nmessage", Notification.Shorten("short\nmessage"));

            var many = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}"));
            Assert.AreEqual(string.Join("\n", Enumerable.Range(1, 25).Select(i => $"line {i}")) +
                            "\nThe whole message is in the Console", Notification.Shorten(many));

            Assert.AreEqual(new string('x', 3000) + "\nThe whole message is in the Console",
                Notification.Shorten(new string('x', 5000)));
        }

        private static string[] Describe(IEnumerable<DiffWindow.DiffLine> lines) =>
            lines.Select(line => $"{line.Kind}|{line.OldNumber}|{line.NewNumber}|{line.Text}").ToArray();
    }
}
