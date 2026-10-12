using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Upwake.Vetka.Tests
{
    internal class IncomingFetchTests
    {
        private static GitIncoming Read(Git git, bool network = true) =>
            git.ReadIncoming(null, network, UpdateStrategyChoice.Merge, 100, 1000);

        [Test]
        public void FetchUpstream_TakesOnlyTheUpstreamWithoutTags()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.PushChange("f.txt", "theirs");
            upstream.RunGit("tag", "-a", "v9", "-m", "v9");
            upstream.RunGit("push", "-q", "origin", "v9");
            upstream.RunGit("push", "-q", "origin", "HEAD:refs/heads/other");

            var fetched = local.Git.FetchUpstream();

            Assert.IsTrue(fetched.IsSuccess, fetched.Message);
            Assert.AreEqual("", fetched.Output);
            Assert.AreEqual(upstream.Head, local.RunGit("rev-parse", "origin/main"));
            Assert.AreEqual("", local.RunGit("for-each-ref", "refs/remotes/origin/other", "refs/tags"));
        }

        [Test]
        public void FetchUpstream_AfterAForcePush_SaysTheUpstreamWasRewritten()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.PushChange("f.txt", "first");
            using var local = remote.Clone("local");
            upstream.RunGit("reset", "-q", "--hard", "HEAD~1");
            upstream.Write("f.txt", "rewritten\n");
            upstream.CommitAll("rewritten");
            upstream.RunGit("push", "-q", "--force");

            var fetched = local.Git.FetchUpstream();

            Assert.AreEqual("origin/main was rewritten on the remote: it no longer has 1 commit it had before. " +
                            "Update Project keeps those that main has", fetched.Output);
        }

        [Test]
        public void FetchUpstream_WhenTheUpstreamWasDeleted_ForgetsItAndTheWindowSaysSo()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.RunGit("push", "-q", "origin", "HEAD:refs/heads/feature");
            using var local = remote.Clone("local");
            local.RunGit("switch", "-q", "feature");
            upstream.RunGit("push", "-q", "origin", "--delete", "feature");

            var fetched = local.Git.FetchUpstream();
            var incoming = Read(local.Git);

            Assert.IsTrue(fetched.IsSuccess, fetched.Message);
            Assert.AreEqual("", local.RunGit("for-each-ref", "refs/remotes/origin/feature"));
            Assert.AreEqual("The upstream of feature (refs/heads/feature) no longer exists, it was probably deleted " +
                            "on the remote", incoming.Result.Message);
        }

        [Test]
        public void Incoming_ForAnUpstreamThatWasNeverFetched_FetchesItFirst()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            upstream.RunGit("push", "-q", "origin", "HEAD:refs/heads/feature");
            local.RunGit("switch", "-q", "-c", "feature", "--no-track");
            local.RunGit("config", "branch.feature.remote", "origin");
            local.RunGit("config", "branch.feature.merge", "refs/heads/feature");
            upstream.PushChange("f.txt", "theirs");
            upstream.RunGit("push", "-q", "origin", "HEAD:refs/heads/feature");

            var fetched = local.Git.FetchUpstream();
            var incoming = Read(local.Git);

            Assert.IsTrue(fetched.IsSuccess, fetched.Message);
            Assert.IsTrue(incoming.Result.IsSuccess, incoming.Result.Message);
            Assert.AreEqual("origin/feature", incoming.Upstream);
            Assert.AreEqual(1, incoming.IncomingCount);
        }

        [Test]
        public void Incoming_UpstreamStates_HaveTheirOwnTexts()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.RunGit("tag", "v1");
            upstream.RunGit("push", "-q", "origin", "v1");
            using var local = remote.Clone("local");

            local.RunGit("switch", "-q", "-c", "lonely");
            Assert.AreEqual("Branch lonely has no upstream to update from", Read(local.Git).Result.Message);

            local.RunGit("config", "branch.lonely.remote", "origin");
            local.RunGit("config", "branch.lonely.merge", "refs/tags/v1");
            Assert.AreEqual("lonely tracks refs/tags/v1, which is not a branch, choose a branch as its upstream",
                Read(local.Git).Result.Message);
            Assert.AreEqual(Read(local.Git).Result.Message, local.Git.UpdateProject(UpdateStrategy.Merge).Message);

            local.RunGit("config", "branch.lonely.remote", remote.Root);
            local.RunGit("config", "branch.lonely.merge", "refs/heads/main");
            Assert.AreEqual($"lonely tracks a remote that is not configured ({remote.Root}), set its upstream again",
                Read(local.Git).Result.Message);

            local.RunGit("switch", "-q", "--detach");
            Assert.AreEqual("Cannot show incoming changes: no branch is checked out", Read(local.Git).Result.Message);
        }

        [Test]
        public void Incoming_FromALocalBranch_DoesNotFetch()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            local.RunGit("switch", "-q", "-c", "dev");
            local.Write("dev.txt", "dev\n");
            local.CommitAll("dev");
            local.RunGit("switch", "-q", "-c", "topic", "main");
            local.RunGit("branch", "-q", "--set-upstream-to=dev");
            upstream.PushChange("f.txt", "theirs");
            var git = local.Git;
            var commands = 0;
            git.BeforeCommand = arguments =>
            {
                if (arguments.Contains("fetch"))
                {
                    commands++;
                }
            };

            var fetched = git.FetchUpstream();
            var incoming = Read(git);

            Assert.IsTrue(fetched.IsSuccess, fetched.Message);
            Assert.AreEqual(0, commands);
            Assert.IsTrue(incoming.LocalUpstream);
            Assert.AreEqual("dev", incoming.Upstream);
            Assert.AreEqual(1, incoming.IncomingCount);

            local.RunGit("branch", "-q", "-D", "dev");
            Assert.AreEqual("topic tracks the local branch dev, which no longer exists", Read(git).Result.Message);
        }

        [Test]
        public void Incoming_WithUnrelatedHistories_SaysUpdateProjectCannotMergeThem()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = TestRepository.Create("local");
            local.Write("own.txt", "own\n");
            local.CommitAll("own root");
            local.RunGit("remote", "add", "origin", remote.Root);
            local.RunGit("fetch", "-q", "origin");
            local.RunGit("branch", "-q", "--set-upstream-to=origin/main");

            Assert.AreEqual("main and origin/main have no common history, Update Project cannot merge them",
                Read(local.Git).Result.Message);
        }

        [Test]
        public void Incoming_CountsLocalCommitsAgainstThePushTarget()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            using var local = remote.Clone("local");
            local.RunGit("switch", "-q", "-c", "feature", "origin/main");
            local.Write("a.txt", "a\n");
            local.CommitAll("a");
            local.Write("b.txt", "b\n");
            local.CommitAll("b");
            local.RunGit("push", "-q", "origin", "feature");
            upstream.PushChange("f.txt", "theirs");
            Assert.IsTrue(local.Git.FetchUpstream().IsSuccess);

            var incoming = Read(local.Git);

            Assert.AreEqual("origin/main", incoming.Upstream);
            Assert.AreEqual("1 incoming commit, 2 commits not in origin/main", incoming.Summary);
        }

        [Test]
        public void Incoming_InAPartialClone_KeepsWhatTheReadWithNetworkDownloaded()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            remote.RunGit("config", "uploadpack.allowFilter", "true");
            var path = Path.Combine(remote.BaseDirectory, "partial");
            upstream.RunGit("clone", "-q", "--filter=blob:none", "-c", "core.autocrlf=false",
                "file:///" + remote.Root.Replace('\\', '/'), path);
            upstream.PushChange("f.txt", "theirs");
            File.WriteAllText(Path.Combine(path, "f.txt"), "mine\n");
            var git = TestRepository.GitAt(path);
            Assert.IsTrue(git.FetchUpstream().IsSuccess);

            var online = Read(git);
            var offline = git.ReadIncoming(online.Cache, false, UpdateStrategyChoice.Merge, 100, 1000);

            Assert.AreEqual(IncomingMark.ConflictsWithYourChanges, online.Marks["f.txt"]);
            Assert.IsNull(offline.Note);
            Assert.AreEqual(IncomingMark.ConflictsWithYourChanges, offline.Marks["f.txt"]);
        }

        [Test]
        public void Incoming_InAPartialCloneWithoutNetwork_DownloadsNothingAndSaysSo()
        {
            using var remote = TestRepository.CreateBare();
            using var upstream = remote.PublishBase();
            upstream.Write("long.txt", string.Join("\n", Enumerable.Range(0, 40).Select(i => $"line {i}")) + "\n");
            upstream.CommitAll("long");
            upstream.RunGit("push", "-q");
            remote.RunGit("config", "uploadpack.allowFilter", "true");
            var path = Path.Combine(remote.BaseDirectory, "partial");
            upstream.RunGit("clone", "-q", "--filter=blob:none", "-c", "core.autocrlf=false",
                "file:///" + remote.Root.Replace('\\', '/'), path);
            upstream.RunGit("mv", "long.txt", "moved.txt");
            upstream.Write("moved.txt",
                string.Join("\n", Enumerable.Range(0, 40).Select(i => i == 20 ? "changed" : $"line {i}")) + "\n");
            upstream.CommitAll("move and edit");
            upstream.RunGit("push", "-q");
            var git = TestRepository.GitAt(path);
            Assert.IsTrue(git.FetchUpstream().IsSuccess);
            var blob = upstream.RunGit("rev-parse", "HEAD:moved.txt");

            var offline = Read(git, false);
            var missing = upstream.RunGit("-C", path, "rev-list", "--objects", "--missing=print", "origin/main");

            Assert.IsTrue(offline.Result.IsSuccess, offline.Result.Message);
            StringAssert.Contains("?" + blob, missing);
            StringAssert.StartsWith("Some file contents are not downloaded yet", offline.Note);
            Assert.IsNull(offline.Cache);

            var online = Read(git);

            Assert.IsNull(online.Note);
            Assert.AreEqual(GitStatus.Renamed, online.Files.Single().Status);
        }
    }
}
