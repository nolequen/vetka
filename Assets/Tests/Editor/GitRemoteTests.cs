using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Upwake.Vetka.Tests
{
    internal class GitRemoteTests
    {
        [Test]
        public void Push_NewBranch_SetsUpstreamAndMarksCommitsAsPushed()
        {
            using var remote = TestRepository.CreateBare();
            using var local = remote.PublishBase();
            local.RunGit("switch", "-q", "-c", "feature");
            local.Write("feature.txt", "feature\n");
            local.CommitAll("feature commit");
            var git = local.Git;

            Assert.IsFalse(git.IsHeadPushed());
            Assert.AreEqual(1, git.OutgoingCommits(git.PushTarget()).Count);

            var result = git.Push();

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Pushed 1 commit to new branch origin/feature", result.Message);
            Assert.AreEqual("origin/feature", git.UpstreamBranch());
            Assert.AreEqual(local.Head, remote.RunGit("rev-parse", "feature"));
            Assert.IsTrue(git.IsHeadPushed());
            Assert.AreEqual(0, git.OutgoingCommits(git.PushTarget()).Count);
            Assert.IsTrue(git.ReadLogPage(null, 0, 50).Commits.All(commit => commit.IsPushed));

            local.Write("feature.txt", "unpushed\n");
            local.CommitAll("unpushed commit");
            var log = git.ReadLogPage(null, 0, 50).Commits;

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
            using var local = remote.PublishBase();
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
        public void ReadLogPage_OfAnotherBranch_MarksCommitsByThatBranchsRemote()
        {
            using var remote = TestRepository.CreateBare();
            using var local = remote.PublishBase();
            var git = local.Git;
            local.RunGit("switch", "-q", "-c", "feature");
            local.Write("feature.txt", "pushed\n");
            local.CommitAll("pushed feature");
            Assert.IsTrue(git.Push().IsSuccess);
            local.Write("feature.txt", "local\n");
            local.CommitAll("local feature");
            var tip = local.Head;
            local.RunGit("switch", "-q", "main");

            var branch = git.ReadLogPage(tip, 0, 2, git.PushTargetOf("refs/heads/feature"));
            var tracking = git.ReadLogPage(git.CommitOf("refs/remotes/origin/feature"), 0, 1,
                git.PushTargetOf("refs/remotes/origin/feature"));

            CollectionAssert.AreEqual(new[] { false, true }, branch.Commits.Select(commit => commit.IsPushed));
            CollectionAssert.AreEqual(new[] { true }, tracking.Commits.Select(commit => commit.IsPushed));
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
        public void Push_WithTheOnlyRemoteNotNamedOrigin_UsesIt()
        {
            using var remote = TestRepository.CreateBare();
            using var local = remote.PublishBase();
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
            using var local = remote.PublishBase();
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
            using var local = remote.PublishBase();
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
        public void LocalIdentity_IsReadAndWritten()
        {
            using var repo = TestRepository.Create();
            var git = repo.Git;

            var identity = git.Identities().Local;
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

            var identity = repo.Git.Identities().Local;

            Assert.AreEqual("Included Name", identity.Name);
            Assert.AreEqual("included@example.com", identity.Email);
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
            StringAssert.StartsWith("branch 'missing' not found", result.Message);
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

            var before = git.Identities().Global;
            var result = git.SetIdentity(new GitIdentity("Global Name", "global@example.com"), global: true);

            Assert.AreEqual("", before.Name);
            Assert.AreEqual("", before.Email);
            Assert.IsTrue(result.IsSuccess, result.Message);
            StringAssert.Contains("global@example.com", File.ReadAllText(repo.GlobalConfig));
            Assert.AreEqual("Global Name", git.Identities().Global.Name);
            Assert.AreEqual("Test", git.Identities().Local.Name);
        }

        [Test]
        public void Push_BehindTheRemote_IsRejectedWithAShortMessage()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.PushChange("f.txt", "theirs");
            local.Write("g.txt", "mine\n");
            local.CommitAll("mine");

            var result = local.Git.Push();

            Assert.IsFalse(result.IsSuccess);
            Assert.IsTrue(result.IsRejected);
            Assert.AreEqual("Push rejected: origin/main has commits that main does not have", result.Message);
        }

        [Test]
        public void ForcePush_ToABranchDeletedOnTheRemote_SaysSoAndForgetsIt()
        {
            using var remote = TestRepository.CreateBare();
            using var local = remote.PublishBase();
            local.RunGit("switch", "-q", "-c", "feature");
            local.Write("f.txt", "one\n");
            local.CommitAll("one");
            local.Write("f.txt", "two\n");
            local.CommitAll("two");
            local.RunGit("push", "-q", "-u", "origin", "feature");
            var git = local.Git;
            Assert.IsTrue(git.UndoLastCommit(local.Head).IsSuccess);
            var seen = git.RemoteOnlyCommits(git.PushTarget().Value, 10);
            remote.RunGit("branch", "-D", "feature");

            var result = git.Push(seen.Tracked, seen.Head);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Force push stopped: origin/feature no longer exists on the remote, push again to create it",
                result.Message);
            Assert.IsFalse(git.PushTarget().Value.Exists);
        }

        [Test]
        public void ForcePush_AfterUndoingAPushedCommit_RemovesItFromTheRemote()
        {
            using var remote = TestRepository.CreateBare();
            using var local = remote.PublishBase();
            var start = local.Head;
            local.Write("f.txt", "pushed by mistake\n");
            local.CommitAll("mistake");
            local.RunGit("push", "-q");
            var git = local.Git;
            Assert.IsTrue(git.UndoLastCommit(local.Head).IsSuccess);
            var target = git.PushTarget().Value;

            var seen = git.RemoteOnlyCommits(target, 10);
            var result = git.Push(seen.Tracked);

            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual("mistake", seen.Commits[0].Subject);
            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Force pushed to origin/main: 1 commit removed", result.Message);
            Assert.AreEqual(start, remote.RunGit("rev-parse", "main"));
            Assert.AreEqual(0, git.RemoteOnlyCommits(target, 10).Count);
        }

        [Test]
        public void ForcePush_AfterTheBranchMovedSinceItWasShown_ChangesNothing()
        {
            using var remote = TestRepository.CreateBare();
            using var local = remote.PublishBase();
            local.Write("f.txt", "one\n");
            local.CommitAll("one");
            local.PushChange("f.txt", "two");
            var git = local.Git;
            Assert.IsTrue(git.UndoLastCommit(local.Head).IsSuccess);
            var seen = git.RemoteOnlyCommits(git.PushTarget().Value, 10);
            Assert.IsTrue(git.UndoLastCommit(local.Head).IsSuccess);
            var remoteHead = remote.RunGit("rev-parse", "main");

            var result = git.Push(seen.Tracked, seen.Head);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Force push stopped: main has changed since the Push window showed it, look at it again",
                result.Message);
            Assert.AreEqual(remoteHead, remote.RunGit("rev-parse", "main"));
        }

        [Test]
        public void ForcePush_ReplacesTheirCommitsWithYours()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.PushChange("f.txt", "theirs");
            local.RunGit("fetch", "-q");
            local.Write("g.txt", "mine\n");
            local.CommitAll("mine");
            var git = local.Git;

            var seen = git.RemoteOnlyCommits(git.PushTarget().Value, 10);
            var result = git.Push(seen.Tracked);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Force pushed to origin/main: 1 commit pushed, 1 commit removed", result.Message);
            Assert.AreEqual(local.Head, remote.RunGit("rev-parse", "main"));
        }

        [Test]
        public void ForcePush_WhenTheRemoteMovedSinceItWasShown_ChangesNothing()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.PushChange("f.txt", "theirs");
            local.RunGit("fetch", "-q");
            local.Write("g.txt", "mine\n");
            local.CommitAll("mine");
            var git = local.Git;
            var seen = git.RemoteOnlyCommits(git.PushTarget().Value, 10);
            upstream.PushChange("f.txt", "theirs again");
            var remoteHead = remote.RunGit("rev-parse", "main");

            var result = git.Push(seen.Tracked);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Force push rejected: origin/main has changed on the remote since the Push window showed it. " +
                            "The window now shows its current state, check it and try again", result.Message);
            Assert.AreEqual(remoteHead, remote.RunGit("rev-parse", "main"));
            Assert.AreEqual("theirs again", git.RemoteOnlyCommits(git.PushTarget().Value, 10).Commits[0].Subject);
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
        public void CreateTag_WithoutAMessage_MakesALightweightTagEvenWhenTheUserSignsTags()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "1\n");
            repo.CommitAll("first");
            repo.RunGit("config", "tag.gpgSign", "true");
            var head = repo.Short("HEAD");

            var result = repo.Git.CreateTag("v1", head, "  ");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual($"Tag v1 created on {head}", result.Message);
            Assert.AreEqual("commit", repo.RunGit("cat-file", "-t", "refs/tags/v1"));
            Assert.AreEqual(repo.Head, repo.RunGit("rev-parse", "v1"));
        }

        [Test]
        public void CreateTag_WithAMessage_MakesAnAnnotatedTagThatKeepsTheMessage()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "1\n");
            repo.CommitAll("first");
            var head = repo.Short("HEAD");

            var result = repo.Git.CreateTag(" v1 ", head, "Release 1\n\n# not a comment\n");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("tag", repo.RunGit("cat-file", "-t", "refs/tags/v1"));
            Assert.AreEqual(repo.Head, repo.RunGit("rev-parse", "v1^{commit}"));
            Assert.AreEqual("Release 1\n\n# not a comment",
                repo.RunGit("for-each-ref", "--format=%(contents)", "refs/tags/v1").Trim());
        }

        [TestCase("bad name")]
        [TestCase("-x")]
        [TestCase("a..b")]
        [TestCase("HEAD")]
        public void CreateTag_WithAnInvalidName_Refuses(string name)
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "1\n");
            repo.CommitAll("first");

            var result = repo.Git.CreateTag(name, repo.Head, "");

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual($"'{name}' is not a valid tag name", result.Message);
            Assert.AreEqual("", repo.RunGit("tag", "-l"));
        }

        [Test]
        public void CreateTag_ThatAlreadyExists_ChangesNothing()
        {
            using var repo = TestRepository.Create();
            repo.Write("a.txt", "1\n");
            repo.CommitAll("first");
            repo.RunGit("tag", "v1");
            var first = repo.Head;
            repo.Write("a.txt", "2\n");
            repo.CommitAll("second");

            var result = repo.Git.CreateTag("v1", repo.Head, "moved");

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Tag v1 already exists", result.Message);
            Assert.AreEqual(first, repo.RunGit("rev-parse", "v1"));
        }

        [Test]
        public void DeleteTag_OnlyHere_KeepsItOnTheRemote()
        {
            using var remote = TestRepository.CreateBare();
            using var local = remote.PublishBase();
            local.RunGit("tag", "v1");
            local.RunGit("push", "-q", "origin", "v1");

            var result = local.Git.DeleteTag("v1", null);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Tag v1 deleted", result.Message);
            Assert.AreEqual("", local.RunGit("tag", "-l"));
            Assert.AreEqual("v1", remote.RunGit("tag", "-l"));
        }

        [Test]
        public void DeleteTag_HereAndOnTheRemote_RemovesBoth()
        {
            using var remote = TestRepository.CreateBare();
            using var local = remote.PublishBase();
            local.RunGit("tag", "-a", "-m", "release", "v1");
            local.RunGit("push", "-q", "origin", "v1");

            var result = local.Git.DeleteTag("v1", "origin");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Tag v1 deleted here and on origin", result.Message);
            Assert.AreEqual("", local.RunGit("tag", "-l"));
            Assert.AreEqual("", remote.RunGit("tag", "-l"));
        }

        [Test]
        public void DeleteTag_OnTheRemoteWithoutABranchCheckedOut_UsesTheDefaultRemote()
        {
            using var remote = TestRepository.CreateBare();
            using var local = remote.PublishBase();
            local.RunGit("tag", "v1");
            local.RunGit("push", "-q", "origin", "v1");
            local.RunGit("switch", "-q", "--detach");

            var found = local.Git.TagRemote();
            var result = local.Git.DeleteTag("v1", found.Remote);

            Assert.AreEqual("origin", found.Remote);
            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("", remote.RunGit("tag", "-l"));
        }

        [Test]
        public void DeleteTag_OnTheRemote_DoesNotSendOtherTagsWhenTheUserFollowsTags()
        {
            using var remote = TestRepository.CreateBare();
            using var local = remote.PublishBase();
            local.RunGit("tag", "old");
            local.RunGit("push", "-q", "origin", "old");
            local.RunGit("tag", "-a", "-m", "private", "secret");
            local.RunGit("config", "push.followTags", "true");

            var result = local.Git.DeleteTag("old", "origin");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("", remote.RunGit("tag", "-l"));
        }

        [Test]
        public void DeleteTag_OnTheRemoteWhereItIsMissing_SaysSoAndDeletesItHere()
        {
            using var remote = TestRepository.CreateBare();
            using var local = remote.PublishBase();
            local.RunGit("tag", "v1");

            var result = local.Git.DeleteTag("v1", "origin");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Tag v1 deleted, it was not on origin", result.Message);
            Assert.AreEqual("", local.RunGit("tag", "-l"));
        }

        [Test]
        public void Push_WithTagsOfTheCurrentBranch_SendsAnnotatedTagsOnThePushedCommits()
        {
            using var remote = TestRepository.CreateBare();
            using var local = remote.PublishBase();
            local.Write("f.txt", "next\n");
            local.CommitAll("next");
            local.RunGit("tag", "-a", "-m", "release", "v1");
            local.RunGit("tag", "light");

            var result = local.Git.Push(tags: PushTags.CurrentBranch);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Pushed 1 commit and 1 tag to origin/main", result.Message);
            Assert.AreEqual("v1", remote.RunGit("tag", "-l"));
        }

        [Test]
        public void Push_WithAllTags_SendsThemEvenWithoutNewCommits()
        {
            using var remote = TestRepository.CreateBare();
            using var local = remote.PublishBase();
            local.RunGit("tag", "-a", "-m", "release", "v1");
            local.RunGit("tag", "light");

            var result = local.Git.Push(tags: PushTags.All);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual("Pushed 2 tags to origin", result.Message);
            CollectionAssert.AreEquivalent(new[] { "light", "v1" },
                remote.RunGit("tag", "-l").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
        }

        [Test]
        public void Push_WithATagTheRemoteHasOnAnotherCommit_PushesTheCommitsAndNamesTheTag()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.RunGit("tag", "v1");
            upstream.RunGit("push", "-q", "origin", "v1");
            using var local = remote.Clone("local");
            local.RunGit("tag", "-d", "v1");
            local.Write("g.txt", "mine\n");
            local.CommitAll("mine");
            local.RunGit("tag", "v1");

            var result = local.Git.Push(tags: PushTags.All);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("Pushed 1 commit to origin/main, but origin already has tag v1 on another commit",
                result.Message);
            Assert.AreEqual(local.Head, remote.RunGit("rev-parse", "main"));
            Assert.AreEqual(upstream.Head, remote.RunGit("rev-parse", "v1"));
        }

        [Test]
        public void Push_WithARefusedTagAndAFetchRefspecThatSkipsTheBranch_RecordsThePushedCommits()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.RunGit("tag", "v1");
            upstream.RunGit("push", "-q", "origin", "v1");
            using var local = remote.Clone("local");
            local.RunGit("config", "remote.origin.fetch", "+refs/heads/main:refs/remotes/origin/main");
            local.RunGit("tag", "-d", "v1");
            local.RunGit("switch", "-q", "-c", "feature");
            local.Write("g.txt", "mine\n");
            local.CommitAll("mine");
            local.RunGit("tag", "v1");

            var result = local.Git.Push(tags: PushTags.All);

            Assert.IsTrue(result.IsDone, result.Message);
            Assert.AreEqual("Pushed 1 commit to new branch origin/feature, but origin already has tag v1 on another commit",
                result.Message);
            Assert.AreEqual(local.Head, local.RunGit("rev-parse", "refs/remotes/origin/feature"));
            Assert.AreEqual(0, local.Git.OutgoingCommits(local.Git.PushTarget()).Count);
        }

        [TestCase(PushTags.CurrentBranch)]
        [TestCase(PushTags.All)]
        public void Push_OfTagsOnlyWhileTheRemoteIsAhead_ReportsTheTagsAndOffersNoUpdate(PushTags mode)
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.PushChange("f.txt", "theirs");
            local.RunGit("tag", "-a", "-m", "release", "v1");
            local.RunGit("fetch", "-q");
            var mine = local.Head;

            var result = local.Git.Push(tags: mode);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.IsFalse(result.IsRejected);
            Assert.AreEqual("Pushed 1 tag to origin", result.Message);
            Assert.AreEqual("v1", remote.RunGit("tag", "-l"));
            Assert.AreEqual(mine, local.Head);
        }

        [Test]
        public void Push_OfATagTheServerRefusesWhileTheRemoteIsAhead_ShowsTheServerError()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.PushChange("f.txt", "theirs");
            remote.Write("hooks/pre-receive",
                "#!/bin/sh\nwhile read old new ref; do case \"$ref\" in refs/tags/*) " +
                "echo 'protected tag' >&2; exit 1;; esac; done\nexit 0\n");
            local.RunGit("tag", "-a", "-m", "release", "v1");
            local.RunGit("fetch", "-q");

            var result = local.Git.Push(tags: PushTags.All);

            Assert.IsFalse(result.IsSuccess);
            Assert.IsFalse(result.IsRejected);
            StringAssert.Contains("protected tag", result.Message);
            Assert.AreEqual("", remote.RunGit("tag", "-l"));
        }

        [Test]
        public void Push_RejectedBecauseOfNewCommits_SaysWhichTagsWentAnyway()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.PushChange("f.txt", "theirs");
            local.RunGit("tag", "base-tag");
            local.Write("g.txt", "mine\n");
            local.CommitAll("mine");

            var result = local.Git.Push(tags: PushTags.All);

            Assert.IsTrue(result.IsRejected);
            Assert.AreEqual("Push rejected: origin/main has commits that main does not have. 1 tag pushed anyway",
                result.Message);
        }

        [Test]
        public void Push_WithATagConflictAndABranchRefusedByTheServer_ShowsTheServerError()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.RunGit("tag", "v1");
            upstream.RunGit("push", "-q", "origin", "v1");
            remote.Write("hooks/pre-receive",
                "#!/bin/sh\nwhile read old new ref; do case \"$ref\" in refs/heads/*) " +
                "echo 'protected branch' >&2; exit 1;; esac; done\nexit 0\n");
            using var local = remote.Clone("local");
            local.RunGit("tag", "-d", "v1");
            local.Write("g.txt", "mine\n");
            local.CommitAll("mine");
            local.RunGit("tag", "v1");

            var result = local.Git.Push(tags: PushTags.All);

            Assert.IsFalse(result.IsSuccess);
            StringAssert.Contains("protected branch", result.Message);
            StringAssert.DoesNotStartWith("Tags not pushed", result.Message);
            Assert.AreEqual(upstream.Head, remote.RunGit("rev-parse", "main"));
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
    }
}
