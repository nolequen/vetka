using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Upwake.Vetka.Tests
{
    internal class GitOperationsTests
    {
        [UnityTest]
        public IEnumerator Run_WhenTheWorkThrows_StillReportsAResult()
        {
            LogAssert.Expect(LogType.Exception, new Regex("boom"));
            GitResult? reported = null;

            GitOperations.Run("Git: exploding", () => throw new InvalidOperationException("boom"),
                result => reported = result);

            yield return WaitUntil(() => reported.HasValue);
            Assert.IsTrue(reported.HasValue, "The operation never reported back");
            Assert.IsFalse(reported.Value.IsSuccess);
            Assert.AreEqual("Exploding failed: boom", reported.Value.Message);
        }

        [UnityTest]
        public IEnumerator Read_WhenTheWorkThrows_ReportsWhyItStopped()
        {
            LogAssert.Expect(LogType.Exception, new Regex("boom"));
            string aborted = null;
            var completed = false;

            GitOperations.Read<int>("Git: exploding", () => throw new InvalidOperationException("boom"),
                _ => completed = true, reason => aborted = reason);

            yield return WaitUntil(() => aborted != null);
            Assert.AreEqual("Failed: boom", aborted);
            Assert.IsFalse(completed);
        }

        [UnityTest]
        public IEnumerator Operations_RunOneAtATimeInOrder()
        {
            var order = new List<string>();
            var running = 0;
            var overlapped = false;
            using var release = new ManualResetEventSlim(false);

            T Work<T>(T value, bool wait)
            {
                if (Interlocked.Increment(ref running) > 1)
                {
                    overlapped = true;
                }

                if (wait)
                {
                    release.Wait(5000);
                }

                Interlocked.Decrement(ref running);
                return value;
            }

            GitOperations.Run("Git: first", () => Work(GitResult.Success("first"), true),
                result => order.Add(result.Message));
            GitOperations.Read("Git: second", () => Work("second", false), value => order.Add(value));
            GitOperations.Run("Git: third", () => Work(GitResult.Success("third"), false),
                result => order.Add(result.Message));

            Assert.AreEqual("Git: first", GitOperations.GuardedTitle);
            release.Set();

            yield return WaitUntil(() => order.Count == 3);
            CollectionAssert.AreEqual(new[] { "first", "second", "third" }, order);
            Assert.IsFalse(overlapped);
        }

        [UnityTest]
        public IEnumerator Reads_DoNotHoldBackQuitOrPlayMode()
        {
            using var release = new ManualResetEventSlim(false);
            var done = false;

            GitOperations.Read("Git: slow read", () => release.Wait(5000), _ => done = true);

            Assert.IsNull(GitOperations.GuardedTitle);
            release.Set();
            yield return WaitUntil(() => done);
            Assert.IsTrue(done);
        }

        [UnityTest]
        public IEnumerator Run_WhenThePreparationIsDeclined_DoesNotRunTheWork()
        {
            var worked = false;
            GitResult? reported = null;

            GitOperations.Run("Git: declined", () =>
            {
                worked = true;
                return GitResult.Success("done");
            }, result => reported = result, prepare: () => false);

            yield return WaitUntil(() => reported.HasValue);
            Assert.IsTrue(reported.HasValue, "The operation never reported back");
            Assert.IsTrue(reported.Value.IsCancelled);
            Assert.AreEqual("Declined cancelled", reported.Value.Message);
            Assert.IsFalse(worked);
            Assert.IsNull(GitOperations.GuardedTitle);
        }

        [UnityTest]
        public IEnumerator Run_WithAProgressStartedEarlier_ShowsOnlyThatOne()
        {
            using var release = new ManualResetEventSlim(false);
            var progress = GitOperations.StartProgress("Git: started early", false);
            var started = false;
            var done = false;

            GitOperations.Run("Git: started early", () =>
            {
                started = true;
                release.Wait(5000);
                return GitResult.Success("done");
            }, _ => done = true, progressId: progress);

            yield return WaitUntil(() => started);
            var shown = Progress.EnumerateItems().Where(item => item.name == "Git: started early")
                .Select(item => item.id).ToList();
            release.Set();
            yield return WaitUntil(() => done && !Running(progress));
            CollectionAssert.AreEqual(new[] { progress }, shown);
            Assert.IsTrue(done, "The operation never finished");
            Assert.IsFalse(Running(progress));
        }

        [UnityTest]
        public IEnumerator Run_WhenThePreparationIsDeclined_DropsTheProgressStartedEarlier()
        {
            var progress = GitOperations.StartProgress("Git: declined early", false);
            GitResult? reported = null;

            GitOperations.Run("Git: declined early", () => GitResult.Success("done"), result => reported = result,
                prepare: () => false, progressId: progress);

            yield return WaitUntil(() => reported.HasValue && !Progress.Exists(progress));
            Assert.IsTrue(reported.HasValue, "The operation never reported back");
            Assert.IsTrue(reported.Value.IsCancelled);
            Assert.IsFalse(Progress.Exists(progress));
        }

        [UnityTest]
        public IEnumerator Run_PreparesOnlyWhenItsTurnComes()
        {
            using var release = new ManualResetEventSlim(false);
            var firstDone = false;
            var secondDone = false;
            bool? preparedAfterFirst = null;

            GitOperations.Run("Git: first", () =>
            {
                release.Wait(5000);
                return GitResult.Success("first");
            }, _ => firstDone = true);
            GitOperations.Run("Git: second", () => GitResult.Success("second"), _ => secondDone = true,
                prepare: () =>
                {
                    preparedAfterFirst = firstDone;
                    return true;
                });

            Assert.IsNull(preparedAfterFirst);
            release.Set();
            yield return WaitUntil(() => secondDone);
            Assert.IsTrue(secondDone, "The second operation never finished");
            Assert.IsTrue(preparedAfterFirst);
        }

        private static bool Running(int progress) =>
            Progress.Exists(progress) && Progress.GetStatus(progress) == Progress.Status.Running;

        private static IEnumerator WaitUntil(Func<bool> condition)
        {
            var deadline = EditorApplication.timeSinceStartup + 10;
            while (!condition() && EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
        }
    }
}
