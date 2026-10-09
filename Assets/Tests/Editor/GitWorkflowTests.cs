using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Upwake.Vetka.Tests
{
    [TestFixture("")]
    [TestFixture("Unity Проект")]
    internal class GitWorkflowTests : ProjectFolderTests
    {
        public GitWorkflowTests(string projectFolder) : base(projectFolder)
        {
        }

        [Test]
        public void UndoLastCommit_KeepsItsChangesStaged()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("first");
            var first = repo.Head;
            repo.Write("a.txt", "a2\n");
            repo.CommitAll("second");

            var result = repo.Git.UndoLastCommit(repo.Head);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Last commit undone, its changes are staged", result.Message);
            Assert.AreEqual(first, repo.Head);
            Assert.AreEqual("a2\n", repo.Read("a.txt"));
            Assert.AreEqual("M  a.txt", repo.RunGit("status", "--porcelain"));
        }

        [Test]
        public void UndoAndAmend_OfACommitThatIsNoLongerTheLast_Refuse()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("seen in the log");
            var seen = repo.RunGit("rev-parse", "--short", "HEAD");
            repo.Write("a.txt", "a2\n");
            repo.CommitAll("made later");
            var before = repo.Snapshot();
            var git = repo.Git;

            var undo = git.UndoLastCommit(seen);
            var amend = git.Amend("new message", seen);

            Assert.AreEqual($"The last commit is not {seen} any more, refresh and try again", undo.Message);
            Assert.AreEqual($"The last commit is not {seen} any more, refresh and try again", amend.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual("made later", git.LastCommitMessage());
        }

        [Test]
        public void ProjectLocation_OutsideARepository_SaysWhy()
        {
            var directory = Path.Combine(Path.GetTempPath(), "VetkaTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var location = TestRepository.GitAt(directory).ProjectLocation();

                Assert.IsFalse(location.Result.IsSuccess);
                StringAssert.Contains("not a git repository", location.Result.Message);
                Assert.IsNull(location.Top);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void Amend_WithoutFiles_ChangesOnlyTheMessage()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("old message");
            repo.Write("a.txt", "staged\n");
            repo.RunGit("add", "a.txt");

            var result = repo.Git.Commit(null, "new message", amend: true);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Commit amended: new message", result.Message);
            Assert.AreEqual("new message", repo.Git.LastCommitMessage());
            Assert.AreEqual("1", repo.RunGit("rev-list", "--count", "HEAD"));
            Assert.AreEqual("a", repo.RunGit("show", "HEAD:a.txt"));
            Assert.AreEqual("M  a.txt", repo.RunGit("status", "--porcelain"));
        }

        [Test]
        public void Push_NewBranch_SetsUpstreamAndMarksCommitsAsPushed()
        {
            using var remote = TestRepository.CreateBare();
            using var local = PublishBase(remote);
            local.RunGit("switch", "-q", "-c", "feature");
            local.Write("feature.txt", "feature\n");
            local.CommitAll("feature commit");
            var git = local.Git;

            Assert.IsFalse(git.IsHeadPushed());
            Assert.AreEqual(1, git.OutgoingCommits().Count);

            var result = git.Push();

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Pushed 1 commit to new branch origin/feature", result.Message);
            Assert.AreEqual("origin/feature", git.UpstreamBranch());
            Assert.AreEqual(local.Head, remote.RunGit("rev-parse", "feature"));
            Assert.IsTrue(git.IsHeadPushed());
            Assert.AreEqual(0, git.OutgoingCommits().Count);
            Assert.IsTrue(git.Log().All(commit => commit.IsPushed));

            local.Write("feature.txt", "unpushed\n");
            local.CommitAll("unpushed commit");
            var log = git.Log();

            Assert.IsFalse(log[0].IsPushed);
            Assert.IsTrue(log.Skip(1).All(commit => commit.IsPushed));
            Assert.IsFalse(git.IsHeadPushed());

            local.Write("feature.txt", "second unpushed\n");
            local.CommitAll("second unpushed commit");

            Assert.AreEqual("Pushed 2 commits to origin/feature", git.Push().Message);
            Assert.AreEqual("Everything is up to date", git.Push().Message);
        }

        [Test]
        public void ReadLogPage_PagesFromTheHeadItStartedAt()
        {
            using var repo = TestRepository.Create();
            for (var i = 1; i <= 5; i++)
            {
                repo.Write("a.txt", $"{i}\n");
                repo.CommitAll($"commit {i}");
            }

            var git = repo.Git;
            var first = git.ReadLogPage(null, 0, 2);
            repo.Write("a.txt", "later\n");
            repo.CommitAll("made later");
            var second = git.ReadLogPage(first.Head, 2, 2);
            var third = git.ReadLogPage(first.Head, 4, 2);

            Assert.IsTrue(first.Result.IsSuccess, first.Result.Message);
            Assert.AreEqual(repo.RunGit("rev-parse", "HEAD~1"), first.Head);
            CollectionAssert.AreEqual(new[] { "commit 5", "commit 4" }, first.Commits.Select(commit => commit.Subject));
            CollectionAssert.AreEqual(new[] { "commit 3", "commit 2" }, second.Commits.Select(commit => commit.Subject));
            CollectionAssert.AreEqual(new[] { "commit 1" }, third.Commits.Select(commit => commit.Subject));
        }

        [Test]
        public void ReadLogPage_MarksUnpushedCommitsOnEveryPage()
        {
            using var remote = TestRepository.CreateBare();
            using var local = PublishBase(remote);
            for (var i = 1; i <= 3; i++)
            {
                local.Write("f.txt", $"{i}\n");
                local.CommitAll($"local {i}");
            }

            var git = local.Git;
            var first = git.ReadLogPage(null, 0, 2);
            var second = git.ReadLogPage(first.Head, 2, 2);

            CollectionAssert.AreEqual(new[] { false, false }, first.Commits.Select(commit => commit.IsPushed));
            CollectionAssert.AreEqual(new[] { "local 1", "base" }, second.Commits.Select(commit => commit.Subject));
            CollectionAssert.AreEqual(new[] { false, true }, second.Commits.Select(commit => commit.IsPushed));
        }

        [Test]
        public void FileHistory_FollowsMovesAndTakesMetaOnlyChangesOnlyWithTheMeta()
        {
            using var repo = TestRepository.Create();
            repo.Write("Assets/x [1].prefab", "a\n");
            repo.Write("Assets/x [1].prefab.meta", "guid: 1\n");
            repo.CommitAll("create");
            repo.Write("Assets/x [1].prefab.meta", "guid: 1\nimport: 2\n");
            repo.CommitAll("import settings");
            Directory.CreateDirectory(Path.Combine(repo.Root, "Assets", "Sub"));
            repo.RunGit("mv", "Assets/x [1].prefab", "Assets/Sub/x [1].prefab");
            repo.RunGit("mv", "Assets/x [1].prefab.meta", "Assets/Sub/x [1].prefab.meta");
            repo.RunGit("commit", "-q", "-m", "move");
            repo.Write("other.txt", "o\n");
            repo.CommitAll("other");
            repo.Write("Assets/Sub/x [1].prefab", "a\nb\n");
            repo.CommitAll("edit");
            var git = repo.Git;

            var withMeta = git.HistoryPaths("Assets/Sub/x [1].prefab", withMeta: true);
            var alone = git.HistoryPaths("Assets/Sub/x [1].prefab", withMeta: false);

            Assert.IsTrue(withMeta.Result.IsSuccess, withMeta.Result.Message);
            CollectionAssert.AreEquivalent(new[]
            {
                "Assets/Sub/x [1].prefab", "Assets/Sub/x [1].prefab.meta", "Assets/x [1].prefab", "Assets/x [1].prefab.meta"
            }, withMeta.Paths);
            CollectionAssert.AreEqual(new[] { "edit", "move", "import settings", "create" },
                git.ReadLogPage(null, 0, 10, withMeta.Paths).Commits.Select(commit => commit.Subject));
            CollectionAssert.AreEqual(new[] { "edit", "move", "create" },
                git.ReadLogPage(null, 0, 10, alone.Paths).Commits.Select(commit => commit.Subject));
        }

        [Test]
        public void FileHistory_MarksUnpushedCommits()
        {
            using var remote = TestRepository.CreateBare();
            using var local = PublishBase(remote);
            local.Write("f.txt", "local\n");
            local.CommitAll("local f");
            local.Write("g.txt", "g\n");
            local.CommitAll("other");
            var git = local.Git;

            var history = git.ReadLogPage(null, 0, 10, git.HistoryPaths("f.txt", withMeta: false).Paths);

            CollectionAssert.AreEqual(new[] { "local f", "base" }, history.Commits.Select(commit => commit.Subject));
            CollectionAssert.AreEqual(new[] { false, true }, history.Commits.Select(commit => commit.IsPushed));
        }

        [Test]
        public void ReadLogPage_BeforeTheFirstCommit_IsEmpty()
        {
            using var repo = TestRepository.Create();

            var page = repo.Git.ReadLogPage(null, 0, 10);

            Assert.IsTrue(page.Result.IsSuccess, page.Result.Message);
            Assert.AreEqual(0, page.Commits.Count);
            Assert.IsNull(page.Head);
        }

        [Test]
        public void Push_BranchCreatedFromARemoteBranch_GoesToItsOwnName()
        {
            using var remote = TestRepository.CreateBare();
            using var local = PublishBase(remote);
            var git = local.Git;
            Assert.IsTrue(git.CreateBranch("feat", "origin/main", checkout: true).IsSuccess);
            Assert.AreEqual("origin/main", git.UpstreamBranch());
            local.Write("feat.txt", "feat\n");
            local.CommitAll("feat commit");
            Assert.AreEqual(1, git.OutgoingCommits().Count);

            var result = git.Push();

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Pushed 1 commit to new branch origin/feat", result.Message);
            Assert.AreEqual(local.Head, remote.RunGit("rev-parse", "feat"));
            Assert.AreNotEqual(local.Head, remote.RunGit("rev-parse", "main"));
            Assert.AreEqual("origin/main", git.UpstreamBranch());
            Assert.AreEqual(0, git.OutgoingCommits().Count);
            Assert.IsTrue(git.IsHeadPushed());
            Assert.IsTrue(git.Log().All(commit => commit.IsPushed));

            local.Write("feat.txt", "next\n");
            var next = git.CommitAndPush(new[] { "feat.txt" }, "next", out _);

            Assert.AreEqual("1 file committed and pushed to origin/feat: next", next.Message);
            Assert.AreEqual(local.Head, remote.RunGit("rev-parse", "feat"));
        }

        [Test]
        public void Push_WithTheOnlyRemoteNotNamedOrigin_UsesIt()
        {
            using var remote = TestRepository.CreateBare();
            using var local = PublishBase(remote);
            local.RunGit("remote", "rename", "origin", "upstream");
            local.RunGit("switch", "-q", "-c", "feature");
            local.Write("feature.txt", "feature\n");
            local.CommitAll("feature commit");

            var result = local.Git.Push();

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Pushed 1 commit to new branch upstream/feature", result.Message);
            Assert.AreEqual("upstream/feature", local.Git.UpstreamBranch());
            Assert.AreEqual(local.Head, remote.RunGit("rev-parse", "feature"));
        }

        [Test]
        public void Push_FollowsRemotePushDefault()
        {
            using var remote = TestRepository.CreateBare();
            using var fork = TestRepository.CreateBare("fork.git");
            using var local = PublishBase(remote);
            local.RunGit("remote", "add", "fork", fork.Root);
            local.RunGit("config", "remote.pushDefault", "fork");
            var originMain = remote.RunGit("rev-parse", "main");
            local.Write("f.txt", "for the fork\n");
            local.CommitAll("fork commit");

            var result = local.Git.Push();

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Pushed 2 commits to new branch fork/main", result.Message);
            Assert.AreEqual(local.Head, fork.RunGit("rev-parse", "main"));
            Assert.AreEqual(originMain, remote.RunGit("rev-parse", "main"));
            Assert.AreEqual("origin/main", local.Git.UpstreamBranch());
        }

        [Test]
        public void Push_RemoteAndBranchWithDotsInTheirNames()
        {
            using var remote = TestRepository.CreateBare();
            using var local = PublishBase(remote);
            local.RunGit("remote", "rename", "origin", "my.fork");
            local.RunGit("switch", "-q", "-c", "feature/v1.2");
            local.Write("v.txt", "v\n");
            local.CommitAll("v");

            var result = local.Git.Push();

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Pushed 1 commit to new branch my.fork/feature/v1.2", result.Message);
            Assert.AreEqual(local.Head, remote.RunGit("rev-parse", "feature/v1.2"));
            Assert.AreEqual("Everything is up to date", local.Git.Push().Message);
        }

        [Test]
        public void Push_WithoutAClearRemote_Refuses()
        {
            using var lonely = TestRepository.Create();
            lonely.Write("f.txt", "f\n");
            lonely.CommitAll("base");

            Assert.AreEqual("This repository has no remote to push to", lonely.Git.Push().Message);

            using var first = TestRepository.CreateBare("first.git");
            using var second = TestRepository.CreateBare("second.git");
            lonely.RunGit("remote", "add", "first", first.Root);
            lonely.RunGit("remote", "add", "second", second.Root);

            Assert.AreEqual("Cannot choose a remote to push to, set remote.pushDefault", lonely.Git.Push().Message);
            Assert.AreEqual("", first.RunGit("for-each-ref"));
            Assert.AreEqual("", second.RunGit("for-each-ref"));
        }

        [Test]
        public void CheckoutRemote_CreatesATrackingBranch()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = PublishBase(remote);
            upstream.RunGit("switch", "-q", "-c", "feature");
            upstream.Write("feature.txt", "feature\n");
            upstream.CommitAll("feature");
            upstream.RunGit("push", "-q", "-u", "origin", "feature");
            using var local = remote.Clone("local");

            var result = local.Git.CheckoutRemote("origin/feature");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Checked out origin/feature as feature", result.Message);
            Assert.AreEqual("feature", local.CurrentBranch);
            Assert.AreEqual("origin/feature", local.Git.UpstreamBranch());
            Assert.IsTrue(local.Exists("feature.txt"));
        }

        [TestCase(UpdateStrategy.Merge)]
        [TestCase(UpdateStrategy.Rebase)]
        public void Integrate_BranchWithoutConflicts_BringsItsCommitsIn(UpdateStrategy strategy)
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "feature");
            repo.Write("g.txt", "feature\n");
            repo.CommitAll("feature");
            repo.RunGit("switch", "-q", "main");
            repo.Write("h.txt", "main\n");
            repo.CommitAll("main");
            repo.Write("f.txt", "local change\n");

            var result = repo.Git.Integrate(strategy, "feature");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(strategy == UpdateStrategy.Merge
                ? "Merged 1 commit from feature, local changes restored"
                : "Rebased onto feature: 1 new commit, local changes restored", result.Message);
            Assert.AreEqual("main", repo.CurrentBranch);
            Assert.IsTrue(repo.Exists("g.txt"));
            Assert.IsTrue(repo.Exists("h.txt"));
            Assert.AreEqual("local change\n", repo.Read("f.txt"));
            var parents = repo.RunGit("rev-list", "--parents", "-n", "1", "HEAD").Split(' ').Length - 1;
            Assert.AreEqual(strategy == UpdateStrategy.Merge ? 2 : 1, parents);
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [TestCase(UpdateStrategy.Merge)]
        [TestCase(UpdateStrategy.Rebase)]
        public void Integrate_ConflictingBranch_RollsBack(UpdateStrategy strategy)
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("o.txt", "o\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "feature");
            repo.Write("f.txt", "feature\n");
            repo.CommitAll("feature");
            repo.RunGit("switch", "-q", "main");
            repo.Write("f.txt", "main\n");
            repo.CommitAll("main");
            repo.Write("o.txt", "local change\n");
            var before = repo.Snapshot();

            var result = repo.Git.Integrate(strategy, "feature");

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual((strategy == UpdateStrategy.Merge ? "Merge of feature" : "Rebase onto feature") +
                            " failed, the project is unchanged\nConflicts:\n  f.txt", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void DeleteBranch_RefusesUnmergedBranchesUnlessForced()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("branch", "merged");
            repo.RunGit("switch", "-q", "-c", "unmerged");
            repo.Write("g.txt", "unmerged\n");
            repo.CommitAll("unmerged");
            repo.RunGit("switch", "-q", "main");
            var git = repo.Git;

            Assert.AreEqual("Branch merged deleted", git.DeleteBranch("merged", false).Message);
            Assert.IsFalse(git.DeleteBranch("unmerged", false).IsSuccess);
            Assert.IsTrue(git.Branches().Any(branch => branch.Name == "unmerged"));
            Assert.IsTrue(git.DeleteBranch("unmerged", true).IsSuccess);
            CollectionAssert.AreEqual(new[] { "main" }, git.Branches().Select(branch => branch.Name));
        }

        [Test]
        public void Stash_WithUntrackedFiles_AndDrop()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "changed\n");
            repo.Write("new.txt", "untracked\n");
            var git = repo.Git;

            Assert.AreEqual("Changes stashed: with untracked", git.Stash("with untracked", true).Message);
            Assert.IsFalse(repo.Exists("new.txt"));
            Assert.AreEqual("a\n", repo.Read("a.txt"));
            Assert.AreEqual(1, git.Stashes().Count);
            Assert.AreEqual("No local changes to stash", git.Stash("", true).Message);
            Assert.AreEqual(1, git.Stashes().Count);

            Assert.AreEqual("stash@{0} dropped", git.DropStash(git.Stashes()[0]).Message);
            Assert.AreEqual(0, git.Stashes().Count);
            Assert.IsFalse(repo.Exists("new.txt"));
        }

        [Test]
        public void Rollback_RestoresTrackedFilesAndKeepsAddedOnesAsUnversioned()
        {
            using var repo = TestRepository.Create();
            repo.Write("mod.txt", "m\n");
            repo.Write("staged.txt", "s\n");
            repo.Write("del.txt", "d\n");
            repo.Write("rm.txt", "r\n");
            repo.Write("other.txt", "o\n");
            repo.CommitAll("base");

            repo.Write("mod.txt", "m2\n");
            repo.Write("staged.txt", "s2\n");
            repo.RunGit("add", "staged.txt");
            repo.Write("staged.txt", "s3\n");
            repo.Delete("del.txt");
            repo.RunGit("rm", "-q", "rm.txt");
            repo.Write("added.txt", "a\n");
            repo.RunGit("add", "added.txt");
            repo.Write("added.txt", "a2\n");
            repo.Write("gone.txt", "g\n");
            repo.RunGit("add", "gone.txt");
            repo.Delete("gone.txt");
            repo.Write("other.txt", "o2\n");

            var result = repo.Git.Rollback(Files("mod.txt", "staged.txt", "del.txt", "rm.txt", "added.txt", "gone.txt"), false);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("6 files rolled back", result.Message);
            Assert.AreEqual("m\n", repo.Read("mod.txt"));
            Assert.AreEqual("s\n", repo.Read("staged.txt"));
            Assert.AreEqual("d\n", repo.Read("del.txt"));
            Assert.AreEqual("r\n", repo.Read("rm.txt"));
            Assert.AreEqual("a2\n", repo.Read("added.txt"));
            Assert.IsFalse(repo.Exists("gone.txt"));
            Assert.AreEqual("M\tother.txt", repo.RunGit("diff", "--name-status"));
            Assert.AreEqual("", repo.RunGit("diff", "--cached", "--name-status"));
            Assert.AreEqual("added.txt", repo.RunGit("ls-files", "--others", "--exclude-standard"));
        }

        [Test]
        public void Rollback_CountsAnAssetAndItsMetaAsSeparateFiles()
        {
            using var repo = TestRepository.Create();
            repo.Write("Assets/a.prefab", "a\n");
            repo.Write("Assets/a.prefab.meta", "guid: 1\n");
            repo.Write("Assets/b.txt.meta", "guid: 2\n");
            repo.CommitAll("base");
            repo.Write("Assets/a.prefab", "a2\n");
            repo.Write("Assets/a.prefab.meta", "guid: 3\n");
            repo.Write("Assets/b.txt.meta", "guid: 4\n");

            var result = repo.Git.Rollback(Files("Assets/a.prefab", "Assets/a.prefab.meta", "Assets/b.txt.meta"), false);

            Assert.AreEqual("3 files rolled back", result.Message);
            Assert.AreEqual("", repo.RunGit("status", "--porcelain"));
        }

        [Test]
        public void Rollback_WithDeleteAdded_RemovesAddedFilesFromDisk()
        {
            using var repo = TestRepository.Create();
            repo.Write("mod.txt", "m\n");
            repo.CommitAll("base");

            repo.Write("mod.txt", "m2\n");
            repo.Write("added.txt", "a\n");
            repo.RunGit("add", "added.txt");
            repo.Write("added.txt", "a2\n");
            repo.Write("new.txt", "n\n");
            repo.RunGit("add", "new.txt");

            var result = repo.Git.Rollback(Files("mod.txt", "added.txt", "new.txt"), deleteAdded: true);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("m\n", repo.Read("mod.txt"));
            Assert.IsFalse(repo.Exists("added.txt"));
            Assert.IsFalse(repo.Exists("new.txt"));
            Assert.AreEqual("", repo.RunGit("status", "--porcelain"));
        }

        [Test]
        public void LocalIdentity_IsReadAndWritten()
        {
            using var repo = TestRepository.Create();
            var git = repo.Git;

            var identity = git.Identity(global: false);
            Assert.AreEqual("Test", identity.Name);
            Assert.AreEqual("test@example.com", identity.Email);

            var result = git.SetIdentity(new GitIdentity("Иван Тест", "ivan@example.com"), global: false);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Иван Тест", repo.RunGit("config", "--local", "user.name"));
            Assert.AreEqual("ivan@example.com", repo.RunGit("config", "--local", "user.email"));
            Assert.IsFalse(git.SetIdentity(new GitIdentity("Name", ""), global: false).IsSuccess);
        }

        [Test]
        public void Identity_ReadsIncludedConfigFiles()
        {
            using var repo = TestRepository.Create();
            repo.RunGit("config", "--unset", "user.name");
            repo.RunGit("config", "--unset", "user.email");
            repo.Write(".git/identity.gitconfig", "[user]\n\tname = Included Name\n\temail = included@example.com\n");
            repo.RunGit("config", "include.path", "identity.gitconfig");

            var identity = repo.Git.Identity(global: false);

            Assert.AreEqual("Included Name", identity.Name);
            Assert.AreEqual("included@example.com", identity.Email);
        }

        [Test]
        public void Identities_KeepLocalAndGlobalValuesApart()
        {
            using var repo = TestRepository.Create();
            repo.RunGit("config", "--unset", "user.name");
            repo.RunGit("config", "--unset", "user.email");
            var none = repo.Git.Identities();
            repo.RunGit("config", "--add", "user.name", "First Name");
            repo.RunGit("config", "--add", "user.name", "Last Name");
            File.WriteAllText(repo.GlobalConfig, "[user]\n\tname = Global Name\n\temail = global@example.com\n");

            var (local, global) = repo.Git.Identities();

            Assert.AreEqual(("", "", "", ""), (none.Local.Name, none.Local.Email, none.Global.Name, none.Global.Email));
            Assert.AreEqual("Last Name", local.Name);
            Assert.AreEqual("", local.Email);
            Assert.AreEqual("Global Name", global.Name);
            Assert.AreEqual("global@example.com", global.Email);
        }

        [Test]
        public void CommitAndPush_WhenThePushIsRejected_SaysTheCommitWasMade()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = PublishBase(remote);
            using var local = remote.Clone("local");
            upstream.Write("f.txt", "theirs\n");
            upstream.CommitAll("theirs");
            upstream.RunGit("push", "-q");
            local.Write("g.txt", "mine\n");

            var result = local.Git.CommitAndPush(new[] { "g.txt" }, "mine", out var committed);

            Assert.IsTrue(committed);
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("1 file committed: mine, but the push failed\n" +
                            "Push rejected: the remote has new commits, update the project first", result.Message);
            Assert.AreEqual("mine", local.RunGit("log", "-1", "--format=%s"));
        }

        [Test]
        public void CommitFileDiff_ShowsOnlyTheRequestedFile()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("first");
            repo.Write("a.txt", "b\n");
            repo.Write("b.txt", "other\n");
            repo.CommitAll("second");

            var diff = repo.Git.CommitFileDiff(repo.Head, "a.txt");

            Assert.IsTrue(diff.IsSuccess, diff.Message);
            StringAssert.Contains("-a", diff.Output);
            StringAssert.Contains("+b", diff.Output);
            StringAssert.DoesNotContain("b.txt", diff.Output);
        }

        [Test]
        public void HasUncommittedChanges_IgnoresUntrackedFiles()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            var git = repo.Git;

            Assert.IsFalse(git.HasUncommittedChanges());
            repo.Write("new.txt", "untracked\n");
            Assert.IsFalse(git.HasUncommittedChanges());
            repo.Write("a.txt", "changed\n");
            Assert.IsTrue(git.HasUncommittedChanges());
        }

        [Test]
        public void Commands_IgnoreARepositoryInheritedFromTheEditorEnvironment()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "changed\n");
            var git = repo.GitWithVariable("GIT_DIR", Path.Combine(repo.BaseDirectory, "elsewhere.git"));

            Assert.AreEqual(true, git.HasUncommittedChanges());
        }

        [Test]
        public void GitMessages_AreReadInEnglishWhateverLanguageTheUserHas()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            var korean = new Dictionary<string, string>
            {
                ["GIT_TEXTDOMAINDIR"] = TranslatedGitMessages(repo.BaseDirectory),
                ["LANG"] = "ko_KR.UTF-8",
                ["LC_ALL"] = "ko_KR.UTF-8",
                ["LANGUAGE"] = "ko"
            };
            Assume.That(repo.RunGitWith(korean, "branch", "-d", "missing").error, Does.StartWith("ERROR-KO: "),
                "git is built without translations");

            var result = repo.GitWithVariables(korean).DeleteBranch("missing", force: false);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("branch 'missing' not found", result.Message);
        }

        [Test]
        public void TestRepositories_DoNotReadTheUsersGitConfig()
        {
            using var repo = TestRepository.Create();

            var scopes = repo.RunGit("config", "--list", "--show-scope");

            StringAssert.DoesNotContain("system", scopes);
            StringAssert.DoesNotContain("global", scopes);
        }

        [Test]
        public void GlobalIdentity_IsReadAndWritten()
        {
            using var repo = TestRepository.Create();
            var git = repo.Git;

            var before = git.Identity(global: true);
            var result = git.SetIdentity(new GitIdentity("Global Name", "global@example.com"), global: true);

            Assert.AreEqual("", before.Name);
            Assert.AreEqual("", before.Email);
            Assert.IsTrue(result.IsSuccess, result.Message);
            StringAssert.Contains("global@example.com", File.ReadAllText(repo.GlobalConfig));
            Assert.AreEqual("Global Name", git.Identity(global: true).Name);
            Assert.AreEqual("Test", git.Identity(global: false).Name);
        }

        [Test]
        public void Add_StagesUntrackedFilesByLiteralPath()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.Write("new [1].txt", "n\n");
            repo.Write("new1.txt", "must stay untracked\n");

            var result = repo.Git.Add(new[] { "new [1].txt" });

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("new [1].txt", repo.RunGit("diff", "--cached", "--name-only"));
            Assert.AreEqual("new1.txt", repo.RunGit("ls-files", "--others", "--exclude-standard"));
        }

        [Test]
        public void CommitAndPush_ReportsBothInOneLine()
        {
            using var remote = TestRepository.CreateBare();
            using var local = PublishBase(remote);
            local.Write("f.txt", "changed\n");

            var result = local.Git.CommitAndPush(new[] { "f.txt" }, "change f\n\ndetails", out var committed);

            Assert.IsTrue(committed);
            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("1 file committed and pushed to origin/main: change f", result.Message);
            Assert.AreEqual(local.Head, remote.RunGit("rev-parse", "main"));
        }

        [Test]
        public void Push_BehindTheRemote_IsRejectedWithAShortMessage()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = PublishBase(remote);
            using var local = remote.Clone("local");
            upstream.Write("f.txt", "theirs\n");
            upstream.CommitAll("theirs");
            upstream.RunGit("push", "-q");
            local.Write("g.txt", "mine\n");
            local.CommitAll("mine");

            var result = local.Git.Push();

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Push rejected: the remote has new commits, update the project first", result.Message);
        }

        [Test]
        public void Cancel_StopsAHangingReadWithItsChildProcesses()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            var hanging = new HangingCommand(repo);
            repo.RunGit("config", "core.fsmonitor", hanging.Command);
            var git = repo.Git;

            var (changes, elapsed) = CancelLater(hanging,
                () => OperationContext.Interruptible(() => git.WorkingTreeChanges(), out _));

            Assert.Less(elapsed.TotalSeconds, 30);
            Assert.IsTrue(changes.Result.IsCancelled, changes.Result.Message);
            Assert.AreEqual(0, changes.Changes.Count);
        }

        [Test]
        public void ReadingTimeout_StopsAHangingRead()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "changed\n");
            var hanging = new HangingCommand(repo);
            repo.RunGit("config", "core.fsmonitor", hanging.Command);
            var previous = Git.ReadingTimeout;
            Git.ReadingTimeout = TimeSpan.FromSeconds(1);
            try
            {
                var watch = Stopwatch.StartNew();

                Assert.IsNull(repo.Git.HasUncommittedChanges());

                Assert.Less(watch.Elapsed.TotalSeconds, 15);
                Assert.IsTrue(hanging.Started, "git never started the hanging command");
                AssertStops(hanging);
            }
            finally
            {
                Git.ReadingTimeout = previous;
            }
        }

        [Test]
        public void UpdateProject_FromASilentRemote_StopsAfterTheSilenceTimeout()
        {
            using var repo = CreateRepositoryWithSilentRemote();
            var before = repo.Snapshot();
            var previous = Git.NetworkSilenceTimeout;
            Git.NetworkSilenceTimeout = TimeSpan.FromSeconds(1);
            try
            {
                var watch = Stopwatch.StartNew();

                var result = repo.Git.UpdateProject(UpdateStrategy.Merge);

                Assert.Less(watch.Elapsed.TotalSeconds, 15);
                Assert.IsFalse(result.IsSuccess);
                StringAssert.Contains("stopped: no response for 1 s", result.Message);
                Assert.AreEqual(before, repo.Snapshot());
            }
            finally
            {
                Git.NetworkSilenceTimeout = previous;
            }
        }

        [Test]
        public void UpdateProject_CancelledWhileFetching_ChangesNothing()
        {
            using var repo = CreateRepositoryWithSilentRemote();
            repo.Write("f.txt", "local change\n");
            var before = repo.Snapshot();
            var git = repo.Git;

            var (result, elapsed) = CancelLater(new HangingCommand(repo), () => git.UpdateProject(UpdateStrategy.Merge));

            Assert.Less(elapsed.TotalSeconds, 30);
            Assert.IsTrue(result.IsCancelled, result.Message);
            Assert.AreEqual("Update cancelled", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, git.Stashes().Count);
        }

        [Test]
        public void CommitAndPush_CancelledWhilePushing_KeepsTheCommit()
        {
            using var repo = CreateRepositoryWithSilentRemote();
            repo.Write("f.txt", "committed\n");
            var git = repo.Git;
            var committed = false;

            var (result, elapsed) = CancelLater(new HangingCommand(repo),
                () => git.CommitAndPush(new[] { "f.txt" }, "change", out committed));

            Assert.Less(elapsed.TotalSeconds, 30);
            Assert.IsTrue(committed);
            Assert.IsTrue(result.IsCancelled, result.Message);
            Assert.AreEqual("1 file committed: change, push cancelled", result.Message);
            Assert.AreEqual("change", repo.RunGit("log", "-1", "--format=%s"));
        }

        [Test]
        public void Integrate_WithLocalChanges_LeavesNoInterruptionMarker()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "feature");
            repo.Write("g.txt", "feature\n");
            repo.CommitAll("feature");
            repo.RunGit("switch", "-q", "main");
            repo.Write("f.txt", "local change\n");

            var result = repo.Git.Integrate(UpdateStrategy.Merge, "feature");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.IsFalse(repo.Exists(".git/vetka-interrupted"));
            Assert.IsNull(repo.Git.InterruptedOperation());
        }

        [Test]
        public void InterruptedOperation_PointsToTheStashItLeftBehind()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.Write("f.txt", "local change\n");
            repo.RunGit("stash", "push", "-q", "-m", "Uncommitted changes before Merge of feature");
            repo.Write(".git/vetka-interrupted",
                $"Merge of feature\n{repo.RunGit("rev-parse", "stash@{0}")}\n2026-10-02 18:00\n");

            var interruption = repo.Git.InterruptedOperation();

            Assert.IsTrue(interruption.HasValue);
            Assert.AreEqual("Merge of feature", interruption.Value.Description);
            Assert.AreEqual("2026-10-02 18:00", interruption.Value.Time);
            Assert.AreEqual("stash@{0}", interruption.Value.Stash);
            StringAssert.EndsWith("Uncommitted changes before Merge of feature", interruption.Value.StashMessage);
            Assert.IsNull(interruption.Value.InProgress);
            Assert.IsTrue(repo.Exists(".git/vetka-interrupted"));
            Assert.AreEqual(Path.GetFullPath(Path.Combine(repo.Root, ".git", "vetka-interrupted")),
                interruption.Value.MarkerPath);
        }

        [Test]
        public void SetIdentity_WritesOnlyTheValuesThatChanged()
        {
            using var repo = TestRepository.Create();
            repo.RunGit("config", "--unset", "user.name");
            repo.RunGit("config", "--unset", "user.email");
            var git = repo.Git;

            var same = git.SetIdentity(new GitIdentity("A", "a@example.com"), false,
                new GitIdentity("A", "a@example.com"));
            var changed = git.SetIdentity(new GitIdentity("A", "b@example.com"), false,
                new GitIdentity("A", "a@example.com"));

            Assert.IsTrue(same.IsSuccess, same.Message);
            Assert.IsTrue(changed.IsSuccess, changed.Message);
            Assert.AreNotEqual(0, repo.TryRunGit("config", "--local", "--get", "user.name"));
            Assert.AreEqual("b@example.com", repo.RunGit("config", "--local", "--get", "user.email"));
        }

        [Test]
        public void InterruptedOperation_FindsAStashByItsMessageWhenThePushWasCut()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.Write("f.txt", "local change\n");
            const string message = "Uncommitted changes before Merge of feature at 2026-10-04 10:00:00";
            repo.RunGit("stash", "push", "-q", "-m", message);
            repo.Write(".git/vetka-interrupted", $"Merge of feature\n\n2026-10-04 10:00\n{message}\n");

            var interruption = repo.Git.InterruptedOperation();

            Assert.IsTrue(interruption.HasValue);
            Assert.AreEqual("stash@{0}", interruption.Value.Stash);
        }

        [Test]
        public void Integrate_WithLocalChanges_PointsToItsStashWhileItRuns()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "feature");
            repo.Write("g.txt", "feature\n");
            repo.CommitAll("feature");
            repo.RunGit("switch", "-q", "main");
            repo.Write("f.txt", "local change\n");
            var git = repo.Git;
            string marker = null;
            string stash = null;
            git.BeforeCommand = arguments =>
            {
                if (arguments[0] == "merge")
                {
                    marker = repo.Read(".git/vetka-interrupted");
                    stash = repo.RunGit("rev-parse", "refs/stash");
                }
            };

            var result = git.Integrate(UpdateStrategy.Merge, "feature");

            Assert.IsTrue(result.IsSuccess, result.Message);
            var lines = marker.Split('\n');
            Assert.AreEqual("Merge of feature", lines[0]);
            Assert.AreEqual(stash, lines[1]);
            StringAssert.StartsWith("Uncommitted changes before Merge of feature at ", lines[3]);
            Assert.IsFalse(repo.Exists(".git/vetka-interrupted"));
        }

        [Test]
        public void Integrate_WithLocalChanges_WritesTheMarkerBeforeStashing()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "feature");
            repo.Write("g.txt", "feature\n");
            repo.CommitAll("feature");
            repo.RunGit("switch", "-q", "main");
            repo.Write("f.txt", "local change\n");
            var git = repo.Git;
            string marker = null;
            git.BeforeCommand = arguments =>
            {
                if (arguments.Count > 1 && arguments[0] == "stash" && arguments[1] == "push")
                {
                    marker = repo.Exists(".git/vetka-interrupted") ? repo.Read(".git/vetka-interrupted") : "";
                }
            };

            var result = git.Integrate(UpdateStrategy.Merge, "feature");

            Assert.IsTrue(result.IsSuccess, result.Message);
            var lines = marker.Split('\n');
            Assert.AreEqual("Merge of feature", lines[0]);
            Assert.AreEqual("", lines[1]);
            StringAssert.StartsWith("Uncommitted changes before Merge of feature at ", lines[3]);
        }

        [Test]
        public void InterruptedOperation_IgnoresAStashThatIsAlreadyGone()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.Write(".git/vetka-interrupted", $"Merge of feature\n{repo.Head}\n2026-10-02 18:00\n");

            Assert.IsNull(repo.Git.InterruptedOperation());
            Assert.IsFalse(repo.Exists(".git/vetka-interrupted"));
        }

        [Test]
        public void InterruptedOperation_KeepsAFreshMarkerWhileItsStashMayStillBeWritten()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.Write(".git/vetka-interrupted",
                "Merge of feature\n\n2026-10-04 10:00\nUncommitted changes before Merge of feature at 2026-10-04 10:00:00\n");

            Assert.IsNull(repo.Git.InterruptedOperation());
            Assert.IsTrue(repo.Git.InterruptionPending());

            File.SetLastWriteTimeUtc(Path.Combine(repo.Root, ".git", "vetka-interrupted"), DateTime.UtcNow.AddHours(-1));

            Assert.IsNull(repo.Git.InterruptedOperation());
            Assert.IsFalse(repo.Git.InterruptionPending());
        }

        private static TestRepository CreateRepositoryWithSilentRemote()
        {
            var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("remote", "add", "origin", "ssh://localhost/silent.git");
            repo.RunGit("update-ref", "refs/remotes/origin/main", "HEAD");
            repo.RunGit("branch", "-q", "--set-upstream-to=origin/main");
            repo.RunGit("config", "core.sshCommand", new HangingCommand(repo).Command);
            return repo;
        }

        private sealed class HangingCommand
        {
            private readonly string _output;

            public HangingCommand(TestRepository repo)
            {
                _output = Path.Combine(repo.BaseDirectory, "hanging-command.out");
            }

            public string Command => $"sleep 60 > '{_output.Replace('\\', '/').Replace("'", "'\\''")}'; :";

            public bool Started => File.Exists(_output);

            public bool Running
            {
                get
                {
                    if (!Started)
                    {
                        return false;
                    }

                    try
                    {
                        using (File.Open(_output, FileMode.Open, FileAccess.Read, FileShare.None))
                        {
                        }

                        return false;
                    }
                    catch (IOException)
                    {
                        return true;
                    }
                }
            }
        }

        private static (T Result, TimeSpan Elapsed) CancelLater<T>(HangingCommand hanging, Func<T> run)
        {
            var context = new OperationContext(null);
            var watch = Stopwatch.StartNew();
            var task = Task.Run(() => OperationContext.With(context, run));

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!hanging.Running && !task.IsCompleted && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }

            Assert.IsTrue(hanging.Running, "git never started the hanging command");
            Assert.IsTrue(context.TryCancel(), "The hanging command is not interruptible");
            Assert.IsTrue(task.Wait(TimeSpan.FromSeconds(30)), "The operation did not stop after Cancel");
            AssertStops(hanging);
            return (task.Result, watch.Elapsed);
        }

        private static string TranslatedGitMessages(string directory)
        {
            var messages = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                [""] = "Content-Type: text/plain; charset=UTF-8\n",
                ["error: "] = "ERROR-KO: ",
                ["fatal: "] = "FATAL-KO: "
            };
            var strings = messages.Keys.Concat(messages.Values).Select(Encoding.UTF8.GetBytes).ToList();
            var count = messages.Count;
            var catalogs = Path.Combine(directory, "locale");
            var folder = Path.Combine(catalogs, "ko", "LC_MESSAGES");
            Directory.CreateDirectory(folder);
            using var writer = new BinaryWriter(File.Create(Path.Combine(folder, "git.mo")));
            foreach (var value in new uint[] { 0x950412de, 0, (uint)count, 28, (uint)(28 + 8 * count), 0, 0 })
            {
                writer.Write(value);
            }

            var position = 28 + 16 * count;
            foreach (var value in strings)
            {
                writer.Write((uint)value.Length);
                writer.Write((uint)position);
                position += value.Length + 1;
            }

            foreach (var value in strings)
            {
                writer.Write(value);
                writer.Write((byte)0);
            }

            return catalogs;
        }

        private static void AssertStops(HangingCommand hanging)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (hanging.Running && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }

            Assert.IsFalse(hanging.Running, "A process started by git is still running");
        }

        private static IEnumerable<GitFileChange> Files(params string[] paths) =>
            paths.Select(path => new GitFileChange(GitStatus.Unknown, path));

        private static TestRepository PublishBase(TestRepository remote)
        {
            var upstream = remote.Clone("upstream");
            upstream.Write("f.txt", "base\n");
            upstream.CommitAll("base");
            upstream.RunGit("push", "-q", "-u", "origin", "main");
            return upstream;
        }
    }
}
