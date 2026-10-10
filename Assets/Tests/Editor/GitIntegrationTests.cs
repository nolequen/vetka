using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Upwake.Vetka.Tests
{
    [TestFixture("")]
    [TestFixture("Unity Проект")]
    internal class GitIntegrationTests : ProjectFolderTests
    {
        public GitIntegrationTests(string projectFolder) : base(projectFolder)
        {
        }

        [Test]
        public void WorkingTreeChanges_ListsModifiedDeletedAndUntrackedFiles()
        {
            using var repo = TestRepository.Create();
            repo.Write("Assets/modified.txt", "a\n");
            repo.Write("Assets/deleted.txt", "d\n");
            repo.CommitAll("base");

            repo.Write("Assets/modified.txt", "b\n");
            repo.Delete("Assets/deleted.txt");
            repo.Write("Assets/Новая папка/new [1].txt", "n\n");

            var changes = repo.Git.WorkingTreeChanges().Changes;

            CollectionAssert.AreEqual(new[]
            {
                "Deleted Assets/deleted.txt",
                "Modified Assets/modified.txt",
                "Untracked Assets/Новая папка/new [1].txt"
            }, changes.Select(Describe));
        }

        [Test]
        public void WorkingTreeChanges_LeavesNestedRepositoriesOut()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.RunGit("init", "-q", "Packages/com.studio.tool");
            repo.Write("Packages/com.studio.tool/package.json", "{}\n");
            repo.Write("Assets/new.txt", "new\n");

            var (result, changes) = repo.Git.WorkingTreeChanges();

            Assert.IsTrue(result.IsSuccess, result.Message);
            CollectionAssert.AreEqual(new[] { "Untracked Assets/new.txt" }, changes.Select(Describe));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Commit_OfAFileRemovedFromGitButKeptOnDisk_CommitsTheRemoval(bool ignored)
        {
            using var repo = TestRepository.Create();
            repo.Write("Library/cache.bin", "cache\n");
            repo.Write("a.txt", "a\n");
            repo.Write("b.txt", "b\n");
            repo.Write("d.txt", "d\n");
            repo.CommitAll("base");
            if (ignored)
            {
                repo.Write(".gitignore", "Library/\n");
            }

            repo.RunGit("rm", "-q", "--cached", "Library/cache.bin");
            repo.Write("a.txt", "a changed\n");
            repo.Write("b.txt", "b staged\n");
            repo.RunGit("add", "b.txt");
            repo.Delete("d.txt");
            repo.Write("new.txt", "new\n");

            var result = repo.Git.Commit(new[] { "Library/cache.bin", "a.txt", "d.txt", "new.txt" },
                "Stop tracking the cache");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("4 files committed: Stop tracking the cache", result.Message);
            CollectionAssert.AreEquivalent(new[] { "a.txt", "b.txt", "new.txt" },
                repo.RunGit("ls-tree", "-r", "--name-only", "HEAD").Split('\n'));
            Assert.AreEqual("a changed", repo.RunGit("show", "HEAD:a.txt"));
            Assert.AreEqual("b", repo.RunGit("show", "HEAD:b.txt"));
            Assert.AreEqual("cache\n", repo.Read("Library/cache.bin"));
            Assert.AreEqual(ignored ? "M  b.txt\n?? .gitignore" : "M  b.txt\n?? Library/cache.bin",
                repo.RunGit("status", "--porcelain=v1", "--untracked-files=all"));
        }

        [Test]
        public void Rollback_OfAFileRemovedFromGitButKeptOnDisk_TracksItAgain()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "a local\n");
            repo.RunGit("rm", "-q", "--cached", "a.txt");

            var result = repo.Git.Rollback(new[] { new GitFileChange(GitStatus.Deleted, "a.txt") }, false);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("", repo.RunGit("status", "--porcelain=v1", "--untracked-files=all"));
            Assert.AreEqual("a\n", repo.Read("a.txt"));
        }

        private const string MetaTemplate =
            "fileFormatVersion: 2\nguid: {0}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n" +
            "  defaultReferences: []\n  executionOrder: 0\n  icon: {{instanceID: 0}}\n  userData: \n" +
            "  assetBundleName: \n  assetBundleVariant: \n";

        private static string Lines(string prefix, int count) =>
            string.Concat(Enumerable.Range(1, count).Select(i => $"{prefix} line {i}\n"));

        private static TestRepository CreateMovedProject()
        {
            var repo = TestRepository.Create();
            repo.Write("Assets/Old/a.cs", Lines("moved", 8));
            repo.Write("Assets/Old/a.cs.meta", string.Format(MetaTemplate, "0123456789abcdef0123456789abcdef"));
            repo.Write("Assets/Old/b.cs", Lines("edited", 6));
            repo.Write("Assets/staged.txt", "s\n");
            repo.CommitAll("base");

            repo.Write("Assets/New/a.cs", repo.Read("Assets/Old/a.cs"));
            repo.Write("Assets/New/a.cs.meta", repo.Read("Assets/Old/a.cs.meta"));
            repo.Write("Assets/New/b2.cs", repo.Read("Assets/Old/b.cs") + "one more\n");
            repo.Delete("Assets/Old/a.cs");
            repo.Delete("Assets/Old/a.cs.meta");
            repo.Delete("Assets/Old/b.cs");
            repo.RunGit("mv", "Assets/staged.txt", "Assets/staged2.txt");
            return repo;
        }

        private static string Describe(GitFileChange change) =>
            change.OldPath == null ? $"{change.Status} {change.Path}" : $"{change.Status} {change.OldPath} -> {change.Path}";

        [Test]
        public void WorkingTreeChanges_ShowsMovesAsOneEntryAndLeavesTheIndexAlone()
        {
            using var repo = CreateMovedProject();
            var before = repo.Snapshot();

            var changes = repo.Git.WorkingTreeChanges().Changes;

            CollectionAssert.AreEqual(new[]
            {
                "Renamed Assets/Old/a.cs -> Assets/New/a.cs",
                "Renamed Assets/Old/a.cs.meta -> Assets/New/a.cs.meta",
                "Untracked Assets/New/b2.cs",
                "Deleted Assets/Old/b.cs",
                "Renamed Assets/staged.txt -> Assets/staged2.txt"
            }, changes.Select(Describe));
            Assert.AreEqual(before, repo.Snapshot());
        }

        [Test]
        public void WorkingTreeChanges_ShowsMovesEvenWhenStatusRenamesAreOff()
        {
            using var repo = CreateMovedProject();
            repo.RunGit("config", "status.renames", "false");

            var changes = repo.Git.WorkingTreeChanges().Changes;

            CollectionAssert.AreEqual(new[]
            {
                "Renamed Assets/Old/a.cs -> Assets/New/a.cs",
                "Renamed Assets/Old/a.cs.meta -> Assets/New/a.cs.meta",
                "Untracked Assets/New/b2.cs",
                "Deleted Assets/Old/b.cs",
                "Renamed Assets/staged.txt -> Assets/staged2.txt"
            }, changes.Select(Describe));
        }

        private static string Script(string name) =>
            $"using UnityEngine;\n\npublic class {name} : MonoBehaviour\n{{\n    void Start()\n    {{\n    }}\n\n" +
            "    void Update()\n    {\n    }\n}\n";

        [Test]
        public void WorkingTreeChanges_DoesNotPairTwoScriptsMadeFromOneTemplate()
        {
            using var repo = TestRepository.Create();
            repo.Write("Assets/Scripts/PlayerOld.cs", Script("PlayerOld"));
            repo.Write("Assets/Scripts/PlayerOld.cs.meta", string.Format(MetaTemplate, "11111111111111111111111111111111"));
            repo.CommitAll("base");
            repo.Delete("Assets/Scripts/PlayerOld.cs");
            repo.Delete("Assets/Scripts/PlayerOld.cs.meta");
            repo.Write("Assets/Scripts/Enemy.cs", Script("Enemy"));
            repo.Write("Assets/Scripts/Enemy.cs.meta", string.Format(MetaTemplate, "22222222222222222222222222222222"));

            var changes = repo.Git.WorkingTreeChanges().Changes;

            CollectionAssert.AreEqual(new[]
            {
                "Untracked Assets/Scripts/Enemy.cs",
                "Untracked Assets/Scripts/Enemy.cs.meta",
                "Deleted Assets/Scripts/PlayerOld.cs",
                "Deleted Assets/Scripts/PlayerOld.cs.meta"
            }, changes.Select(Describe));
        }

        [Test]
        public void WorkingTreeChanges_PairsAMovedAndEditedScriptByItsMeta()
        {
            using var repo = TestRepository.Create();
            repo.Write("Assets/Old/Mover.cs", Script("Mover"));
            repo.Write("Assets/Old/Mover.cs.meta", string.Format(MetaTemplate, "33333333333333333333333333333333"));
            repo.CommitAll("base");
            repo.Write("Assets/New/Mover.cs", Script("Mover") + "\npublic class Extra\n{\n}\n");
            repo.Write("Assets/New/Mover.cs.meta", repo.Read("Assets/Old/Mover.cs.meta"));
            repo.Delete("Assets/Old/Mover.cs");
            repo.Delete("Assets/Old/Mover.cs.meta");

            var changes = repo.Git.WorkingTreeChanges().Changes;

            CollectionAssert.AreEqual(new[]
            {
                "Renamed Assets/Old/Mover.cs -> Assets/New/Mover.cs",
                "Renamed Assets/Old/Mover.cs.meta -> Assets/New/Mover.cs.meta"
            }, changes.Select(Describe));
        }

        [Test]
        public void WorkingTreeChanges_ShowsAStagedRenameOfASimilarFileAsTwoChanges()
        {
            using var repo = TestRepository.Create();
            repo.Write("notes.txt", Lines("note", 10));
            repo.CommitAll("base");
            repo.RunGit("mv", "notes.txt", "docs.txt");
            repo.Write("docs.txt", Lines("note", 10) + "one more\n");

            var changes = repo.Git.WorkingTreeChanges().Changes;

            CollectionAssert.AreEqual(new[] { "Added docs.txt", "Deleted notes.txt" }, changes.Select(Describe));
        }

        [Test]
        public void WorkingTreeChanges_PairsMovesSplitBetweenTheIndexAndTheWorkingTree()
        {
            using var repo = TestRepository.Create();
            repo.Write("old1.txt", Lines("first", 6));
            repo.Write("old2.txt", Lines("second", 6));
            repo.CommitAll("base");
            repo.Write("new1.txt", repo.Read("old1.txt"));
            repo.RunGit("add", "new1.txt");
            repo.Delete("old1.txt");
            repo.Write("new2.txt", repo.Read("old2.txt"));
            repo.RunGit("rm", "-q", "old2.txt");
            var before = repo.Snapshot();

            var changes = repo.Git.WorkingTreeChanges().Changes;

            CollectionAssert.AreEqual(new[]
            {
                "Renamed old1.txt -> new1.txt",
                "Renamed old2.txt -> new2.txt"
            }, changes.Select(Describe));
            Assert.AreEqual(before, repo.Snapshot());
        }

        [Test]
        public void WorkingTreeChanges_DoesNotPairUnrelatedMetaFiles()
        {
            using var repo = TestRepository.Create();
            repo.Write("Assets/A.cs", Lines("first asset", 6));
            repo.Write("Assets/A.cs.meta", string.Format(MetaTemplate, "11111111111111111111111111111111"));
            repo.CommitAll("base");
            repo.Delete("Assets/A.cs");
            repo.Delete("Assets/A.cs.meta");
            repo.Write("Assets/Other/B.cs", "completely different\n");
            repo.Write("Assets/Other/B.cs.meta", string.Format(MetaTemplate, "22222222222222222222222222222222"));

            var changes = repo.Git.WorkingTreeChanges().Changes;

            CollectionAssert.AreEqual(new[]
            {
                "Deleted Assets/A.cs",
                "Deleted Assets/A.cs.meta",
                "Untracked Assets/Other/B.cs",
                "Untracked Assets/Other/B.cs.meta"
            }, changes.Select(Describe));
        }

        [Test]
        public void Commit_OfAnUnstagedMove_RecordsARename()
        {
            using var repo = CreateMovedProject();

            var result = repo.Git.Commit(new[] { "Assets/New/a.cs", "Assets/Old/a.cs" }, "move");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("1 file committed: move", result.Message);
            Assert.AreEqual("R100\tAssets/Old/a.cs\tAssets/New/a.cs",
                repo.RunGit("show", "--name-status", "--format=", "-M", "HEAD"));
        }

        [Test]
        public void Rollback_OfAMove_PutsTheFileBack()
        {
            using var repo = CreateMovedProject();

            var result = repo.Git.Rollback(new[]
            {
                new GitFileChange(GitStatus.Renamed, "Assets/New/b2.cs", "Assets/Old/b.cs"),
                new GitFileChange(GitStatus.Renamed, "Assets/staged2.txt", "Assets/staged.txt")
            }, false);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("2 files rolled back", result.Message);
            Assert.AreEqual(Lines("edited", 6), repo.Read("Assets/Old/b.cs"));
            Assert.IsFalse(repo.Exists("Assets/New/b2.cs"));
            Assert.AreEqual("s\n", repo.Read("Assets/staged.txt"));
            Assert.IsFalse(repo.Exists("Assets/staged2.txt"));
            CollectionAssert.AreEqual(new[]
            {
                "Renamed Assets/Old/a.cs -> Assets/New/a.cs",
                "Renamed Assets/Old/a.cs.meta -> Assets/New/a.cs.meta"
            }, repo.Git.WorkingTreeChanges().Changes.Select(Describe));
        }

        [Test]
        public void FileDiff_OfAMovedFile_ShowsTheRenameAndTheEdit()
        {
            using var repo = CreateMovedProject();

            var diff = repo.Git.FileDiff("Assets/New/b2.cs", true, "Assets/Old/b.cs");

            Assert.IsTrue(diff.IsSuccess, diff.Message);
            StringAssert.Contains("rename from Assets/Old/b.cs", diff.Output);
            StringAssert.Contains("rename to Assets/New/b2.cs", diff.Output);
            StringAssert.Contains("+one more", diff.Output);
        }

        [Test]
        public void Blame_AttributesLinesToCommitsAndLocalChanges()
        {
            using var repo = TestRepository.Create();
            repo.Write("Assets/blamed.txt", "one\ntwo\nthree\n");
            repo.CommitAll("first commit");
            var first = repo.Head;
            repo.Write("Assets/blamed.txt", "one\nTWO\nthree\n");
            repo.CommitAll("second commit");
            var second = repo.Head;
            repo.Write("Assets/blamed.txt", "one\nTWO\nthree\nfour\n");

            var (result, lines) = repo.Git.Blame("Assets/blamed.txt");

            Assert.IsTrue(result.IsSuccess, result.Message);
            CollectionAssert.AreEqual(new[] { "one", "TWO", "three", "four" }, lines.Select(line => line.Text));
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, lines.Select(line => line.Number));
            CollectionAssert.AreEqual(new[] { first, second, first }, lines.Take(3).Select(line => line.Hash));
            Assert.IsFalse(lines[3].IsCommitted);
            Assert.IsTrue(lines[0].IsCommitted);
            Assert.AreEqual("Test", lines[0].Author);
            Assert.AreEqual("first commit", lines[2].Summary);
            Assert.AreEqual("second commit", lines[1].Summary);
            Assert.AreEqual("Assets/blamed.txt", lines[1].FileName);
        }

        [Test]
        public void Blame_RefusesBinaryFiles()
        {
            using var repo = TestRepository.Create();
            repo.WriteBytes("Assets/image.png", new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x00, 0x0a, 0x01, 0x0a });
            repo.CommitAll("image");

            var (result, lines) = repo.Git.Blame("Assets/image.png");

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Blame is not available for binary files", result.Message);
            Assert.AreEqual(0, lines.Count);
        }

        [Test]
        public void WorkingTreeChanges_ListsStagedFilesRemovedFromDiskAsDeleted()
        {
            using var repo = TestRepository.Create();
            repo.Write("Assets/kept.txt", "k\n");
            repo.Write("Assets/modified.txt", "m\n");
            repo.CommitAll("base");

            repo.Write("Assets/added.txt", "a\n");
            repo.Write("Assets/modified.txt", "m2\n");
            repo.RunGit("add", "Assets/added.txt", "Assets/modified.txt");
            repo.Delete("Assets/added.txt");
            repo.Delete("Assets/modified.txt");

            var changes = repo.Git.WorkingTreeChanges().Changes;

            CollectionAssert.AreEqual(new[] { "Deleted Assets/added.txt", "Deleted Assets/modified.txt" },
                changes.Select(Describe));
        }

        [Test]
        public void Commit_CommitsOnlySelectedFiles()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.Write("b.txt", "b\n");
            repo.Write("c.txt", "c\n");
            repo.CommitAll("base");

            repo.Write("a.txt", "a2\n");
            repo.Write("b.txt", "b2\n");
            repo.Delete("c.txt");
            repo.Write("new file [1].txt", "n\n");

            var result = repo.Git.Commit(new[] { "a.txt", "c.txt", "new file [1].txt" }, "partial");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("3 files committed: partial", result.Message);
            Assert.AreEqual("M\tb.txt", repo.RunGit("diff", "--name-status"));
            Assert.AreEqual("", repo.RunGit("diff", "--cached", "--name-status"));
            Assert.AreEqual("M\ta.txt\nD\tc.txt\nA\tnew file [1].txt",
                repo.RunGit("show", "--name-status", "--format=", "HEAD"));
        }

        [Test]
        public void Commit_RejectedByAHook_LeavesTheIndexAsItWas()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "changed\n");
            repo.Write("Assets/new file.txt", "new\n");
            repo.Write("Assets/marked.txt", "marked by the user\n");
            repo.RunGit("add", "--intent-to-add", "Assets/marked.txt");
            repo.Write(".git/hooks/pre-commit", "#!/bin/sh\necho rejected by the hook >&2\nexit 1\n");
            var before = repo.Snapshot();

            var result = repo.Git.Commit(new[] { "a.txt", "Assets/new file.txt", "Assets/marked.txt" }, "rejected");

            Assert.IsFalse(result.IsSuccess);
            StringAssert.Contains("rejected by the hook", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            StringAssert.Contains("Assets/new file.txt", repo.RunGit("ls-files", "-z", "--others", "--exclude-standard"));
        }

        [Test]
        public void Commit_CommitsStagedMoveAndStagedDeletion()
        {
            using var repo = TestRepository.Create();
            repo.Write("Assets/Old/moved.txt", "m\n");
            repo.Write("Assets/deleted.txt", "d\n");
            repo.Write("Assets/kept.txt", "k\n");
            repo.CommitAll("base");

            Directory.CreateDirectory(Path.Combine(repo.Root, "Packages/New"));
            repo.RunGit("mv", "Assets/Old/moved.txt", "Packages/New/moved.txt");
            repo.RunGit("rm", "-q", "Assets/deleted.txt");
            repo.Write("Assets/kept.txt", "k2\n");

            var result = repo.Git.Commit(
                new[] { "Assets/Old/moved.txt", "Packages/New/moved.txt", "Assets/deleted.txt" }, "move");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("2 files committed: move", result.Message);
            Assert.AreEqual("D\tAssets/deleted.txt\nR100\tAssets/Old/moved.txt\tPackages/New/moved.txt",
                repo.RunGit("show", "--name-status", "--format=", "-M", "HEAD"));
            Assert.AreEqual("M\tAssets/kept.txt", repo.RunGit("diff", "--name-status"));
            Assert.AreEqual("", repo.RunGit("diff", "--cached", "--name-status"));
        }

        [Test]
        public void CreatePatch_AppliedToAnotherClone_ReproducesTheChanges()
        {
            using var origin = TestRepository.Create("origin");
            origin.Write("Assets/text.txt", "line1\nline2\n");
            origin.Write("Assets/deleted.txt", "gone\n");
            origin.Write("Assets/unselected.txt", "keep\n");
            origin.WriteBytes("Assets/image.png", new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x00, 0x01 });
            origin.CommitAll("base");
            using var clone = origin.Clone("clone");

            origin.Write("Assets/text.txt", "line1\nchanged\n");
            origin.Delete("Assets/deleted.txt");
            origin.Write("Assets/Новый файл.txt", "новый\n");
            origin.WriteBytes("Assets/image.png", new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x00, 0x02, 0xff });
            origin.Write("Assets/unselected.txt", "changed\n");
            var before = origin.Snapshot();

            var patch = Path.Combine(origin.BaseDirectory, "changes.patch");
            var files = new[] { "Assets/text.txt", "Assets/deleted.txt", "Assets/Новый файл.txt", "Assets/image.png" };
            var created = origin.Git.CreatePatch(files, patch);

            Assert.IsTrue(created.IsSuccess, created.Message);
            Assert.AreEqual($"Patch saved to {patch}", created.Message);
            Assert.AreEqual(before, origin.Snapshot(), "Creating a patch must not touch the repository.");

            var applied = clone.Git.ApplyPatch(patch);

            Assert.IsTrue(applied.IsSuccess, applied.Message);
            Assert.AreEqual("Patch changes.patch applied", applied.Message);
            Assert.AreEqual(origin.Read("Assets/text.txt"), clone.Read("Assets/text.txt"));
            Assert.AreEqual(origin.Read("Assets/Новый файл.txt"), clone.Read("Assets/Новый файл.txt"));
            CollectionAssert.AreEqual(origin.ReadBytes("Assets/image.png"), clone.ReadBytes("Assets/image.png"));
            Assert.IsFalse(clone.Exists("Assets/deleted.txt"));
            Assert.AreEqual("keep\n", clone.Read("Assets/unselected.txt"));

            var afterFirstApply = clone.Snapshot();
            var reapplied = clone.Git.ApplyPatch(patch);

            Assert.IsFalse(reapplied.IsSuccess);
            Assert.AreEqual(afterFirstApply, clone.Snapshot(), "A failed apply must not change anything.");
        }

        [Test]
        public void CreatePatch_WithTheUsersDiffSettings_StillApplies()
        {
            using var origin = TestRepository.Create("origin");
            origin.Write("Assets/text.txt", "line1\nline2\n");
            origin.CommitAll("base");
            using var clone = origin.Clone("clone");
            origin.RunGit("config", "diff.noprefix", "true");
            origin.RunGit("config", "diff.external", "false");
            origin.RunGit("config", "diff.upper.textconv", "sed s/line/LINE/");
            origin.Write(".git/info/attributes", "*.txt diff=upper\n");
            origin.Write("Assets/text.txt", "line1\nchanged\n");
            var patch = Path.Combine(origin.BaseDirectory, "changes.patch");

            var created = origin.Git.CreatePatch(new[] { "Assets/text.txt" }, patch);
            var applied = clone.Git.ApplyPatch(patch);

            Assert.IsTrue(created.IsSuccess, created.Message);
            Assert.IsTrue(applied.IsSuccess, applied.Message);
            Assert.AreEqual("line1\nchanged\n", clone.Read("Assets/text.txt"));
        }

        [Test]
        public void Integrate_WithDiffRelative_StillFindsConflictsOutsideTheProjectFolder()
        {
            using var repo = CreateDivergedBranches();
            repo.RunGit("config", "diff.relative", "true");
            repo.Write("f.txt", "mine\n");
            var before = repo.Snapshot();

            var result = repo.Git.Integrate(UpdateStrategy.Merge, "other");

            Assert.AreEqual("Merge of other rolled back, it conflicts with local changes\nConflicts:\n  f.txt",
                result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void ApplyStash_WithConflict_RestoresTheProjectAndKeepsTheStash()
        {
            using var repo = TestRepository.Create();
            repo.Write("c.txt", "base\n");
            repo.Write("u.txt", "u\n");
            repo.Write("s.txt", "s\n");
            repo.CommitAll("base");

            repo.Write("c.txt", "stashed\n");
            repo.Write("New Folder/Sub/a b.txt", "untracked from stash\n");
            repo.RunGit("stash", "push", "-q", "--include-untracked", "-m", "my stash");

            repo.Write("c.txt", "head\n");
            repo.CommitAll("head");

            repo.Write("u.txt", "local unstaged\n");
            repo.Write("s.txt", "local staged\n");
            repo.RunGit("add", "s.txt");
            repo.Write("keep.txt", "local untracked\n");
            var before = repo.Snapshot();

            var result = repo.Git.ApplyStash(repo.Git.Stashes()[0], drop: true);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("stash@{0} not applied because of conflicts, the project and the stash are unchanged\n" +
                            "Conflicts:\n  c.txt", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.IsFalse(repo.Exists("New Folder"));
            Assert.AreEqual(1, repo.Git.Stashes().Count);
        }

        [Test]
        public void ApplyStash_WhenTheCleanupFails_KeepsTheLocalChangesInTheStash()
        {
            using var repo = TestRepository.Create();
            repo.Write("c.txt", "base\n");
            repo.Write("u.txt", "u\n");
            repo.Write("s.txt", "s\n");
            repo.CommitAll("base");
            repo.Write("c.txt", "stashed\n");
            repo.RunGit("stash", "push", "-q", "-m", "my stash");
            repo.Write("c.txt", "head\n");
            repo.CommitAll("head");
            repo.Write("u.txt", "local unstaged\n");
            repo.Write("s.txt", "local staged\n");
            repo.RunGit("add", "s.txt");
            var git = repo.Git;
            var indexLock = Path.Combine(repo.Root, ".git", "index.lock");
            git.BeforeCommand = arguments =>
            {
                if (arguments[0] == "restore")
                {
                    File.WriteAllText(indexLock, "");
                }
            };

            GitResult result;
            var previous = Git.LockRetryTimeout;
            Git.LockRetryTimeout = TimeSpan.Zero;
            try
            {
                result = git.ApplyStash(git.Stashes()[0], drop: false);
            }
            finally
            {
                Git.LockRetryTimeout = previous;
                File.Delete(indexLock);
            }

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("stash@{1} not applied and the project could not be cleaned up\n" +
                                    "Conflicts:\n  c.txt\n", result.Message);
            StringAssert.EndsWith("Local changes from before it are saved in the stash, see git stash list",
                result.Message);
            var stashes = git.Stashes();
            Assert.AreEqual(2, stashes.Count);
            StringAssert.Contains("Uncommitted changes before applying \"On main: my stash\"", stashes[0].Message);
            StringAssert.EndsWith("my stash", stashes[1].Message);
            Assert.IsFalse(repo.Exists(".git/vetka-interrupted"));
            CollectionAssert.AreEquivalent(new[] { "s.txt", "u.txt" },
                repo.RunGit("stash", "show", "--name-only", "stash@{0}").Split('\n'));
        }

        [Test]
        public void ApplyStash_WithDrop_AppliesAndRemovesTheStash()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "stashed\n");

            var stash = repo.Git.Stash("Мой stash", false);
            Assert.IsTrue(stash.IsSuccess, stash.Message);

            var stashes = repo.Git.Stashes();
            Assert.AreEqual(1, stashes.Count);
            Assert.AreEqual("stash@{0}", stashes[0].Reference);
            StringAssert.EndsWith("Мой stash", stashes[0].Message);
            Assert.AreEqual("a\n", repo.Read("a.txt"));

            var pop = repo.Git.ApplyStash(repo.Git.Stashes()[0], drop: true);

            Assert.IsTrue(pop.IsSuccess, pop.Message);
            Assert.AreEqual("stash@{0} applied and dropped", pop.Message);
            Assert.AreEqual("stashed\n", repo.Read("a.txt"));
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void DropStash_AfterTheListShifted_DropsTheStashTheUserSaw()
        {
            using var repo = TestRepository.Create();
            var seen = StashThenStashAgain(repo);

            var result = repo.Git.DropStash(seen);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("stash@{1} dropped", result.Message);
            var left = repo.Git.Stashes();
            Assert.AreEqual(1, left.Count);
            StringAssert.EndsWith("second", left[0].Message);
        }

        [Test]
        public void ApplyStash_AfterTheListShifted_AppliesTheStashTheUserSaw()
        {
            using var repo = TestRepository.Create();
            var seen = StashThenStashAgain(repo);

            var result = repo.Git.ApplyStash(seen, drop: true);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("stash@{1} applied and dropped", result.Message);
            Assert.AreEqual("first\n", repo.Read("a.txt"));
            var left = repo.Git.Stashes();
            Assert.AreEqual(1, left.Count);
            StringAssert.EndsWith("second", left[0].Message);
        }

        [Test]
        public void StashActions_OnAStashThatIsGone_ChangeNothing()
        {
            using var repo = TestRepository.Create();
            var seen = StashThenStashAgain(repo);
            repo.RunGit("stash", "drop", "-q", "stash@{1}");
            var before = repo.Snapshot();

            var drop = repo.Git.DropStash(seen);
            var pop = repo.Git.ApplyStash(seen, drop: true);

            Assert.IsFalse(drop.IsSuccess);
            Assert.AreEqual("\"On main: first\" is no longer in the stash list, nothing was dropped", drop.Message);
            Assert.IsFalse(pop.IsSuccess);
            Assert.AreEqual("\"On main: first\" is no longer in the stash list, nothing was applied", pop.Message);
            Assert.AreEqual(before, repo.Snapshot());
            var left = repo.Git.Stashes();
            Assert.AreEqual(1, left.Count);
            StringAssert.EndsWith("second", left[0].Message);
        }

        [Test]
        public void ApplyStash_WithConflict_KeepsAnUntrackedFileOfTheSameName()
        {
            using var repo = TestRepository.Create();
            repo.Write("c.txt", "base\n");
            repo.CommitAll("base");
            repo.Write("c.txt", "stashed\n");
            repo.Write("u.txt", "from the stash\n");
            repo.RunGit("stash", "push", "-q", "--include-untracked", "-m", "with u");
            repo.Write("c.txt", "head\n");
            repo.CommitAll("head");
            repo.Write("u.txt", "my own file\n");
            var before = repo.Snapshot();

            var result = repo.Git.ApplyStash(repo.Git.Stashes()[0], drop: true);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("stash@{0} not applied because of conflicts, the project and the stash are unchanged\n" +
                            "Conflicts:\n  c.txt", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(1, repo.Git.Stashes().Count);
        }

        [Test]
        public void ApplyStash_WhenAnUntrackedFileIsInTheWay_ChangesNothing()
        {
            using var repo = TestRepository.Create();
            repo.Write("o.txt", "o\n");
            repo.CommitAll("base");
            repo.Write("o.txt", "stashed\n");
            repo.Write("u.txt", "from the stash\n");
            repo.RunGit("stash", "push", "-q", "--include-untracked", "-m", "with u");
            repo.Write("u.txt", "my own file\n");
            var before = repo.Snapshot();

            var result = repo.Git.ApplyStash(repo.Git.Stashes()[0], drop: true);

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("stash@{0} not applied, the project and the stash are unchanged\n", result.Message);
            StringAssert.Contains("u.txt already exists", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(1, repo.Git.Stashes().Count);
        }

        [Test]
        public void ApplyStash_WithConflict_KeepsFilesChangedMeanwhile()
        {
            using var repo = TestRepository.Create();
            repo.Write("c.txt", "base\n");
            repo.Write("other.txt", "other\n");
            repo.CommitAll("base");
            repo.Write("c.txt", "stashed\n");
            repo.RunGit("stash", "push", "-q", "-m", "c");
            repo.Write("c.txt", "head\n");
            repo.CommitAll("head");
            var git = repo.Git;
            git.BeforeCommand = arguments =>
            {
                if (arguments.Count > 1 && arguments[0] == "stash" && arguments[1] == "apply")
                {
                    repo.Write("other.txt", "saved meanwhile\n");
                }
            };

            var result = git.ApplyStash(git.Stashes()[0], drop: true);

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("stash@{0} not applied because of conflicts", result.Message);
            Assert.AreEqual("head\n", repo.Read("c.txt"));
            Assert.AreEqual("saved meanwhile\n", repo.Read("other.txt"));
            Assert.AreEqual(1, git.Stashes().Count);
        }

        [Test]
        public void ApplyStash_RestoresTheStagedVersion()
        {
            using var repo = TestRepository.Create();
            repo.Write("s.txt", "1\n2\n3\n4\n5\n");
            repo.CommitAll("base");
            StageAndEdit(repo, "s.txt");
            var staged = repo.RunGit("diff", "--cached");
            var unstaged = repo.RunGit("diff");
            repo.RunGit("stash", "push", "-q", "-m", "staged and unstaged");

            var result = repo.Git.ApplyStash(repo.Git.Stashes()[0], drop: true);

            Assert.AreEqual("stash@{0} applied and dropped", result.Message);
            Assert.AreEqual(staged, repo.RunGit("diff", "--cached"));
            Assert.AreEqual(unstaged, repo.RunGit("diff"));
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void ApplyStash_WhenTheStagedVersionCannotBeRestored_KeepsTheStash()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "1\n2\n3\n");
            repo.CommitAll("base");
            repo.Write("f.txt", "1\n2\nS\n");
            repo.RunGit("add", "f.txt");
            repo.Write("f.txt", "1\n2\n3\n");
            repo.RunGit("stash", "push", "-q", "-m", "staged");
            repo.Write("f.txt", "1\n2\nX\n");
            repo.CommitAll("x");

            var result = repo.Git.ApplyStash(repo.Git.Stashes()[0], drop: true);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("stash@{0} applied without the staging and not dropped\n" +
                            "The staged version could not be restored, it is kept in the stash, see git stash list",
                result.Message);
            Assert.AreEqual("1\n2\nX\n", repo.Read("f.txt"));
            Assert.AreEqual(1, repo.Git.Stashes().Count);
        }

        [Test]
        public void StashActions_DuringAMerge_RefuseAndKeepTheMerge()
        {
            using var repo = CreateDivergedBranches();
            repo.Write("f.txt", "stashed\n");
            repo.RunGit("stash", "push", "-q", "-m", "older");
            repo.RunGit("switch", "-q", "-c", "side");
            repo.Write("side.txt", "side\n");
            repo.CommitAll("side");
            repo.RunGit("switch", "-q", "main");
            repo.RunGit("merge", "-q", "--no-commit", "--no-ff", "side");
            var before = repo.Snapshot();
            var git = repo.Git;

            var stash = git.Stash("during the merge", false);
            var apply = git.ApplyStash(git.Stashes()[0], drop: true);

            Assert.AreEqual("Stashing is not possible while a merge is in progress, finish or abort it first",
                stash.Message);
            Assert.AreEqual("Applying stash@{0} is not possible while a merge is in progress, finish or abort it first",
                apply.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.IsTrue(repo.Exists(".git/MERGE_HEAD"));
            Assert.AreEqual(1, git.Stashes().Count);
        }

        private static GitStash StashThenStashAgain(TestRepository repo)
        {
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "first\n");
            repo.RunGit("stash", "push", "-q", "-m", "first");
            var seen = repo.Git.Stashes()[0];
            repo.Write("a.txt", "second\n");
            repo.RunGit("stash", "push", "-q", "-m", "second");
            return seen;
        }

        [TestCase(UpdateStrategy.Merge)]
        [TestCase(UpdateStrategy.Rebase)]
        public void UpdateProject_CommittedConflict_RollsBack(UpdateStrategy strategy)
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = PublishBase(remote);
            using var local = remote.Clone("local");

            upstream.Write("f.txt", "theirs\n");
            upstream.CommitAll("theirs");
            upstream.RunGit("push", "-q");

            local.Write("f.txt", "mine\n");
            local.CommitAll("mine");
            local.Write("o.txt", "local change\n");
            var before = local.Snapshot();

            var result = local.Git.UpdateProject(strategy);

            Assert.IsFalse(result.IsSuccess);
            StringAssert.Contains("f.txt", result.Message);
            Assert.AreEqual(before, local.Snapshot());
            Assert.AreEqual(0, local.Git.Stashes().Count);
        }

        [TestCase(UpdateStrategy.Merge)]
        [TestCase(UpdateStrategy.Rebase)]
        public void UpdateProject_LocalChangesConflictWithIncoming_RollsBackEverything(UpdateStrategy strategy)
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = PublishBase(remote);
            using var local = remote.Clone("local");

            upstream.Write("f.txt", "theirs\n");
            upstream.Write("incoming.txt", "new upstream file\n");
            upstream.CommitAll("theirs");
            upstream.RunGit("push", "-q");

            local.Write("root.txt", "local commit\n");
            local.CommitAll("local commit");
            local.Write("f.txt", "mine\n");
            local.Write("untracked.txt", "u\n");
            var before = local.Snapshot();

            var result = local.Git.UpdateProject(strategy);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual((strategy == UpdateStrategy.Merge ? "Merge of origin/main" : "Rebase onto origin/main") +
                            " rolled back, it conflicts with local changes\nConflicts:\n  f.txt", result.Message);
            Assert.AreEqual(before, local.Snapshot());
            Assert.AreEqual(0, local.Git.Stashes().Count);
        }

        [Test]
        public void UpdateProject_WithoutConflicts_KeepsLocalChanges()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = PublishBase(remote);
            using var local = remote.Clone("local");

            upstream.Write("f.txt", "theirs\n");
            upstream.CommitAll("theirs");
            upstream.RunGit("push", "-q");

            local.Write("o.txt", "local change\n");

            var result = local.Git.UpdateProject(UpdateStrategy.Merge);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Merged 1 commit from origin/main, local changes restored", result.Message);
            Assert.AreEqual("theirs\n", local.Read("f.txt"));
            Assert.AreEqual("local change\n", local.Read("o.txt"));
            Assert.AreEqual(0, local.Git.Stashes().Count);
        }

        [Test]
        public void Checkout_CarriesNonConflictingLocalChangesOver()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("g.txt", "other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("f.txt", "mine\n");

            var result = repo.Git.Checkout("other");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Checked out other, local changes carried over", result.Message);
            Assert.AreEqual("other", repo.CurrentBranch);
            Assert.AreEqual("mine\n", repo.Read("f.txt"));
            Assert.IsTrue(repo.Exists("g.txt"));
        }

        [Test]
        public void Checkout_WithConflictingLocalChanges_RollsBack()
        {
            using var repo = CreateDivergedBranches();
            repo.Write("f.txt", "mine\n");
            repo.Write("untracked.txt", "u\n");
            var before = repo.Snapshot();

            var result = repo.Git.Checkout("other");

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void Checkout_WithATagNamedLikeTheBranch_StillRollsBack()
        {
            using var repo = CreateDivergedBranches();
            repo.RunGit("tag", "main");
            repo.Write("f.txt", "mine\n");
            var before = repo.Snapshot();

            var result = repo.Git.Checkout("other");

            Assert.AreEqual("Checkout of other rolled back, it conflicts with local changes\nConflicts:\n  f.txt",
                result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual("main", repo.Git.CurrentBranch());
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void Checkout_WhenItCannotReturn_DoesNotSayItRolledBack()
        {
            using var repo = CreateDivergedBranches();
            repo.Write("f.txt", "mine\n");
            var git = repo.Git;
            var indexLock = Path.Combine(repo.Root, ".git", "index.lock");
            git.BeforeCommand = arguments =>
            {
                if (arguments.Count == 2 && arguments[0] == "switch" && arguments[1] == "main")
                {
                    File.WriteAllText(indexLock, "");
                }
            };

            GitResult result;
            var previous = Git.LockRetryTimeout;
            Git.LockRetryTimeout = TimeSpan.Zero;
            try
            {
                result = git.Checkout("other");
            }
            finally
            {
                Git.LockRetryTimeout = previous;
                File.Delete(indexLock);
            }

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("Checkout of other could not be rolled back, it conflicts with local changes\n" +
                                    "Conflicts:\n  f.txt\nCould not return to main. " +
                                    "Local changes are kept in the stash, restore them with git stash pop\n",
                result.Message);
            Assert.AreEqual(1, git.Stashes().Count);
        }

        [Test]
        public void CreateBranch_WithConflictingLocalChanges_RollsBackAndDeletesTheBranch()
        {
            using var repo = CreateDivergedBranches();
            repo.Write("f.txt", "mine\n");
            var before = repo.Snapshot();

            var result = repo.Git.CreateBranch("created", "other", checkout: true);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(before, repo.Snapshot());
        }

        [Test]
        public void CreateBranch_RejectsInvalidNames()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");

            Assert.IsFalse(repo.Git.CreateBranch("bad..name", null, checkout: false).IsSuccess);
            Assert.IsFalse(repo.Git.CreateBranch("with space", null, checkout: false).IsSuccess);
            Assert.IsTrue(repo.Git.CreateBranch("фича/новая", null, checkout: false).IsSuccess);
        }

        [Test]
        public void Branches_ListsLocalAndCurrentBranches_AndRecentCheckouts()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "feature");
            repo.RunGit("switch", "-q", "-c", "фича/x");
            repo.RunGit("switch", "-q", "main");

            var branches = repo.Git.Branches();

            CollectionAssert.AreEquivalent(new[] { "main", "feature", "фича/x" }, branches.Select(b => b.Name));
            Assert.AreEqual("main", branches.Single(b => b.IsCurrent).Name);
            Assert.IsTrue(branches.All(b => !b.IsRemote));

            var recent = repo.Git.RecentBranches();

            Assert.AreEqual(new[] { "main", "фича/x", "feature" }, recent.Take(3).ToArray());
        }

        [Test]
        public void CommitFiles_ListsFilesOfRootAndMergeCommits()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.Write("Р ф.txt", "r\n");
            repo.CommitAll("root");
            var root = repo.Head;

            repo.RunGit("switch", "-q", "-c", "feature");
            repo.Write("f.txt", "feature\n");
            repo.CommitAll("feature");
            repo.RunGit("switch", "-q", "main");
            repo.Write("a.txt", "a2\n");
            repo.CommitAll("main change");
            repo.RunGit("merge", "-q", "--no-ff", "--no-edit", "feature");

            var rootFiles = repo.Git.CommitFiles(root);
            var mergeFiles = repo.Git.CommitFiles(repo.Head);

            CollectionAssert.AreEqual(new[] { "a.txt", "Р ф.txt" }, rootFiles.Select(f => f.Path));
            Assert.IsTrue(rootFiles.All(f => f.Status == GitStatus.Added));
            CollectionAssert.AreEqual(new[] { "f.txt" }, mergeFiles.Select(f => f.Path));
        }

        [Test]
        public void FileDiff_ShowsTrackedAndUntrackedChanges()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "b\n");
            repo.Write("new.txt", "n\n");

            var tracked = repo.Git.FileDiff("a.txt", false);
            var untracked = repo.Git.FileDiff("new.txt", true);

            Assert.IsTrue(tracked.IsSuccess, tracked.Message);
            StringAssert.Contains("-a", tracked.Output);
            StringAssert.Contains("+b", tracked.Output);
            Assert.IsTrue(untracked.IsSuccess, untracked.Message);
            StringAssert.Contains("+n", untracked.Output);
        }

        [TestCase(UpdateStrategy.Merge)]
        [TestCase(UpdateStrategy.Rebase)]
        public void Integrate_WithOnlyASubmoduleChange_LeavesAnOlderStashAlone(UpdateStrategy strategy)
        {
            using var library = CreateLibrary();
            using var repo = CreateBranchesWithSubmodule(library);
            StashUntrackedFile(repo, "older");
            repo.Write("library/l.txt", "dirty\n");

            var result = repo.Git.Integrate(strategy, "other");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(strategy == UpdateStrategy.Merge
                ? "Merged 1 commit from other"
                : "Rebased onto other: 1 new commit", result.Message);
            Assert.AreEqual("other\n", repo.Read("f.txt"));
            Assert.AreEqual("dirty\n", repo.Read("library/l.txt"));
            AssertOnlyStash(repo, "older");
        }

        [Test]
        public void Checkout_WithOnlyASubmoduleChange_LeavesAnOlderStashAlone()
        {
            using var library = CreateLibrary();
            using var repo = CreateBranchesWithSubmodule(library);
            StashUntrackedFile(repo, "older");
            repo.Write("library/l.txt", "dirty\n");

            var result = repo.Git.Checkout("other");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Checked out other", result.Message);
            Assert.AreEqual("other", repo.CurrentBranch);
            Assert.AreEqual("dirty\n", repo.Read("library/l.txt"));
            AssertOnlyStash(repo, "older");
        }

        [TestCase(UpdateStrategy.Merge)]
        [TestCase(UpdateStrategy.Rebase)]
        public void Integrate_KeepsStagedAndUnstagedChangesApart(UpdateStrategy strategy)
        {
            using var repo = CreateDivergedBranches();
            repo.Write("s.txt", "1\n2\n3\n4\n5\n");
            repo.CommitAll("s");
            StageAndEdit(repo, "s.txt");
            var staged = repo.RunGit("diff", "--cached");
            var unstaged = repo.RunGit("diff");

            var result = repo.Git.Integrate(strategy, "other");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("other\n", repo.Read("f.txt"));
            Assert.AreEqual(staged, repo.RunGit("diff", "--cached"));
            Assert.AreEqual(unstaged, repo.RunGit("diff"));
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void Checkout_KeepsStagedAndUnstagedChangesApart()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("s.txt", "1\n2\n3\n4\n5\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            StageAndEdit(repo, "s.txt");
            var staged = repo.RunGit("diff", "--cached");
            var unstaged = repo.RunGit("diff");

            var result = repo.Git.Checkout("other");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("other", repo.CurrentBranch);
            Assert.AreEqual(staged, repo.RunGit("diff", "--cached"));
            Assert.AreEqual(unstaged, repo.RunGit("diff"));
        }

        [Test]
        public void Integrate_WhenTheStagedVersionCannotBeRestored_KeepsItInTheStash()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "1\n2\n3\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "1\n2\nX\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("f.txt", "1\n2\nS\n");
            repo.RunGit("add", "f.txt");
            repo.Write("f.txt", "1\n2\n3\n");

            var result = repo.Git.Integrate(UpdateStrategy.Merge, "other");

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Merged 1 commit from other, local changes restored without the staging\n" +
                            "The staged version could not be restored, it is kept in the stash, see git stash list",
                result.Message);
            Assert.AreEqual("1\n2\nX\n", repo.Read("f.txt"));
            Assert.AreEqual(1, repo.Git.Stashes().Count);
            Assert.AreEqual("1\n2\nS", repo.RunGit("show", "stash@{0}^2:f.txt"));
        }

        [Test]
        public void Integrate_DuringAMerge_RefusesAndKeepsTheMerge()
        {
            using var repo = CreateDivergedBranchesWithMergeInProgress();
            var before = repo.Snapshot();

            var result = repo.Git.Integrate(UpdateStrategy.Merge, "other");

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Merge of other is not possible while a merge is in progress, finish or abort it first",
                result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.IsTrue(repo.Exists(".git/MERGE_HEAD"));
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void Checkout_DuringAMerge_RefusesAndKeepsTheMerge()
        {
            using var repo = CreateDivergedBranchesWithMergeInProgress();
            var before = repo.Snapshot();

            var result = repo.Git.Checkout("other");

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Checkout of other is not possible while a merge is in progress, finish or abort it first",
                result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.IsTrue(repo.Exists(".git/MERGE_HEAD"));
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void Checkout_WithAFileInUse_RollsBackAndNamesTheFile()
        {
            using var repo = CreateBranchesWithANativePlugin(localCommit: true);
            repo.Write("Assets/local.txt", "local change\n");
            var before = repo.Snapshot();

            GitResult result;
            using (repo.InUse("Assets/Plugins/native.dll"))
            {
                result = repo.Git.Checkout("other");
            }

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Checkout of other rolled back, these files are in use and could not be updated:\n" +
                            "  Assets/Plugins/native.dll\n" +
                            "Close the programs that use them and try again, " +
                            "a native plugin loaded by Unity needs an editor restart", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [TestCase(UpdateStrategy.Merge, true)]
        [TestCase(UpdateStrategy.Merge, false)]
        [TestCase(UpdateStrategy.Rebase, true)]
        public void Integrate_WithAFileInUse_LeavesTheProjectUnchanged(UpdateStrategy strategy, bool localCommit)
        {
            using var repo = CreateBranchesWithANativePlugin(localCommit);
            repo.Write("Assets/local.txt", "local change\n");
            var before = repo.Snapshot();

            GitResult result;
            using (repo.InUse("Assets/Plugins/native.dll"))
            {
                result = repo.Git.Integrate(strategy, "other");
            }

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith((strategy == UpdateStrategy.Merge ? "Merge of other" : "Rebase onto other") +
                                    " failed, the project is unchanged\n", result.Message);
            StringAssert.Contains("Assets/Plugins/native.dll", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void Integrate_WhenAChangedFileIsInUse_PutsTheLocalChangesBack()
        {
            using var repo = CreateLocalChangesOfEveryKind();
            var before = repo.Snapshot();

            GitResult result;
            using (repo.InUse("Assets/Plugins/native.dll"))
            {
                result = repo.Git.Integrate(UpdateStrategy.Merge, "other");
            }

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("Merge of other is not possible, the local changes could not be stashed, " +
                                    "the project is unchanged\n", result.Message);
            StringAssert.Contains("Assets/Plugins/native.dll", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void Checkout_WhenAChangedFileIsInUse_PutsTheLocalChangesBack()
        {
            using var repo = CreateLocalChangesOfEveryKind();
            var before = repo.Snapshot();

            GitResult result;
            using (repo.InUse("Assets/Plugins/native.dll"))
            {
                result = repo.Git.Checkout("other");
            }

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("Checkout of other is not possible, the local changes could not be stashed, " +
                                    "the project is unchanged\n", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Stash_WhenAFileIsInUse_ChangesNothing(bool includeUntracked)
        {
            using var repo = CreateLocalChangesOfEveryKind();
            var before = repo.Snapshot();

            GitResult result;
            using (repo.InUse(includeUntracked ? "Assets/untracked.txt" : "Assets/Plugins/native.dll"))
            {
                result = repo.Git.Stash("wip", includeUntracked);
            }

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("Changes not stashed, the project is unchanged\n", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void Integrate_WhenTheAbortFails_SaysTheMergeIsStillInProgress()
        {
            using var repo = CreateDivergedBranchesWithLocalChange();
            var git = repo.Git;
            var indexLock = Path.Combine(repo.Root, ".git", "index.lock");
            git.BeforeCommand = arguments =>
            {
                if (IsMergeAbort(arguments))
                {
                    File.WriteAllText(indexLock, "");
                }
            };

            GitResult result;
            var previous = Git.LockRetryTimeout;
            Git.LockRetryTimeout = TimeSpan.Zero;
            try
            {
                result = git.Integrate(UpdateStrategy.Merge, "other");
            }
            finally
            {
                Git.LockRetryTimeout = previous;
                File.Delete(indexLock);
            }

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("Merge of other failed and could not be undone\nConflicts:\n  f.txt\n" +
                                    "A merge is still in progress: Unable to create", result.Message);
            StringAssert.EndsWith("Local changes are kept in the stash, restore them with git stash pop",
                result.Message);
            Assert.IsTrue(repo.Exists(".git/MERGE_HEAD"));
            Assert.AreEqual(1, git.Stashes().Count);
        }

        [Test]
        public void Integrate_WhenTheAbortFailsOnce_TriesAgainAndRestoresTheProject()
        {
            using var repo = CreateDivergedBranchesWithLocalChange();
            var before = repo.Snapshot();
            var git = repo.Git;
            var indexLock = Path.Combine(repo.Root, ".git", "index.lock");
            var aborts = 0;
            git.BeforeCommand = arguments =>
            {
                if (!IsMergeAbort(arguments))
                {
                    return;
                }

                if (++aborts == 1)
                {
                    File.WriteAllText(indexLock, "");
                }
                else
                {
                    File.Delete(indexLock);
                }
            };

            var result = git.Integrate(UpdateStrategy.Merge, "other");

            Assert.AreEqual("Merge of other failed, the project is unchanged\nConflicts:\n  f.txt", result.Message);
            Assert.AreEqual(2, aborts);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, git.Stashes().Count);
        }

        [Test]
        public void Integrate_WhenAnotherGitHoldsTheIndexForAMoment_StillRestoresTheLocalChanges()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("o.txt", "o\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("o.txt", "local change\n");
            var git = repo.Git;
            var indexLock = Path.Combine(repo.Root, ".git", "index.lock");
            var locked = false;
            git.BeforeCommand = arguments =>
            {
                if (!locked && arguments.Count > 1 && arguments[0] == "stash" && arguments[1] == "apply")
                {
                    locked = true;
                    File.WriteAllText(indexLock, "");
                    Task.Delay(500).ContinueWith(_ => File.Delete(indexLock));
                }
            };

            var result = git.Integrate(UpdateStrategy.Merge, "other");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Merged 1 commit from other, local changes restored", result.Message);
            Assert.AreEqual("local change\n", repo.Read("o.txt"));
            Assert.AreEqual(0, git.Stashes().Count);
        }

        [Test]
        public void Integrate_WhenAFileIsSavedMeanwhile_KeepsItAndTheStash()
        {
            using var repo = CreateDivergedBranches();
            repo.Write("Assets/Scene.unity", "scene\n");
            repo.Write("o.txt", "o\n");
            repo.CommitAll("scene");
            repo.Write("o.txt", "local change\n");
            repo.Write(".git/hooks/post-merge", "#!/bin/sh\necho 'saved while merging' > Assets/Scene.unity\n");

            var result = repo.Git.Integrate(UpdateStrategy.Merge, "other");

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Merged 1 commit from other, but files were changed while it ran:\n" +
                            "  Assets/Scene.unity\n" +
                            "Local changes are kept in the stash, restore them with git stash pop", result.Message);
            Assert.AreEqual("saved while merging\n", repo.Read("Assets/Scene.unity"));
            Assert.AreEqual("other\n", repo.Read("f.txt"));
            Assert.AreEqual(1, repo.Git.Stashes().Count);
        }

        [TestCase(UpdateStrategy.Merge)]
        [TestCase(UpdateStrategy.Rebase)]
        public void Integrate_ThatMovesASubmodule_StillRestoresLocalChanges(UpdateStrategy strategy)
        {
            using var library = CreateLibrary();
            using var repo = CreateBranchesWithSubmodule(library);
            library.Write("l.txt", "l2\n");
            library.CommitAll("library 2");
            repo.RunGit("switch", "-q", "other");
            repo.RunGit("-C", "library", "fetch", "-q", "origin");
            repo.RunGit("-C", "library", "checkout", "-q", "FETCH_HEAD");
            repo.RunGit("add", "library");
            repo.RunGit("commit", "-q", "-m", "library pointer");
            repo.RunGit("switch", "-q", "main");
            repo.RunGit("-C", "library", "checkout", "-q", "HEAD~1");
            repo.Write("s.txt", "s\n");
            repo.CommitAll("s");
            repo.Write("s.txt", "local change\n");

            var result = repo.Git.Integrate(strategy, "other");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(strategy == UpdateStrategy.Merge
                ? "Merged 2 commits from other, local changes restored"
                : "Rebased onto other: 2 new commits, local changes restored", result.Message);
            Assert.AreEqual("local change\n", repo.Read("s.txt"));
            Assert.AreEqual("other\n", repo.Read("f.txt"));
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [TestCase(UpdateStrategy.Merge)]
        [TestCase(UpdateStrategy.Rebase)]
        public void Integrate_WhenItRollsBack_KeepsUncommittedWorkInASubmodule(UpdateStrategy strategy)
        {
            using var library = CreateLibrary();
            using var repo = CreateBranchesWithSubmodule(library);
            repo.RunGit("config", "submodule.recurse", "true");
            repo.Write("library/l.txt", "dirty\n");
            repo.Write("f.txt", "local\n");

            var result = repo.Git.Integrate(strategy, "other");

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith((strategy == UpdateStrategy.Merge ? "Merge of other" : "Rebase onto other") +
                                    " rolled back, it conflicts with local changes", result.Message);
            Assert.AreEqual("dirty\n", repo.Read("library/l.txt"));
            Assert.AreEqual("local\n", repo.Read("f.txt"));
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void Integrate_ThatConflictsInCommits_KeepsUncommittedWorkInASubmodule()
        {
            using var library = CreateLibrary();
            using var repo = CreateBranchesWithSubmodule(library);
            repo.RunGit("config", "submodule.recurse", "true");
            repo.Write("f.txt", "main\n");
            repo.CommitAll("main");
            repo.Write("library/l.txt", "dirty\n");

            var result = repo.Git.Integrate(UpdateStrategy.Merge, "other");

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("Merge of other failed, the project is unchanged", result.Message);
            Assert.AreEqual("dirty\n", repo.Read("library/l.txt"));
        }

        [Test]
        public void Checkout_WhenItRollsBack_KeepsUncommittedWorkInASubmodule()
        {
            using var library = CreateLibrary();
            using var repo = CreateBranchesWithSubmodule(library);
            repo.RunGit("config", "submodule.recurse", "true");
            repo.Write("library/l.txt", "dirty\n");
            repo.Write("f.txt", "local\n");

            var result = repo.Git.Checkout("other");

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("Checkout of other rolled back, it conflicts with local changes", result.Message);
            Assert.AreEqual("main", repo.CurrentBranch);
            Assert.AreEqual("dirty\n", repo.Read("library/l.txt"));
            Assert.AreEqual("local\n", repo.Read("f.txt"));
        }

        [Test]
        public void Push_KeepsTheUsersCheckForUnpushedSubmoduleCommits()
        {
            using var library = CreateLibrary();
            using var repo = CreateBranchesWithSubmodule(library);
            using var remote = TestRepository.CreateBare();
            repo.RunGit("remote", "add", "origin", remote.Root);
            repo.RunGit("push", "-q", "-u", "origin", "main");
            repo.RunGit("config", "push.recurseSubmodules", "check");
            repo.Write("library/l.txt", "unpushed\n");
            repo.RunGit("-C", "library", "-c", "user.name=Test", "-c", "user.email=test@example.com",
                "commit", "-q", "-a", "-m", "unpushed");
            repo.CommitAll("bump");
            var pushed = remote.RunGit("rev-parse", "main");

            var result = repo.Git.Push();

            Assert.IsFalse(result.IsSuccess, result.Message);
            StringAssert.Contains("library", result.Message);
            Assert.AreEqual(pushed, remote.RunGit("rev-parse", "main"));
        }

        [Test]
        public void Commit_WithLegacyEncodingSettings_WritesAndReadsUtf8()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.RunGit("config", "i18n.commitEncoding", "cp1251");
            repo.RunGit("config", "i18n.logOutputEncoding", "cp866");
            repo.Write("a.txt", "b\n");

            var commit = repo.Git.Commit(new[] { "a.txt" }, "Второй коммит");

            Assert.IsTrue(commit.IsSuccess, commit.Message);
            StringAssert.DoesNotContain("encoding", repo.RunGit("cat-file", "-p", "HEAD"));
            Assert.AreEqual("Второй коммит", repo.Git.Log()[0].Subject);
            Assert.AreEqual("Второй коммит", repo.Git.LastCommitMessage());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Rollback_OfAFileMarkedIntentToAdd_DeletesItOnlyWhenAsked(bool deleteAdded)
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.Write("Assets/new [1].txt", "precious\n");
            repo.RunGit("add", "-N", "Assets/new [1].txt");

            var result = repo.Git.Rollback(new[] { new GitFileChange(GitStatus.Added, "Assets/new [1].txt") },
                deleteAdded);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(!deleteAdded, repo.Exists("Assets/new [1].txt"));
            Assert.AreEqual(deleteAdded ? "" : "?? \"Assets/new [1].txt\"",
                repo.RunGit("-c", "core.quotepath=true", "status", "--porcelain=v1", "--untracked-files=all"));
            if (!deleteAdded)
            {
                Assert.AreEqual("precious\n", repo.Read("Assets/new [1].txt"));
            }
        }

        [Test]
        public void Commit_OfAFileThatStillHasConflictMarkers_Refuses()
        {
            using var repo = CreateStashConflict();
            var head = repo.Head;

            var result = repo.Git.Commit(new[] { "f.txt", "g.txt" }, "Oops");

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Committing is not possible, these files still have conflicts:\n  f.txt", result.Message);
            Assert.AreEqual(head, repo.Head);
            StringAssert.Contains("UU f.txt", repo.RunGit("status", "--porcelain=v1"));
        }

        [Test]
        public void Commit_OfAConflictResolvedByHand_CommitsTheResolution()
        {
            using var repo = CreateStashConflict();
            repo.Write("f.txt", "resolved\n");

            var result = repo.Git.Commit(new[] { "f.txt" }, "Resolve");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("resolved", repo.RunGit("show", "HEAD:f.txt"));
            StringAssert.DoesNotContain("f.txt", repo.RunGit("status", "--porcelain=v1"));
        }

        [Test]
        public void Commit_DuringAMerge_RefusesAndKeepsTheMerge()
        {
            using var repo = CreateDivergedBranchesWithMergeInProgress();
            var before = repo.Snapshot();

            var result = repo.Git.Commit(new[] { "side.txt" }, "Partial");

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Committing is not possible while a merge is in progress, finish or abort it first",
                result.Message);
            Assert.AreEqual(before, repo.Snapshot());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Rollback_BeforeTheFirstCommit_UnstagesTheAddedFiles(bool deleteAdded)
        {
            using var repo = TestRepository.Create();
            repo.Write("Assets/a b.txt", "a\n");
            repo.Write("Assets/keep.txt", "k\n");
            repo.RunGit("add", "-A");

            var result = repo.Git.Rollback(new[] { new GitFileChange(GitStatus.Added, "Assets/a b.txt") },
                deleteAdded);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("1 file rolled back", result.Message);
            Assert.AreEqual(!deleteAdded, repo.Exists("Assets/a b.txt"));
            Assert.AreEqual("Assets/keep.txt", repo.RunGit("diff", "--cached", "--name-only"));
        }

        [Test]
        public void FileDiffAndCreatePatch_BeforeTheFirstCommit_ShowTheAddedFile()
        {
            using var repo = TestRepository.Create();
            repo.Write("Assets/a b.txt", "added\n");
            repo.RunGit("add", "-A");
            var patch = Path.Combine(repo.BaseDirectory, "first.patch");

            var diff = repo.Git.FileDiff("Assets/a b.txt", false);
            var created = repo.Git.CreatePatch(new[] { "Assets/a b.txt" }, patch);

            Assert.IsTrue(diff.IsSuccess, diff.Message);
            StringAssert.Contains("+added", diff.Output);
            Assert.IsTrue(created.IsSuccess, created.Message);
            StringAssert.Contains("+added", File.ReadAllText(patch));
        }

        [Test]
        public void FileDiff_OfAnUntrackedFileThatIsGone_ReportsTheError()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");

            var diff = repo.Git.FileDiff("gone.txt", true);

            Assert.IsFalse(diff.IsSuccess);
        }

        [Test]
        public void ApplyPatch_WhenAFilterFailsHalfway_PutsTheFilesBack()
        {
            using var repo = TestRepository.Create();
            UseFailingFilter(repo);
            repo.Write("b.bin", "b1\n");
            repo.Write("z.txt", Lines("z", 12));
            repo.CommitAll("base");
            repo.Write("b.bin", "b2 FAIL\n");
            repo.Write("z.txt", "changed\n" + Lines("z", 12).Substring("z line 1\n".Length));
            var patch = Path.Combine(repo.BaseDirectory, "change.patch");
            var created = repo.Git.CreatePatch(new[] { "b.bin", "z.txt" }, patch);
            Assert.IsTrue(created.IsSuccess, created.Message);
            repo.RunGit("restore", ".");
            repo.Write("z.txt", Lines("z", 12).Replace("z line 10", "local edit"));
            var before = repo.Snapshot();

            var result = repo.Git.ApplyPatch(patch);

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("Patch change.patch not applied, the project is unchanged\n", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
        }

        [TestCase(UpdateStrategy.Merge)]
        [TestCase(UpdateStrategy.Rebase)]
        public void Integrate_ThatFailsOnAConflict_StashesAFileSavedWhileItRan(UpdateStrategy strategy)
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("s.txt", "scene\n");
            repo.Write("o.txt", "o\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("f.txt", "main\n");
            repo.CommitAll("main");
            var head = repo.Head;
            repo.Write("o.txt", "local change\n");
            var git = repo.Git;
            var saved = false;
            git.BeforeCommand = arguments =>
            {
                if (!saved && arguments[0] == "diff" && arguments.Contains("--diff-filter=U"))
                {
                    saved = true;
                    repo.Write("s.txt", "saved meanwhile\n");
                }
            };

            var result = git.Integrate(strategy, "other");

            var name = strategy == UpdateStrategy.Merge ? "Merge of other" : "Rebase onto other";
            StringAssert.StartsWith($"{name} failed, the project is unchanged\nConflicts:\n  f.txt\n" +
                                    "These files were changed while it ran or left changed by it, " +
                                    $"the changes are saved in the stash as \"Changes left after {name} failed at ",
                result.Message);
            StringAssert.EndsWith("\n  s.txt", result.Message);
            Assert.AreEqual("scene\n", repo.Read("s.txt"));
            Assert.AreEqual("local change\n", repo.Read("o.txt"));
            Assert.AreEqual(head, repo.Head);
            Assert.AreEqual(1, git.Stashes().Count);
            Assert.AreEqual("saved meanwhile", repo.RunGit("show", "stash@{0}:s.txt"));
        }

        [Test]
        public void Integrate_ThatFailsAfterWritingAMergedFile_SavesItAndRestoresTheProject()
        {
            using var repo = CreateDivergedBranchesWithLocalChange();
            repo.RunGit("config", "merge.ff", "only");
            var head = repo.Head;
            var git = repo.Git;
            var written = false;
            git.BeforeCommand = arguments =>
            {
                if (!written && arguments[0] == "diff" && arguments.Contains("--diff-filter=U"))
                {
                    written = true;
                    repo.Write("f.txt", "main and other\n");
                    repo.RunGit("add", "f.txt");
                }
            };

            var result = git.Integrate(UpdateStrategy.Merge, "other");

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("Merge of other failed, the project is unchanged\n", result.Message);
            StringAssert.Contains("These files were changed while it ran or left changed by it, " +
                                  "the changes are saved in the stash as \"Changes left after Merge of other failed at ",
                result.Message);
            StringAssert.EndsWith("\n  f.txt", result.Message);
            Assert.AreEqual("main\n", repo.Read("f.txt"));
            Assert.AreEqual("", repo.RunGit("diff", "--cached"));
            Assert.AreEqual("local change\n", repo.Read("o.txt"));
            Assert.AreEqual(head, repo.Head);
            var stashes = git.Stashes();
            Assert.AreEqual(1, stashes.Count);
            StringAssert.Contains("Changes left after Merge of other failed", stashes[0].Message);
            Assert.AreEqual("main and other", repo.RunGit("show", "stash@{0}:f.txt"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Switch_WhenAFilterCannotWriteAFile_PutsTheProjectBack(bool merge)
        {
            using var repo = TestRepository.Create();
            UseFailingFilter(repo);
            repo.Write("b.bin", "b1\n");
            repo.Write("o.txt", "o\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("b.bin", "b2 FAIL\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("o.txt", "local change\n");
            var before = repo.Snapshot();

            var result = merge ? repo.Git.Integrate(UpdateStrategy.Merge, "other") : repo.Git.Checkout("other");

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith((merge ? "Merge of other" : "Checkout of other") +
                                    " failed, the project is unchanged\n", result.Message);
            StringAssert.EndsWith("These files could not be written and were put back:\n  b.bin", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Switch_WhenAFileIsSavedWhileTheLocalChangesComeBack_KeepsIt(bool merge)
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("s.txt", "scene\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            var head = repo.Head;
            repo.Write("f.txt", "local\n");
            var git = repo.Git;
            var saved = false;
            git.BeforeCommand = arguments =>
            {
                if (!saved && arguments.Count > 2 && arguments[0] == "stash" && arguments[1] == "apply")
                {
                    saved = true;
                    repo.Write("s.txt", "saved meanwhile\n");
                }
            };

            var result = merge ? git.Integrate(UpdateStrategy.Merge, "other") : git.Checkout("other");

            Assert.AreEqual((merge ? "Merge of other" : "Checkout of other") +
                            " rolled back, it conflicts with local changes\nConflicts:\n  f.txt", result.Message);
            Assert.AreEqual("saved meanwhile\n", repo.Read("s.txt"));
            Assert.AreEqual("local\n", repo.Read("f.txt"));
            Assert.AreEqual(head, repo.Head);
            Assert.AreEqual("main", repo.CurrentBranch);
            Assert.AreEqual(0, git.Stashes().Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Switch_WithAFileThatStaysModifiedAfterStashing_ChangesNothing(bool merge)
        {
            using var repo = CreateDivergedBranches();
            repo.WriteBytes("crlf.txt", Encoding.ASCII.GetBytes("a\r\nb\r\n"));
            repo.CommitAll("crlf");
            repo.Write(".gitattributes", "*.txt text eol=lf\n");
            repo.RunGit("add", ".gitattributes");
            repo.RunGit("commit", "-q", "-m", "attributes");
            repo.WriteBytes("crlf.txt", Encoding.ASCII.GetBytes("a\r\nb\r\n"));
            repo.Write("f.txt", "local\n");
            var before = repo.Snapshot();
            var git = repo.Git;
            git.BeforeCommand = MoveBackAfterStashing(repo, "crlf.txt");

            var result = merge ? git.Integrate(UpdateStrategy.Merge, "other") : git.Checkout("other");

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith((merge ? "Merge of other" : "Checkout of other") +
                                    " is not possible, the project is unchanged. " +
                                    "These files stay modified even after stashing:\n  crlf.txt\n", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        private static Action<IReadOnlyList<string>> MoveBackAfterStashing(TestRepository repo, string path)
        {
            var stashed = false;
            return arguments =>
            {
                if (arguments.Count > 1 && arguments[0] == "stash" && arguments[1] == "push")
                {
                    stashed = true;
                }
                else if (stashed && arguments[0] == "status" && arguments.Contains("--ignore-submodules=all"))
                {
                    stashed = false;
                    File.SetLastWriteTimeUtc(Path.Combine(repo.Root, path), DateTime.UtcNow.AddMinutes(-1));
                }
            };
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Switch_ThatWouldOverwriteAnIgnoredFile_Refuses(bool merge)
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write(".gitignore", "*.ign\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("Assets/cfg.ign", "team\n");
            repo.RunGit("add", "-f", "Assets/cfg.ign");
            repo.RunGit("commit", "-q", "-m", "share the config");
            repo.RunGit("switch", "-q", "main");
            repo.Write("Assets/cfg.ign", "mine\n");
            repo.Write("f.txt", "local\n");
            var before = repo.Snapshot();

            var result = merge ? repo.Git.Integrate(UpdateStrategy.Merge, "other") : repo.Git.Checkout("other");

            Assert.AreEqual((merge ? "Merge of other" : "Checkout of other") +
                            " is not possible, it would overwrite these files that Git does not track:\n" +
                            "  Assets/cfg.ign\nMove or delete them and try again", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual("mine\n", repo.Read("Assets/cfg.ign"));
        }

        [Test]
        public void Integrate_AfterAFileWasRemovedFromGitButKeptOnDisk_StillMerges()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("Library/cache.bin", "cache\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("g.txt", "g\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.RunGit("rm", "-q", "--cached", "Library/cache.bin");
            repo.Write(".gitignore", "Library/\n");
            repo.CommitAll("untrack the cache");

            var result = repo.Git.Integrate(UpdateStrategy.Merge, "other");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("cache\n", repo.Read("Library/cache.bin"));
        }

        [Test]
        public void Checkout_WhenAHookFailsAfterTheSwitch_SaysItSwitched()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("o.txt", "o\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("o.txt", "local change\n");
            repo.Write(".git/hooks/post-checkout", "#!/bin/sh\necho 'hook failed' >&2\nexit 1\n");

            var result = repo.Git.Checkout("other");

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("Checked out other, local changes carried over\n" +
                                    "Git reported an error after it, a hook of this repository may have failed:\n",
                result.Message);
            Assert.AreEqual("other", repo.CurrentBranch);
            Assert.AreEqual("local change\n", repo.Read("o.txt"));
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void Rebase_OfLocalMergeCommits_Refuses()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("branch", "other");
            repo.RunGit("switch", "-q", "-c", "side");
            repo.Write("s.txt", "s\n");
            repo.CommitAll("side");
            repo.RunGit("switch", "-q", "main");
            repo.Write("m.txt", "m\n");
            repo.CommitAll("main");
            repo.RunGit("merge", "-q", "--no-ff", "--no-edit", "side");
            repo.RunGit("switch", "-q", "other");
            repo.Write("o.txt", "o\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            var before = repo.Snapshot();

            var result = repo.Git.Integrate(UpdateStrategy.Rebase, "other");

            Assert.AreEqual("Rebase onto other is not possible, the local commits include merge commits " +
                            "that a rebase would flatten, use Merge instead", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
        }

        [Test]
        public void Integrate_WhenAnotherStashAppearsMeanwhile_RestoresItsOwnAndLeavesTheOther()
        {
            using var repo = CreateDivergedBranches();
            repo.Write("o.txt", "o\n");
            repo.CommitAll("o");
            var foreign = CreateForeignStash(repo, "o.txt");
            repo.Write("o.txt", "local\n");
            var git = repo.Git;
            var stored = false;
            git.BeforeCommand = arguments =>
            {
                if (!stored && arguments[0] == "merge")
                {
                    stored = true;
                    repo.RunGit("stash", "store", "-m", "foreign", foreign);
                }
            };

            var result = git.Integrate(UpdateStrategy.Merge, "other");

            Assert.AreEqual("Merged 1 commit from other, local changes restored", result.Message);
            Assert.AreEqual("local\n", repo.Read("o.txt"));
            var stashes = git.Stashes();
            Assert.AreEqual(1, stashes.Count);
            Assert.AreEqual(foreign, stashes[0].Hash);
        }

        [Test]
        public void ApplyStash_WhenAnotherStashAppearsMeanwhile_DropsOnlyTheOneItApplied()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.Write("b.txt", "b\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "mine\n");
            repo.RunGit("stash", "push", "-q", "-m", "mine");
            var foreign = CreateForeignStash(repo, "b.txt");
            var git = repo.Git;
            var stored = false;
            git.BeforeCommand = arguments =>
            {
                if (!stored && arguments.Count > 1 && arguments[0] == "stash" && arguments[1] == "apply")
                {
                    stored = true;
                    repo.RunGit("stash", "store", "-m", "foreign", foreign);
                }
            };

            var result = git.ApplyStash(git.Stashes()[0], drop: true);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("mine\n", repo.Read("a.txt"));
            Assert.AreEqual("b\n", repo.Read("b.txt"));
            var stashes = git.Stashes();
            Assert.AreEqual(1, stashes.Count);
            Assert.AreEqual(foreign, stashes[0].Hash);
        }

        [Test]
        public void ApplyStash_WithAStagedPartWhileTheUserHasStagedFiles_KeepsTheirStaging()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.Write("b.txt", "b\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "staged in the stash\n");
            repo.RunGit("add", "a.txt");
            repo.RunGit("stash", "push", "-q", "-m", "staged");
            repo.Write("b.txt", "staged by the user\n");
            repo.RunGit("add", "b.txt");

            var result = repo.Git.ApplyStash(repo.Git.Stashes()[0], drop: false);

            Assert.AreEqual("stash@{0} applied without the staging", result.Message);
            Assert.AreEqual("staged in the stash\n", repo.Read("a.txt"));
            Assert.AreEqual("b.txt", repo.RunGit("diff", "--cached", "--name-only"));
            Assert.AreEqual(1, repo.Git.Stashes().Count);
        }

        [Test]
        public void ApplyStash_ThatChangesAStagedFileAndThenFails_PutsEverythingBack()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", Lines("a", 10));
            repo.CommitAll("base");
            repo.Write("a.txt", Lines("a", 10).Replace("a line 8", "stashed"));
            repo.Write("u.txt", "from the stash\n");
            repo.RunGit("stash", "push", "-q", "--include-untracked", "-m", "with u");
            repo.Write("a.txt", Lines("a", 10).Replace("a line 1\n", "staged\n"));
            repo.RunGit("add", "a.txt");
            repo.Write("u.txt", "my own file\n");
            var before = repo.Snapshot();

            var result = repo.Git.ApplyStash(repo.Git.Stashes()[0], drop: true);

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("stash@{0} not applied, the project and the stash are unchanged\n", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(1, repo.Git.Stashes().Count);
        }

        [Test]
        public void ApplyStash_OfAFileRenamedSince_RollsBackTheRenamedFileToo()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", Lines("a", 6));
            repo.Write("x.txt", "x\n");
            repo.Write("l.txt", "l\n");
            repo.CommitAll("base");
            repo.Write("a.txt", Lines("a", 6) + "stashed\n");
            repo.Write("x.txt", "x stashed\n");
            repo.RunGit("stash", "push", "-q", "-m", "s");
            repo.RunGit("mv", "a.txt", "c.txt");
            repo.Write("x.txt", "x head\n");
            repo.CommitAll("move");
            repo.Write("l.txt", "local\n");
            var before = repo.Snapshot();

            var result = repo.Git.ApplyStash(repo.Git.Stashes()[0], drop: false);

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("stash@{0} not applied because of conflicts, the project and the stash are unchanged",
                result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(1, repo.Git.Stashes().Count);
        }

        [Test]
        public void ApplyStash_AfterAFileWasTouchedWithoutChanges_StillApplies()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.Write("b.txt", "b\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "mine\n");
            repo.RunGit("stash", "push", "-q", "-m", "mine");
            File.SetLastWriteTimeUtc(Path.Combine(repo.Root, "b.txt"), DateTime.UtcNow.AddMinutes(1));

            var result = repo.Git.ApplyStash(repo.Git.Stashes()[0], drop: true);

            Assert.AreEqual("stash@{0} applied and dropped", result.Message);
            Assert.AreEqual("mine\n", repo.Read("a.txt"));
        }

        [Test]
        public void Branches_WithATagNamedLikeABranch_StillWorkWithTheBranch()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("tag", "release");
            repo.RunGit("switch", "-q", "-c", "release");
            repo.Write("r.txt", "release\n");
            repo.CommitAll("release");
            repo.RunGit("switch", "-q", "main");

            var branch = repo.Git.Branches().First(item => !item.IsRemote && item.Name == "release");
            var checkout = repo.Git.Checkout(branch.Name);
            repo.RunGit("switch", "-q", "main");
            var merge = repo.Git.Integrate(UpdateStrategy.Merge, branch.Ref, branch.Name);

            Assert.AreEqual("refs/heads/release", branch.Ref);
            Assert.AreEqual("Checked out release", checkout.Message);
            Assert.AreEqual("Merged 1 commit from release", merge.Message);
            Assert.AreEqual("release\n", repo.Read("r.txt"));
        }

        [Test]
        public void CheckoutRemote_OfABranchThatExistsLocally_ChecksItOutAndSaysItIsBehind()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = PublishBase(remote);
            upstream.RunGit("switch", "-q", "-c", "feature");
            upstream.Write("feature.txt", "1\n");
            upstream.CommitAll("feature 1");
            upstream.RunGit("push", "-q", "-u", "origin", "feature");
            using var local = remote.Clone("local");
            local.RunGit("switch", "-q", "feature");
            local.RunGit("switch", "-q", "main");
            upstream.Write("feature.txt", "2\n");
            upstream.CommitAll("feature 2");
            upstream.RunGit("push", "-q");
            local.RunGit("fetch", "-q");

            var result = local.Git.CheckoutRemote("origin/feature");

            Assert.AreEqual("Checked out feature\nfeature is 1 commit behind origin/feature, update the project to get them",
                result.Message);
            Assert.AreEqual("feature", local.CurrentBranch);
        }

        [Test]
        public void CheckoutRemote_OfAnotherRemotesBranchWithTheSameName_Refuses()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = PublishBase(remote);
            using var local = remote.Clone("local");
            local.RunGit("remote", "add", "fork", remote.Root);
            local.RunGit("fetch", "-q", "fork");
            local.RunGit("switch", "-q", "-c", "other");
            var before = local.Snapshot();

            var result = local.Git.CheckoutRemote("fork/main");

            Assert.AreEqual("Checkout of fork/main is not possible, the local branch main already exists and does not " +
                            "track it\nCheck out main from the local branches or create a new branch from fork/main",
                result.Message);
            Assert.AreEqual(before, local.Snapshot());
        }

        [Test]
        public void UpdateProject_WhenTheUpstreamWasDeleted_SaysSo()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = PublishBase(remote);
            upstream.RunGit("switch", "-q", "-c", "feature");
            upstream.RunGit("push", "-q", "-u", "origin", "feature");
            using var local = remote.Clone("local");
            local.RunGit("switch", "-q", "feature");
            upstream.RunGit("push", "-q", "origin", "--delete", "feature");

            var result = local.Git.UpdateProject(UpdateStrategy.Merge);

            Assert.AreEqual("The upstream of feature (refs/heads/feature) no longer exists, " +
                            "it was probably deleted on the remote", result.Message);
        }

        [Test]
        public void UndoLastCommit_OfAMergeCommit_Refuses()
        {
            using var repo = CreateDivergedBranches();
            repo.Write("m.txt", "m\n");
            repo.CommitAll("main");
            repo.RunGit("merge", "-q", "--no-ff", "--no-edit", "other");
            var head = repo.Head;

            var result = repo.Git.UndoLastCommit(head);

            Assert.AreEqual("The last commit is a merge, undoing it would stage the merged changes as your own",
                result.Message);
            Assert.AreEqual(head, repo.Head);
        }

        [Test]
        public void UndoLastCommit_OfTheFirstCommit_KeepsItsChangesStaged()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("first");

            var result = repo.Git.UndoLastCommit(repo.Head);

            Assert.AreEqual("First commit undone, its changes are staged", result.Message);
            Assert.AreNotEqual(0, repo.TryRunGit("rev-parse", "-q", "--verify", "HEAD"));
            Assert.AreEqual("refs/heads/main", repo.RunGit("symbolic-ref", "HEAD"));
            Assert.AreEqual("a.txt", repo.RunGit("diff", "--cached", "--name-only"));
        }

        [Test]
        public void UndoLastCommit_OfTheFirstCommitOnADetachedHead_Refuses()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("first");
            repo.RunGit("switch", "-q", "--detach", "HEAD");
            var before = repo.Snapshot();

            var result = repo.Git.UndoLastCommit(repo.Head);

            Assert.AreEqual("The first commit can be undone only on a branch, check out a branch first",
                result.Message);
            Assert.AreEqual(before, repo.Snapshot());
        }

        [Test]
        public void Blame_OfAPathInAnotherCase_FindsTheFile()
        {
            using var repo = TestRepository.Create();
            repo.Write("Assets/Scripts/Foo.cs", "line\n");
            repo.CommitAll("base");

            var (result, lines) = repo.Git.Blame("assets/scripts/foo.cs");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(1, lines.Count);
        }

        [Test]
        public void CreateBranch_BeforeTheFirstCommit_SwitchesToIt()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.RunGit("add", "-A");

            var result = repo.Git.CreateBranch("feat", "HEAD", true);

            Assert.AreEqual("Branch feat created and checked out", result.Message);
            Assert.AreEqual("refs/heads/feat", repo.RunGit("symbolic-ref", "HEAD"));
        }

        [Test]
        public void Integrate_ThatGoesBackHard_KeepsUncommittedWorkInASubmodule()
        {
            using var library = CreateLibrary();
            using var repo = CreateBranchesWithSubmodule(library);
            repo.RunGit("config", "submodule.recurse", "true");
            repo.Write("library/l.txt", "dirty\n");
            repo.Write("f.txt", "local\n");
            var git = repo.Git;
            var indexLock = Path.Combine(repo.Root, ".git", "index.lock");
            git.BeforeCommand = arguments =>
            {
                if (arguments.Count > 1 && arguments[0] == "reset" && arguments[1] == "-q")
                {
                    File.WriteAllText(indexLock, "");
                }
                else if (File.Exists(indexLock))
                {
                    File.Delete(indexLock);
                }
            };

            GitResult result;
            var previous = Git.LockRetryTimeout;
            Git.LockRetryTimeout = TimeSpan.Zero;
            try
            {
                result = git.Integrate(UpdateStrategy.Merge, "other");
            }
            finally
            {
                Git.LockRetryTimeout = previous;
                if (File.Exists(indexLock))
                {
                    File.Delete(indexLock);
                }
            }

            StringAssert.StartsWith("Merge of other rolled back, it conflicts with local changes", result.Message);
            Assert.AreEqual("dirty\n", repo.Read("library/l.txt"));
            Assert.AreEqual("local\n", repo.Read("f.txt"));
            Assert.AreEqual(0, git.Stashes().Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Switch_WithAStuckFileAndAStashedFileSavedAgain_KeepsTheSaveAndTheStash(bool merge)
        {
            using var repo = CreateDivergedBranches();
            repo.WriteBytes("crlf.txt", Encoding.ASCII.GetBytes("a\r\nb\r\n"));
            repo.CommitAll("crlf");
            repo.Write(".gitattributes", "*.txt text eol=lf\n");
            repo.RunGit("add", ".gitattributes");
            repo.RunGit("commit", "-q", "-m", "attributes");
            repo.WriteBytes("crlf.txt", Encoding.ASCII.GetBytes("a\r\nb\r\n"));
            repo.Write("f.txt", "local\n");
            var git = repo.Git;
            var saved = false;
            var moveBack = MoveBackAfterStashing(repo, "crlf.txt");
            git.BeforeCommand = arguments =>
            {
                moveBack(arguments);
                if (!saved && arguments[0] == "status" && arguments.Contains("--ignore-submodules=all"))
                {
                    saved = true;
                    repo.Write("f.txt", "saved again\n");
                }
            };

            var result = merge ? git.Integrate(UpdateStrategy.Merge, "other") : git.Checkout("other");

            StringAssert.StartsWith((merge ? "Merge of other" : "Checkout of other") +
                                    " is not possible, these files stay modified after stashing:\n  crlf.txt\n" +
                                    "Local changes are kept in the stash", result.Message);
            StringAssert.EndsWith("The local changes could not be put back", result.Message);
            Assert.AreEqual("saved again\n", repo.Read("f.txt"));
            Assert.AreEqual("main", repo.CurrentBranch);
            Assert.AreEqual(1, git.Stashes().Count);
        }

        [Test]
        public void Stash_WithAFileThatStaysModified_SaysSo()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "f\n");
            repo.WriteBytes("crlf.txt", Encoding.ASCII.GetBytes("a\r\nb\r\n"));
            repo.CommitAll("crlf");
            repo.Write(".gitattributes", "*.txt text eol=lf\n");
            repo.RunGit("add", ".gitattributes");
            repo.RunGit("commit", "-q", "-m", "attributes");
            repo.WriteBytes("crlf.txt", Encoding.ASCII.GetBytes("a\r\nb\r\n"));
            repo.Write("f.txt", "local\n");
            var git = repo.Git;
            git.BeforeCommand = MoveBackAfterStashing(repo, "crlf.txt");

            var result = git.Stash("mine", false);

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("Changes stashed: mine\nThese files stay modified after stashing:\n  crlf.txt\n",
                result.Message);
            Assert.AreEqual(1, repo.Git.Stashes().Count);
            Assert.AreEqual("f\n", repo.Read("f.txt"));
        }

        [Test]
        public void Commit_OfAFileKeptOnDiskWithAResolvedConflictButNoMerge_Refuses()
        {
            using var repo = CreateDivergedBranches();
            repo.Write("k.txt", "k\n");
            repo.Write("f.txt", "main\n");
            repo.CommitAll("main");
            Assert.AreNotEqual(0, repo.TryRunGit("merge", "-q", "other"));
            repo.RunGit("merge", "--quit");
            repo.Write("f.txt", "resolved\n");
            repo.RunGit("rm", "-q", "--cached", "k.txt");
            var before = repo.Snapshot();

            var result = repo.Git.Commit(new[] { "k.txt", "f.txt" }, "Partial");

            Assert.AreEqual("Committing is not possible, these files have conflicts:\n  f.txt", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
        }

        [Test]
        public void Commit_OfAFileKeptOnDiskWhenAHookFails_ChangesNothing()
        {
            using var repo = TestRepository.Create();
            repo.Write("k.txt", "k\n");
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.RunGit("rm", "-q", "--cached", "k.txt");
            repo.Write(".git/hooks/pre-commit", "#!/bin/sh\necho 'rejected' >&2\nexit 1\n");
            var before = repo.Snapshot();

            var result = repo.Git.Commit(new[] { "k.txt" }, "Untrack k");

            Assert.IsFalse(result.IsSuccess);
            StringAssert.Contains("rejected", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
        }

        [Test]
        public void Integrate_WhoseAbortIsRefusedBecauseOfAFileSavedMeanwhile_StillPutsTheProjectBack()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("m.txt", "m\n");
            repo.Write("o.txt", "o\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.Write("m.txt", "m other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("f.txt", "main\n");
            repo.CommitAll("main");
            var head = repo.Head;
            repo.Write("o.txt", "local change\n");
            var git = repo.Git;
            var saved = false;
            git.BeforeCommand = arguments =>
            {
                if (!saved && arguments[0] == "diff" && arguments.Contains("--diff-filter=U"))
                {
                    saved = true;
                    repo.Write("m.txt", "saved meanwhile\n");
                }
            };

            var result = git.Integrate(UpdateStrategy.Merge, "other");

            StringAssert.StartsWith("Merge of other failed, the project is unchanged\nConflicts:\n  f.txt\n", result.Message);
            StringAssert.EndsWith("\n  m.txt", result.Message);
            Assert.AreNotEqual(0, repo.TryRunGit("rev-parse", "-q", "--verify", "MERGE_HEAD"));
            Assert.AreEqual(head, repo.Head);
            Assert.AreEqual("main", repo.CurrentBranch);
            Assert.AreEqual("m\n", repo.Read("m.txt"));
            Assert.AreEqual("local change\n", repo.Read("o.txt"));
            Assert.AreEqual(1, git.Stashes().Count);
            Assert.AreEqual("saved meanwhile", repo.RunGit("show", "stash@{0}:m.txt"));
        }

        [TestCase(UpdateStrategy.Merge)]
        [TestCase(UpdateStrategy.Rebase)]
        public void Integrate_WhoseAbortIsBlockedByAFileRecreatedMeanwhile_StillPutsTheProjectBack(
            UpdateStrategy strategy)
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("d.txt", "d\n");
            repo.Write("o.txt", "o\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.Delete("d.txt");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("f.txt", "main\n");
            repo.CommitAll("main");
            var head = repo.Head;
            repo.Write("o.txt", "local change\n");
            var git = repo.Git;
            var saved = false;
            git.BeforeCommand = arguments =>
            {
                if (!saved && arguments[0] == "diff" && arguments.Contains("--diff-filter=U"))
                {
                    saved = true;
                    repo.Write("d.txt", "saved meanwhile\n");
                }
            };

            var result = git.Integrate(strategy, "other");

            var name = strategy == UpdateStrategy.Merge ? "Merge of other" : "Rebase onto other";
            StringAssert.StartsWith($"{name} failed, the project is unchanged\nConflicts:\n  f.txt\n", result.Message);
            StringAssert.EndsWith("\n  d.txt", result.Message);
            Assert.IsFalse(repo.Exists(".git/rebase-merge"));
            Assert.AreNotEqual(0, repo.TryRunGit("rev-parse", "-q", "--verify", "MERGE_HEAD"));
            Assert.AreEqual(head, repo.Head);
            Assert.AreEqual("main", repo.CurrentBranch);
            Assert.AreEqual("d\n", repo.Read("d.txt"));
            Assert.AreEqual("local change\n", repo.Read("o.txt"));
            Assert.AreEqual("saved meanwhile", repo.RunGit("show", "stash@{0}:d.txt"));
        }

        [Test]
        public void Integrate_ThatFailsToWriteAFileAfterMergingAnother_StashesTheMergedFile()
        {
            using var repo = TestRepository.Create();
            UseFailingFilter(repo);
            repo.Write("a.txt", "1\n2\n3\n4\n5\n6\n7\n8\n9\n");
            repo.Write("b.bin", "b1\n");
            repo.Write("o.txt", "o\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("a.txt", "1\n2\n3\n4\n5\n6\n7\n8\nnine other\n");
            repo.Write("b.bin", "b2 FAIL\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("a.txt", "one main\n2\n3\n4\n5\n6\n7\n8\n9\n");
            repo.CommitAll("main");
            var head = repo.Head;
            repo.Write("o.txt", "local change\n");

            var result = repo.Git.Integrate(UpdateStrategy.Merge, "other");

            StringAssert.StartsWith("Merge of other failed, the project is unchanged\n", result.Message);
            StringAssert.Contains("These files could not be written and were put back:\n  b.bin\n", result.Message);
            StringAssert.EndsWith("\n  a.txt", result.Message);
            Assert.AreEqual("one main\n2\n3\n4\n5\n6\n7\n8\n9\n", repo.Read("a.txt"));
            Assert.AreEqual("b1\n", repo.Read("b.bin"));
            Assert.AreEqual("local change\n", repo.Read("o.txt"));
            Assert.AreEqual(head, repo.Head);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Integrate_WhenAFileIsSavedRightAfterTheStash_KeepsIt(bool stashedFile)
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("o.txt", "o\n");
            repo.Write("s.txt", "s\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("o.txt", "local change\n");
            var saved = stashedFile ? "o.txt" : "s.txt";
            var git = repo.Git;
            var written = false;
            git.BeforeCommand = arguments =>
            {
                if (!written && arguments[0] == "status" && arguments.Contains("--ignore-submodules=all"))
                {
                    written = true;
                    repo.Write(saved, "saved meanwhile\n");
                }
            };

            var result = git.Integrate(UpdateStrategy.Merge, "other");

            Assert.AreEqual("saved meanwhile\n", repo.Read(saved));
            Assert.AreEqual("other\n", repo.Read("f.txt"));
            if (stashedFile)
            {
                StringAssert.StartsWith("Merged 1 commit from other, but local changes were not restored\n",
                    result.Message);
                Assert.AreEqual(1, git.Stashes().Count);
            }
            else
            {
                Assert.AreEqual("Merged 1 commit from other, local changes restored", result.Message);
                Assert.AreEqual("local change\n", repo.Read("o.txt"));
                Assert.AreEqual(0, git.Stashes().Count);
            }
        }

        [Test]
        public void Integrate_WhoseLocalChangesConflictAfterARename_RollsBack()
        {
            using var repo = TestRepository.Create();
            var script = Lines("line", 20);
            repo.Write("X.cs", script);
            repo.Write("C.txt", "c\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.RunGit("mv", "X.cs", "Y.cs");
            repo.Write("C.txt", "c other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("X.cs", "local first line\n" + script);
            repo.Write("C.txt", "c local\n");
            var before = repo.Snapshot();

            var result = repo.Git.Integrate(UpdateStrategy.Merge, "other");

            Assert.AreEqual("Merge of other rolled back, it conflicts with local changes\nConflicts:\n  C.txt",
                result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void Integrate_ThatWouldOverwriteAnIgnoredFileTheTargetKeptChanging_Refuses()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("cfg.json", "shared\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("cfg.json", "shared changed\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.RunGit("rm", "-q", "--cached", "cfg.json");
            repo.Write(".gitignore", "cfg.json\n");
            repo.CommitAll("untrack cfg");
            repo.Write("cfg.json", "MY LOCAL SECRET\n");
            var before = repo.Snapshot();

            var result = repo.Git.Integrate(UpdateStrategy.Merge, "other");

            Assert.AreEqual("Merge of other is not possible, it would overwrite these files that Git does not track:\n" +
                            "  cfg.json\nMove or delete them and try again", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
        }

        [Test]
        public void Checkout_ThatWouldReplaceAnIgnoredFileWithAFolder_Refuses()
        {
            using var repo = TestRepository.Create();
            repo.Write(".gitignore", "Build\n");
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("Build/run.txt", "run\n");
            repo.RunGit("add", "-f", "Build/run.txt");
            repo.RunGit("commit", "-q", "-m", "other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("Build", "LOCAL BUILD FILE\n");
            var before = repo.Snapshot();

            var result = repo.Git.Checkout("other");

            Assert.AreEqual("Checkout of other is not possible, it would overwrite these files that Git does not track:\n" +
                            "  Build\nMove or delete them and try again", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
        }

        [Test]
        public void Checkout_OfABranchWithASameNamedTag_ChecksAgainstTheBranch()
        {
            using var repo = TestRepository.Create();
            repo.Write(".gitignore", "*.ign\n");
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("tag", "other");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("cfg.ign", "shared\n");
            repo.RunGit("add", "-f", "cfg.ign");
            repo.RunGit("commit", "-q", "-m", "other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("cfg.ign", "MY LOCAL SECRET\n");
            var before = repo.Snapshot();

            var result = repo.Git.Checkout("other");

            Assert.AreEqual("Checkout of other is not possible, it would overwrite these files that Git does not track:\n" +
                            "  cfg.ign\nMove or delete them and try again", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
        }

        [Test]
        public void Checkout_WhenAHookFailsAndTheLocalChangesConflict_RollsBack()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("f.txt", "local change\n");
            repo.Write(".git/hooks/post-checkout", "#!/bin/sh\necho 'hook failed' >&2\nexit 1\n");
            var before = repo.Snapshot();

            var result = repo.Git.Checkout("other");

            StringAssert.StartsWith("Checkout of other rolled back, it conflicts with local changes\nConflicts:\n  f.txt",
                result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, repo.Git.Stashes().Count);
        }

        [Test]
        public void Checkout_WhenAnUntouchedFileIsDeletedWhileItRuns_KeepsTheCheckout()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("u.txt", "u\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            var git = repo.Git;
            var deleted = false;
            git.BeforeCommand = arguments =>
            {
                if (!deleted && arguments[0] == "switch")
                {
                    deleted = true;
                    repo.Delete("u.txt");
                }
            };

            var result = git.Checkout("other");

            Assert.AreEqual("Checked out other", result.Message);
            Assert.AreEqual("other", repo.CurrentBranch);
            Assert.IsFalse(repo.Exists("u.txt"));
        }

        [Test]
        public void Integrate_ThatBringsAFileGitSeesAsChanged_RestoresTheLocalChangesAndSaysSo()
        {
            using var repo = TestRepository.Create();
            repo.Write(".gitattributes", "*.dat text eol=lf\n");
            repo.Write("f.txt", "base\n");
            repo.Write("o.txt", "o\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("p.dat", "a\r\nb\r\n");
            var blob = repo.RunGit("hash-object", "-w", "--no-filters", "p.dat");
            repo.RunGit("update-index", "--add", "--cacheinfo", $"100644,{blob},p.dat");
            repo.RunGit("commit", "-q", "-m", "other");
            repo.RunGit("switch", "-q", "--discard-changes", "main");
            repo.Write("o.txt", "local change\n");
            var git = repo.Git;
            var reads = 0;
            git.BeforeCommand = arguments =>
            {
                if (arguments[0] == "status" && arguments.Contains("--ignore-submodules=all") && ++reads == 2)
                {
                    File.SetLastWriteTimeUtc(Path.Combine(repo.Root, "p.dat"), DateTime.UtcNow.AddMinutes(1));
                }
            };

            var result = git.Integrate(UpdateStrategy.Merge, "other");

            StringAssert.StartsWith("Merged 1 commit from other, local changes restored\n" +
                                    "These files it wrote stay modified:\n  p.dat\n", result.Message);
            Assert.AreEqual("local change\n", repo.Read("o.txt"));
            Assert.AreEqual(0, git.Stashes().Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Switch_ThatBringsAFileGitSeesAsChangedAndConflictsWithLocalChanges_RollsBack(bool merge)
        {
            using var repo = TestRepository.Create();
            repo.Write(".gitattributes", "*.dat text eol=lf\n");
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.Write("p.dat", "a\r\nb\r\n");
            var blob = repo.RunGit("hash-object", "-w", "--no-filters", "p.dat");
            repo.RunGit("add", "f.txt");
            repo.RunGit("update-index", "--add", "--cacheinfo", $"100644,{blob},p.dat");
            repo.RunGit("commit", "-q", "-m", "other");
            repo.RunGit("switch", "-q", "--discard-changes", "main");
            repo.Write("f.txt", "local\n");
            var before = repo.Snapshot();
            var git = repo.Git;
            var reads = 0;
            git.BeforeCommand = arguments =>
            {
                if (arguments[0] == "status" && arguments.Contains("--ignore-submodules=all") && ++reads == 2)
                {
                    File.SetLastWriteTimeUtc(Path.Combine(repo.Root, "p.dat"), DateTime.UtcNow.AddMinutes(1));
                }
            };

            var result = merge ? git.Integrate(UpdateStrategy.Merge, "other") : git.Checkout("other");

            Assert.AreEqual((merge ? "Merge of other" : "Checkout of other") +
                            " rolled back, it conflicts with local changes\nConflicts:\n  f.txt", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(0, git.Stashes().Count);
        }

        [Test]
        public void Merge_OfABranchGivenByItsFullName_WritesTheShortNameInTheCommit()
        {
            using var repo = CreateDivergedBranches();
            repo.Write("m.txt", "m\n");
            repo.CommitAll("main");

            var result = repo.Git.Integrate(UpdateStrategy.Merge, "refs/heads/other", "other");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Merge branch 'other'", repo.RunGit("log", "-1", "--format=%s"));
        }

        [Test]
        public void CanForceDelete_OnlyForABranchThatIsNotCheckedOut()
        {
            using var repo = CreateDivergedBranches();

            Assert.IsTrue(repo.Git.CanForceDelete("other"));
            Assert.IsFalse(repo.Git.CanForceDelete("main"));
            Assert.IsFalse(repo.Git.CanForceDelete("missing"));
        }

        [Test]
        public void CheckoutRemote_WithALocalBranchNamedLikeIt_ChecksOutTheRemoteBranch()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = PublishBase(remote);
            upstream.RunGit("switch", "-q", "-c", "feature");
            upstream.Write("g.txt", "feature\n");
            upstream.CommitAll("feature");
            upstream.RunGit("push", "-q", "origin", "feature");
            using var local = remote.Clone("local");
            local.RunGit("branch", "origin/feature", "main");

            var result = local.Git.CheckoutRemote("refs/remotes/origin/feature");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("refs/heads/feature", local.RunGit("symbolic-ref", "HEAD"));
            Assert.AreEqual("feature\n", local.Read("g.txt"));
        }

        [Test]
        public void CheckoutRemote_OfABranchThatOnlyPushesThere_AdvisesAMerge()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = PublishBase(remote);
            using var local = remote.Clone("local");
            local.RunGit("switch", "-q", "-c", "feature", "--track", "origin/main");
            local.Write("g.txt", "g\n");
            local.CommitAll("g");
            local.RunGit("push", "-q", "origin", "feature");
            upstream.RunGit("fetch", "-q");
            upstream.RunGit("switch", "-q", "-c", "feature", "origin/feature");
            upstream.Write("h.txt", "h\n");
            upstream.CommitAll("h");
            upstream.RunGit("push", "-q", "origin", "feature");
            local.RunGit("switch", "-q", "main");
            local.RunGit("fetch", "-q");

            var result = local.Git.CheckoutRemote("origin/feature");

            Assert.AreEqual("Checked out feature\n" +
                            "feature is 1 commit behind origin/feature, merge origin/feature into it to get them",
                result.Message);
        }

        [Test]
        public void ApplyStash_OfAFileDeletedSince_RollsBackTheConflict()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.Write("l.txt", "l\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "a stashed\n");
            repo.RunGit("stash", "push", "-q", "-m", "my work");
            repo.Delete("a.txt");
            repo.CommitAll("delete");
            repo.Write("l.txt", "local\n");
            var before = repo.Snapshot();

            var result = repo.Git.ApplyStash(repo.Git.Stashes()[0], drop: true);

            StringAssert.StartsWith("stash@{0} not applied because of conflicts, the project and the stash are unchanged",
                result.Message);
            Assert.AreEqual(before, repo.Snapshot());
            Assert.AreEqual(1, repo.Git.Stashes().Count);
            Assert.IsFalse(repo.Exists(".git/vetka-interrupted"));
        }

        [Test]
        public void ApplyStash_ThatFailsAfterMergingIntoARenamedFile_RollsItBackNextToAPathStartingWithR()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", Lines("a", 6));
            repo.Write("README.md", "readme\n");
            repo.Write("l.txt", "l\n");
            repo.CommitAll("base");
            repo.Write("a.txt", Lines("a", 6) + "stashed\n");
            repo.Write("u.txt", "stashed untracked\n");
            repo.RunGit("stash", "push", "-q", "-u", "-m", "s");
            repo.RunGit("mv", "a.txt", "b.txt");
            repo.Write("README.md", "readme head\n");
            repo.CommitAll("move");
            repo.Write("l.txt", "local\n");
            repo.Write("u.txt", "mine\n");
            var before = repo.Snapshot();

            var result = repo.Git.ApplyStash(repo.Git.Stashes()[0], drop: false);

            Assert.IsFalse(result.IsSuccess);
            StringAssert.StartsWith("stash@{0} not applied, the project and the stash are unchanged", result.Message);
            Assert.AreEqual(before, repo.Snapshot());
        }

        [Test]
        public void ApplyStash_WhenAnotherStashIsAddedBeforeTheDrop_DropsOnlyItsOwnEntry()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.Write("b.txt", "b\n");
            repo.CommitAll("base");
            var foreign = CreateForeignStash(repo, "b.txt");
            repo.Write("a.txt", "mine\n");
            repo.RunGit("stash", "push", "-q", "-m", "mine");
            var git = repo.Git;
            var stored = false;
            git.BeforeCommand = arguments =>
            {
                if (!stored && arguments.Count > 1 && arguments[0] == "stash" && arguments[1] == "drop")
                {
                    stored = true;
                    repo.RunGit("stash", "store", "-m", "foreign", foreign);
                }
            };

            var result = git.ApplyStash(git.Stashes()[0], drop: true);

            Assert.AreEqual("stash@{0} applied and dropped", result.Message);
            var stashes = git.Stashes();
            Assert.AreEqual(1, stashes.Count);
            Assert.AreEqual(foreign, stashes[0].Hash);
            Assert.AreEqual("mine\n", repo.Read("a.txt"));
        }

        [Test]
        public void Stash_WithRepeatedSpacesInTheMessage_FindsItsEntry()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "mine\n");

            var result = repo.Git.Stash("fix  the\tbug", false);

            Assert.AreEqual("Changes stashed: fix  the\tbug", result.Message);
            Assert.AreEqual(1, repo.Git.Stashes().Count);
            Assert.AreEqual("a\n", repo.Read("a.txt"));
        }

        [Test]
        public void Rollback_BeforeTheFirstCommit_UnstagesAFileChangedAfterAdding()
        {
            using var repo = TestRepository.Create();
            repo.Write("Library/a.txt", "a\n");
            repo.Write("Library/b.txt", "b\n");
            repo.RunGit("add", "-A");
            repo.Write("Library/a.txt", "a changed\n");

            var result = repo.Git.Rollback(new[]
            {
                new GitFileChange(GitStatus.Added, "Library/a.txt"),
                new GitFileChange(GitStatus.Added, "Library/b.txt")
            }, false);

            Assert.AreEqual("2 files rolled back", result.Message);
            Assert.AreEqual("", repo.RunGit("diff", "--cached", "--name-only"));
            Assert.AreEqual("a changed\n", repo.Read("Library/a.txt"));
        }

        [Test]
        public void Rollback_OfANewFolderSelectedAfterItsContent_DeletesTheFolder()
        {
            using var repo = TestRepository.Create();
            repo.Write("f.txt", "f\n");
            repo.CommitAll("base");
            repo.Write("Assets/New/x.cs", "x\n");
            repo.Write("Assets/New/x.cs.meta", "guid: x\n");
            repo.Write("Assets/New.meta", "guid: new\n");

            var result = repo.Git.Rollback(new[]
            {
                new GitFileChange(GitStatus.Untracked, "Assets/New/x.cs"),
                new GitFileChange(GitStatus.Untracked, "Assets/New/x.cs.meta"),
                new GitFileChange(GitStatus.Untracked, "Assets/New.meta")
            }, true);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.IsFalse(repo.Exists("Assets/New"));
            Assert.IsFalse(repo.Exists("Assets/New.meta"));
        }

        [Test]
        public void Rollback_OfAConflictWithAFileDeletedInHead_UnstagesIt()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("a.txt", "a other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Delete("a.txt");
            repo.CommitAll("delete");
            Assert.AreNotEqual(0, repo.TryRunGit("merge", "-q", "other"));
            repo.RunGit("merge", "--quit");

            var result = repo.Git.Rollback(new[] { new GitFileChange(GitStatus.Unmerged, "a.txt") }, true);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("", repo.RunGit("status", "--porcelain"));
            Assert.IsFalse(repo.Exists("a.txt"));
        }

        [Test]
        public void Commit_OfARevertInProgress_FinishesIt()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "a\n");
            repo.CommitAll("base");
            repo.Write("a.txt", "a changed\n");
            repo.CommitAll("change");
            repo.RunGit("revert", "--no-commit", "HEAD");

            var result = repo.Git.Commit(new[] { "a.txt" }, "Revert the change");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("a\n", repo.RunGit("show", "HEAD:a.txt") + "\n");
            Assert.AreNotEqual(0, repo.TryRunGit("rev-parse", "-q", "--verify", "REVERT_HEAD"));
        }

        private static string CreateForeignStash(TestRepository repo, string path)
        {
            var content = repo.Read(path);
            repo.Write(path, "foreign\n");
            var hash = repo.RunGit("stash", "create");
            repo.Write(path, content);
            return hash;
        }

        private static void UseFailingFilter(TestRepository repo)
        {
            repo.RunGit("config", "filter.fail.clean", "cat");
            repo.RunGit("config", "filter.fail.smudge",
                "c=$(cat); case \"$c\" in *FAIL*) echo 'object unavailable' >&2; exit 1;; esac; printf '%s\\n' \"$c\"");
            repo.RunGit("config", "filter.fail.required", "true");
            repo.Write(".gitattributes", "*.bin filter=fail\n");
        }

        private static TestRepository CreateStashConflict()
        {
            var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("g.txt", "g\n");
            repo.CommitAll("base");
            repo.Write("f.txt", "stashed\n");
            repo.RunGit("stash", "push", "-q");
            repo.Write("f.txt", "head\n");
            repo.CommitAll("head");
            repo.Write("g.txt", "g changed\n");
            Assert.AreNotEqual(0, repo.TryRunGit("stash", "pop"));
            return repo;
        }

        private static bool IsMergeAbort(IReadOnlyList<string> arguments) =>
            arguments.Count == 2 && arguments[0] == "merge" && arguments[1] == "--abort";

        private static TestRepository CreateBranchesWithANativePlugin(bool localCommit)
        {
            var repo = TestRepository.Create();
            repo.Write("Assets/Plugins/native.dll", "native 1\n");
            repo.Write("Assets/a.txt", "a\n");
            repo.Write("Assets/local.txt", "l\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("Assets/Plugins/native.dll", "native 2\n");
            repo.Write("Assets/a.txt", "other\n");
            repo.Write("Assets/new.txt", "new\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            if (localCommit)
            {
                repo.Write("Assets/m.txt", "m\n");
                repo.CommitAll("main");
            }

            return repo;
        }

        private static TestRepository CreateLocalChangesOfEveryKind()
        {
            var repo = TestRepository.Create();
            repo.Write("Assets/Plugins/native.dll", "native 1\n");
            repo.Write("Assets/staged.txt", "staged 1\n");
            repo.Write("Assets/deleted.txt", "deleted\n");
            repo.Write("Assets/Removed/removed.txt", "removed\n");
            repo.Write("Assets/kept.txt", "kept 1\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("Assets/other.txt", "other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");

            repo.Write("Assets/Plugins/native.dll", "native local\n");
            repo.Write("Assets/staged.txt", "staged 2\n");
            repo.RunGit("add", "Assets/staged.txt");
            repo.Write("Assets/staged.txt", "staged 2 and edited\n");
            repo.Write("Assets/added.txt", "added\n");
            repo.RunGit("add", "Assets/added.txt");
            repo.Delete("Assets/deleted.txt");
            repo.RunGit("rm", "-q", "Assets/Removed/removed.txt");
            repo.Write("Assets/kept.txt", "kept local\n");
            repo.RunGit("rm", "-q", "--cached", "Assets/kept.txt");
            repo.Write("Assets/untracked.txt", "untracked\n");
            return repo;
        }

        private static TestRepository CreateDivergedBranchesWithLocalChange()
        {
            var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.Write("o.txt", "o\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            repo.Write("f.txt", "main\n");
            repo.CommitAll("main");
            repo.Write("o.txt", "local change\n");
            return repo;
        }

        private static TestRepository CreateLibrary()
        {
            var library = TestRepository.Create("library");
            library.Write("l.txt", "l\n");
            library.CommitAll("library");
            return library;
        }

        private static TestRepository CreateBranchesWithSubmodule(TestRepository library)
        {
            var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("-c", "protocol.file.allow=always", "submodule", "add", "-q", library.Root, "library");
            repo.CommitAll("library");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            return repo;
        }

        private static void StashUntrackedFile(TestRepository repo, string message)
        {
            repo.Write("stashed.txt", "stashed\n");
            repo.RunGit("stash", "push", "-q", "--include-untracked", "-m", message);
        }

        private static void AssertOnlyStash(TestRepository repo, string message)
        {
            var stashes = repo.Git.Stashes();
            Assert.AreEqual(1, stashes.Count);
            StringAssert.EndsWith(message, stashes[0].Message);
            Assert.IsFalse(repo.Exists("stashed.txt"));
        }

        private static void StageAndEdit(TestRepository repo, string path)
        {
            repo.Write(path, "staged\n2\n3\n4\n5\n");
            repo.RunGit("add", path);
            repo.Write(path, "staged\n2\n3\n4\nunstaged\n");
        }

        private static TestRepository CreateDivergedBranchesWithMergeInProgress()
        {
            var repo = CreateDivergedBranches();
            repo.RunGit("switch", "-q", "-c", "side");
            repo.Write("side.txt", "side\n");
            repo.CommitAll("side");
            repo.RunGit("switch", "-q", "main");
            repo.RunGit("merge", "-q", "--no-commit", "--no-ff", "side");
            return repo;
        }

        private static TestRepository PublishBase(TestRepository remote)
        {
            var upstream = remote.Clone("upstream");
            upstream.Write("f.txt", "base\n");
            upstream.Write("o.txt", "o\n");
            upstream.CommitAll("base");
            upstream.RunGit("push", "-q", "-u", "origin", "main");
            return upstream;
        }

        private static TestRepository CreateDivergedBranches()
        {
            var repo = TestRepository.Create();
            repo.Write("f.txt", "base\n");
            repo.CommitAll("base");
            repo.RunGit("switch", "-q", "-c", "other");
            repo.Write("f.txt", "other\n");
            repo.CommitAll("other");
            repo.RunGit("switch", "-q", "main");
            return repo;
        }
    }
}
