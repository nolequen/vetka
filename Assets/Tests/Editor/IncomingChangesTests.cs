using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Upwake.Vetka.Tests
{
    [TestFixture("")]
    [TestFixture("Unity Проект")]
    internal class IncomingChangesTests : ProjectFolderTests
    {
        public IncomingChangesTests(string projectFolder) : base(projectFolder)
        {
        }

        private static GitIncoming Incoming(Git git, UpdateStrategyChoice strategy = UpdateStrategyChoice.Merge)
        {
            var fetched = git.FetchUpstream();
            Assert.IsTrue(fetched.IsSuccess, fetched.Message);
            var incoming = git.ReadIncoming(null, true, strategy, 100, 1000);
            Assert.IsTrue(incoming.Result.IsSuccess, incoming.Result.Message);
            return incoming;
        }

        private static string[] Paths(GitIncoming incoming) => incoming.Files.Select(file => file.Path).ToArray();

        private static IncomingMark Mark(GitIncoming incoming, string path) =>
            incoming.Marks.TryGetValue(path, out var mark) ? mark : IncomingMark.None;

        [Test]
        public void Incoming_ListsTheirCommitsAndOnlyTheFilesTheUpdateChanges()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.PushChange("f.txt", "theirs");
            upstream.Write("n.txt", "new\n");
            upstream.CommitAll("new file");
            upstream.Write("same.txt", "same\n");
            upstream.CommitAll("same change");
            upstream.RunGit("push", "-q");
            local.Write("o.txt", "mine\n");
            local.CommitAll("mine");
            local.Write("same.txt", "same\n");
            local.CommitAll("same change here too");

            var incoming = Incoming(local.Git);

            Assert.AreEqual(3, incoming.IncomingCount);
            Assert.AreEqual("3 incoming commits, 2 commits to push", incoming.Summary);
            Assert.AreEqual("Update Project will merge them into main", incoming.Action);
            CollectionAssert.AreEqual(new[] { "f.txt", "n.txt" }, Paths(incoming));
            Assert.AreEqual(GitStatus.Added, incoming.Files[1].Status);
            Assert.AreEqual(0, incoming.Marks.Count);
            Assert.AreEqual(0, incoming.Warnings.Count + incoming.Refusals.Count);
            StringAssert.Contains("+theirs",
                local.Git.RangeFileDiff(incoming.DiffFrom, incoming.DiffTo, "f.txt", null).Output);
        }

        [Test]
        public void Incoming_WithoutLocalCommits_SaysItFastForwards()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.PushChange("f.txt", "theirs");

            var incoming = Incoming(local.Git, UpdateStrategyChoice.Rebase);

            Assert.AreEqual("1 incoming commit", incoming.Summary);
            Assert.AreEqual("Update Project will fast-forward main", incoming.Action);
            CollectionAssert.AreEqual(new[] { "f.txt" }, Paths(incoming));
            Assert.AreEqual(local.RunGit("rev-parse", "origin/main"), incoming.DiffTo);
        }

        [Test]
        public void Incoming_WhenUpToDate_SaysSo()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            local.Write("o.txt", "mine\n");
            local.CommitAll("mine");

            var incoming = Incoming(local.Git);

            Assert.AreEqual(0, incoming.IncomingCount);
            Assert.AreEqual("main is up to date with origin/main, 1 commit to push", incoming.Summary);
            Assert.IsNull(incoming.Action);
        }

        [Test]
        public void Incoming_PredictsConflictsWithLocalCommits_AsUpdateProjectMeetsThem()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.WriteBytes("b.bin", new byte[] { 0, 1, 2 });
            upstream.Write("free.txt", "free\n");
            upstream.CommitAll("binary");
            upstream.RunGit("push", "-q");
            using var local = remote.Clone("local");
            upstream.Write("f.txt", "theirs\n");
            upstream.Delete("o.txt");
            upstream.WriteBytes("b.bin", new byte[] { 0, 9, 9 });
            upstream.Write("free.txt", "free change\n");
            upstream.CommitAll("theirs");
            upstream.RunGit("push", "-q");
            local.Write("f.txt", "mine\n");
            local.Write("o.txt", "edited here\n");
            local.WriteBytes("b.bin", new byte[] { 0, 7, 7 });
            local.CommitAll("mine");

            var incoming = Incoming(local.Git);

            CollectionAssert.AreEquivalent(new[] { "b.bin", "f.txt", "o.txt" }, incoming.Conflicted);
            CollectionAssert.AreEqual(new[] { "b.bin", "f.txt", "o.txt", "free.txt" }, Paths(incoming));
            Assert.IsTrue(new[] { "b.bin", "f.txt", "o.txt" }.All(path => Mark(incoming, path) == IncomingMark.Conflict));
            Assert.AreEqual(IncomingMark.None, Mark(incoming, "free.txt"));
            StringAssert.StartsWith("Update Project will stop on a conflict in these files and roll everything back",
                incoming.Warnings[0]);

            var update = local.Git.UpdateProject(UpdateStrategy.Merge);

            Assert.IsFalse(update.IsSuccess);
            StringAssert.Contains("f.txt", update.Message);
        }

        [Test]
        public void Incoming_PredictsWhichUncommittedChangesConflict()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.Write("f.txt", "a\nb\nc\nd\ne\n");
            upstream.Write("g.txt", "a\nb\nc\nd\ne\n");
            upstream.CommitAll("lines");
            upstream.RunGit("push", "-q");
            using var local = remote.Clone("local");
            upstream.Write("f.txt", "a\nTHEIRS\nc\nd\ne\n");
            upstream.Write("g.txt", "a\nTHEIRS\nc\nd\ne\n");
            upstream.CommitAll("theirs");
            upstream.RunGit("push", "-q");
            local.Write("f.txt", "a\nMINE\nc\nd\ne\n");
            local.Write("g.txt", "a\nb\nc\nd\nMINE\n");

            var incoming = Incoming(local.Git);

            Assert.AreEqual(IncomingMark.ConflictsWithYourChanges, Mark(incoming, "f.txt"));
            Assert.AreEqual(IncomingMark.None, Mark(incoming, "g.txt"));
            StringAssert.StartsWith("Your uncommitted changes to these files conflict with the incoming ones",
                incoming.Warnings.Single());
            StringAssert.Contains("\n  f.txt\n", incoming.Warnings.Single());

            var update = local.Git.UpdateProject(UpdateStrategy.Merge);

            Assert.IsFalse(update.IsSuccess);
            StringAssert.StartsWith("Merge of origin/main rolled back, it conflicts with local changes\nConflicts:\n  f.txt",
                update.Message);
        }

        [Test]
        public void Incoming_WithAStagedFileDeletedFromDisk_StillChecksTheOtherChanges()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.Write("f.txt", "theirs\n");
            upstream.Write("n.txt", "theirs\n");
            upstream.CommitAll("theirs");
            upstream.RunGit("push", "-q");
            local.Write("n.txt", "staged\n");
            local.RunGit("add", "n.txt");
            local.Delete("n.txt");
            local.Write("f.txt", "mine\n");

            var incoming = Incoming(local.Git);

            Assert.AreEqual(IncomingMark.ConflictsWithYourChanges, Mark(incoming, "f.txt"));
            Assert.IsNull(incoming.Note);
        }

        [Test]
        public void Incoming_WhenAFolderMovesUnderAStagedNewFile_PredictsTheConflict()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.Write("X/a.txt", "a\n");
            upstream.CommitAll("folder");
            upstream.RunGit("push", "-q");
            using var local = remote.Clone("local");
            upstream.RunGit("mv", "X", "Y");
            upstream.CommitAll("move the folder");
            upstream.RunGit("push", "-q");
            local.Write("X/new.txt", "new\n");
            local.RunGit("add", "X/new.txt");

            var incoming = Incoming(local.Git);

            StringAssert.StartsWith("Your uncommitted changes to these files conflict with the incoming ones",
                incoming.Warnings.Single());
            Assert.IsFalse(local.Git.UpdateProject(UpdateStrategy.Merge).IsSuccess);
        }

        [Test]
        public void Incoming_DoesNotMarkAFileTheUserRenamedAndStaged()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.Write("X.txt", "1\n2\n3\n4\n5\n6\n7\n8\n");
            upstream.CommitAll("x");
            upstream.RunGit("push", "-q");
            using var local = remote.Clone("local");
            upstream.Write("X.txt", "1\n2\n3\n4\n5\n6\n7\nTHEIRS\n");
            upstream.CommitAll("theirs");
            upstream.RunGit("push", "-q");
            local.RunGit("mv", "X.txt", "Z.txt");

            var incoming = Incoming(local.Git);

            Assert.AreEqual(IncomingMark.None, Mark(incoming, "X.txt"));
            Assert.IsFalse(incoming.Warnings.Any(warning => warning.StartsWith("Your uncommitted changes")),
                string.Join("\n", incoming.Warnings));
            Assert.IsTrue(local.Git.UpdateProject(UpdateStrategy.Merge).IsDone);
        }

        [Test]
        public void Incoming_WithoutTrialMerges_MarksFilesChangedOnBothSides()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.Write("f.txt", "theirs\n");
            upstream.Write("o.txt", "theirs\n");
            upstream.CommitAll("theirs");
            upstream.RunGit("push", "-q");
            local.Write("f.txt", "mine\n");
            local.CommitAll("mine");
            local.Write("o.txt", "uncommitted\n");
            var git = local.Git;
            git.TrialMerges = false;

            var incoming = Incoming(git);

            Assert.AreEqual(IncomingMark.ChangedLocally, Mark(incoming, "f.txt"));
            Assert.AreEqual(IncomingMark.ChangedLocally, Mark(incoming, "o.txt"));
            StringAssert.StartsWith("These files are also changed locally", incoming.Warnings.Single());
            Assert.AreEqual(local.RunGit("merge-base", "HEAD", "origin/main"), incoming.DiffFrom);
        }

        [Test]
        public void Incoming_WithoutRestoreTrials_MarksUncommittedChangesOnTheSameFiles()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.PushChange("f.txt", "theirs");
            local.Write("f.txt", "uncommitted\n");
            var git = local.Git;
            git.RestoreTrials = false;

            var incoming = Incoming(git);

            Assert.AreEqual(IncomingMark.ChangedLocally, Mark(incoming, "f.txt"));
        }

        [Test]
        public void Incoming_WithUntrackedFilesInTheWay_SaysUpdateProjectRefuses()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.Write("n.txt", "theirs\n");
            upstream.Write("Dir/x.txt", "theirs\n");
            upstream.CommitAll("new files");
            upstream.RunGit("push", "-q");
            local.Write("n.txt", "mine\n");
            local.Write("Dir", "a file where a folder comes\n");

            var incoming = Incoming(local.Git);

            Assert.AreEqual("Update Project is not possible, it would overwrite these files that Git does not track:" +
                            "\n  Dir\n  n.txt\nMove or delete them and try again", incoming.Refusals.Single());
            Assert.AreEqual(IncomingMark.InTheWay, Mark(incoming, "n.txt"));
            Assert.AreEqual(IncomingMark.InTheWay, Mark(incoming, "Dir/x.txt"));
        }

        [Test]
        public void Incoming_OnABranchWithoutCommits_ListsEverythingAndTheFilesInTheWay()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = TestRepository.Create("local");
            local.RunGit("remote", "add", "origin", remote.Root);
            local.RunGit("config", "branch.main.remote", "origin");
            local.RunGit("config", "branch.main.merge", "refs/heads/main");
            local.Write("o.txt", "different\n");
            local.Write("f.txt", "base\n");
            local.RunGit("add", "f.txt");

            var incoming = Incoming(local.Git);

            Assert.AreEqual("1 incoming commit", incoming.Summary);
            Assert.AreEqual("Update Project will fast-forward main", incoming.Action);
            CollectionAssert.AreEquivalent(new[] { "f.txt", "o.txt" }, Paths(incoming));
            Assert.AreEqual("Update Project is not possible, it would overwrite these files that Git does not track:" +
                            "\n  o.txt\nMove or delete them and try again", incoming.Refusals.Single());

            var update = local.Git.UpdateProject(UpdateStrategy.Merge);

            Assert.AreEqual("Merge of origin/main is not possible, it would overwrite these files that Git does not " +
                            "track:\n  o.txt\nMove or delete them and try again", update.Message);
            local.Delete("o.txt");
            Assert.IsTrue(local.Git.UpdateProject(UpdateStrategy.Merge).IsSuccess);
            Assert.AreEqual("o\n", local.Read("o.txt"));
        }

        [Test]
        public void Incoming_OnABranchWithoutCommits_RefusesStagedFilesThatDiffer()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = TestRepository.Create("local");
            local.RunGit("remote", "add", "origin", remote.Root);
            local.RunGit("config", "branch.main.remote", "origin");
            local.RunGit("config", "branch.main.merge", "refs/heads/main");
            local.Write("f.txt", "staged here\n");
            local.RunGit("add", "f.txt");

            var incoming = Incoming(local.Git);

            Assert.AreEqual("Update Project is not possible, it would overwrite these staged files:\n  f.txt\n" +
                            "Move them out of the project and unstage them, then try again", incoming.Refusals.Single());
            StringAssert.StartsWith("Merge of origin/main is not possible, it would overwrite these staged files",
                local.Git.UpdateProject(UpdateStrategy.Merge).Message);
        }

        [Test]
        public void Incoming_MarksAChangedFileThatGitWasToldToSkip()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.Write("f.txt", "theirs\n");
            upstream.Write("o.txt", "theirs\n");
            upstream.CommitAll("theirs");
            upstream.RunGit("push", "-q");
            local.RunGit("update-index", "--skip-worktree", "f.txt");
            local.Write("f.txt", "hidden change\n");
            local.RunGit("update-index", "--assume-unchanged", "o.txt");
            File.SetLastWriteTime(Path.Combine(local.Root, "o.txt"), System.DateTime.Now.AddMinutes(1));

            var incoming = Incoming(local.Git);

            Assert.AreEqual(IncomingMark.HiddenChange, Mark(incoming, "f.txt"));
            Assert.AreEqual(IncomingMark.HiddenChange, Mark(incoming, "o.txt"));
            StringAssert.StartsWith("These files are marked as unchanged in Git", incoming.Warnings.Single());
            Assert.IsFalse(local.Git.UpdateProject(UpdateStrategy.Merge).IsSuccess);
        }

        [Test]
        public void Incoming_MarksFilesInUse()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.Write("Assets/Plugins/native.dll", "v1\n");
            upstream.CommitAll("plugin");
            upstream.RunGit("push", "-q");
            using var local = remote.Clone("local");
            upstream.PushChange("Assets/Plugins/native.dll", "v2");
            upstream.PushChange("o.txt", "theirs");
            File.SetAttributes(Path.Combine(local.Root, "o.txt"), FileAttributes.ReadOnly);

            GitIncoming incoming;
            using (local.InUse("Assets/Plugins/native.dll"))
            {
                incoming = Incoming(local.Git);
            }

            Assert.AreEqual(IncomingMark.InUse, Mark(incoming, "Assets/Plugins/native.dll"));
            Assert.AreEqual(IncomingMark.None, Mark(incoming, "o.txt"));
            StringAssert.StartsWith("These files are in use by another program", incoming.Warnings.Single());
        }

        [Test]
        public void Incoming_WithAnOwnMergeDriver_DoesNotRunItAndSaysTheFileMayConflict()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.Write(".gitattributes", "*.yaml merge=scene\n");
            upstream.Write("s.yaml", "a: 1\nb: 2\nc: 3\nd: 4\ne: 5\n");
            upstream.CommitAll("scene");
            upstream.RunGit("push", "-q");
            using var local = remote.Clone("local");
            local.RunGit("config", "merge.scene.driver", "echo ran > driver-ran.txt; exit 1");
            upstream.PushChange("s.yaml", "a: 1\nb: THEIRS\nc: 3\nd: 4\ne: 5");
            local.Write("s.yaml", "a: 1\nb: 2\nc: 3\nd: 4\ne: MINE\n");
            local.CommitAll("mine");

            var incoming = Incoming(local.Git);

            Assert.AreEqual(IncomingMark.MayConflict, Mark(incoming, "s.yaml"));
            StringAssert.StartsWith("These files are changed on both sides and use their own merge driver",
                incoming.Warnings.Single());
            Assert.IsFalse(local.Exists("driver-ran.txt"));
        }

        [Test]
        public void Incoming_WithAnOwnMergeDriverAndAnUncommittedChange_SaysTheFileMayConflict()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.Write(".gitattributes", "*.yaml merge=scene\n");
            upstream.Write("s.yaml", "a: 1\nb: 2\nc: 3\nd: 4\ne: 5\n");
            upstream.CommitAll("scene");
            upstream.RunGit("push", "-q");
            using var local = remote.Clone("local");
            local.RunGit("config", "merge.scene.driver", "echo ran > driver-ran.txt; exit 1");
            local.RunGit("config", "merge.unity yaml.driver", "echo ran > other-ran.txt; exit 1");
            upstream.PushChange("s.yaml", "a: 1\nb: THEIRS\nc: 3\nd: 4\ne: 5");
            local.Write("s.yaml", "a: 1\nb: 2\nc: 3\nd: 4\ne: MINE\n");

            var incoming = Incoming(local.Git);

            Assert.AreEqual(IncomingMark.MayConflict, Mark(incoming, "s.yaml"));
            StringAssert.StartsWith("These files are changed on both sides and use their own merge driver",
                incoming.Warnings.Single());
            Assert.IsFalse(local.Exists("driver-ran.txt"));
        }

        [Test]
        public void Incoming_WithRebaseAndLocalMergeCommits_SaysUpdateProjectRefuses()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.PushChange("f.txt", "theirs");
            local.RunGit("switch", "-q", "-c", "side");
            local.Write("side.txt", "side\n");
            local.CommitAll("side");
            local.RunGit("switch", "-q", "main");
            local.Write("o.txt", "mine\n");
            local.CommitAll("mine");
            local.RunGit("merge", "-q", "--no-ff", "--no-edit", "side");

            var incoming = Incoming(local.Git, UpdateStrategyChoice.Rebase);

            Assert.AreEqual("Rebase onto origin/main is not possible, the local commits include merge commits that a " +
                            "rebase would flatten, use Merge instead", incoming.Refusals.Single());
        }

        [Test]
        public void Incoming_WhenGitAlwaysMakesMergeCommits_DoesNotPromiseAFastForward()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.PushChange("f.txt", "theirs");
            local.RunGit("config", "merge.ff", "No");

            Assert.AreEqual("Update Project will merge them into main", Incoming(local.Git).Action);
        }

        [Test]
        public void Incoming_WhenAFileOnlyChangesItsCase_DoesNotCallItInTheWay()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.RunGit("mv", "o.txt", "O.txt");
            upstream.CommitAll("case only");
            upstream.RunGit("push", "-q");

            var incoming = Incoming(local.Git);

            Assert.AreEqual(0, incoming.Refusals.Count, string.Join("\n", incoming.Refusals));
        }

        [Test]
        public void Incoming_WhenGitAllowsOnlyFastForwardMerges_SaysUpdateProjectFails()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.PushChange("f.txt", "theirs");
            local.Write("o.txt", "mine\n");
            local.CommitAll("mine");
            local.RunGit("config", "merge.ff", "only");

            var incoming = Incoming(local.Git);

            Assert.AreEqual("Update Project will fail: Git is set to allow only fast-forward merges here and main has " +
                            "its own commits", incoming.Refusals.Single());
        }

        [Test]
        public void Incoming_DuringARebase_SaysWhatUpdateProjectSays()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            local.RunGit("switch", "-q", "-c", "side");
            local.Write("f.txt", "side\n");
            local.CommitAll("side");
            local.RunGit("switch", "-q", "main");
            local.Write("f.txt", "main\n");
            local.CommitAll("main");
            Assert.AreNotEqual(0, local.TryRunGit("rebase", "side"));

            var incoming = local.Git.ReadIncoming(null, true, UpdateStrategyChoice.Merge, 100, 1000);
            var update = local.Git.UpdateProject(UpdateStrategy.Merge);

            Assert.AreEqual("Update Project is not possible while a rebase is in progress, finish or abort it first",
                incoming.Result.Message);
            Assert.AreEqual(incoming.Result.Message, update.Message);
            local.RunGit("rebase", "--abort");
        }

        [Test]
        public void RangeFileDiff_FollowsARename()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "1\n2\n3\n4\n5\n6\n");
            repo.CommitAll("a");
            var before = repo.Head;
            repo.RunGit("mv", "a.txt", "b.txt");
            repo.Write("b.txt", "1\n2\n3\n4\n5\nsix\n");
            repo.CommitAll("move");

            var diff = repo.Git.RangeFileDiff(before, repo.Head, "b.txt", "a.txt");

            StringAssert.Contains("rename from a.txt", diff.Output);
            StringAssert.Contains("+six", diff.Output);
        }
    }
}
