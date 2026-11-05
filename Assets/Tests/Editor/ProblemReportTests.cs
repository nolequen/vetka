using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Upwake.Vetka.Tests
{
    internal class ProblemReportTests
    {
        [Serializable]
        private class PackageVersion
        {
            public string version = "";
        }

        [Test]
        public void Version_MatchesThePackageManifest()
        {
            var manifest = Path.Combine(Git.ProjectRoot, "Packages", "dev.upwake.vetka", "package.json");
            if (!File.Exists(manifest))
            {
                Assert.Pass("No package.json in the store layout");
            }

            var package = JsonUtility.FromJson<PackageVersion>(File.ReadAllText(manifest));
            Assert.AreEqual(package.version, VetkaInfo.Version);
        }

        [Test]
        public void Url_OpensTheBugFormWithTheFourFieldsEncoded()
        {
            var url = ProblemReport.Url("6000.0.74f1 & co", "0.2.0+dev", "git version 2.47.1.windows.1",
                "Windows 11 (10.0.26300) 专业版");

            Assert.AreEqual(
                "https://github.com/nolequen/vetka/issues/new?template=bug-report.yml" +
                "&unity=6000.0.74f1%20%26%20co" +
                "&vetka=0.2.0%2Bdev" +
                "&git=git%20version%202.47.1.windows.1" +
                "&os=Windows%2011%20%2810.0.26300%29%20%E4%B8%93%E4%B8%9A%E7%89%88",
                url);
        }

        [Test]
        public void GitValue_OfASupportedGit_IsItsVersionLine()
        {
            Assert.AreEqual("git version 2.47.1.windows.1",
                ProblemReport.GitValue(GitResult.Success("git version 2.47.1.windows.1")));
        }

        [Test]
        public void GitValue_OfAFailure_IsTheFirstLineOfItsMessage()
        {
            Assert.AreEqual("git version 2.30.0",
                ProblemReport.GitValue(GitResult.Failure("git version 2.30.0\nVetka needs Git 2.31 or newer")));
        }

        [Test]
        public void GitValue_WhenGitIsMissing_SaysSo()
        {
            var git = new Git(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "git.exe"),
                Path.GetTempPath());

            Assert.AreEqual("Git not found, set its path in Tools/Git/Settings",
                ProblemReport.GitValue(git.Version()));
        }

        [Test]
        public void GitValue_WhenTheReadWasAborted_IsNotFound()
        {
            Assert.AreEqual("not found", ProblemReport.GitValue(null));
        }

        [TestCase(@"C:\Program Files\Unity\Hub\Editor\6000.0.74f1\Editor\Unity.exe", "6000.0.74f1", "6000.0.74f1")]
        [TestCase(@"F:\tuanjie\installs\2022.3.61t15\Editor\Tuanjie.exe", "2022.3.61t15", "Tuanjie 2022.3.61t15")]
        [TestCase("/Applications/Tuanjie/Hub/Editor/2022.3.61t15/Tuanjie.app", "2022.3.61t15", "Tuanjie 2022.3.61t15")]
        public void UnityVersion_NamesTuanjie(string applicationPath, string unityVersion, string expected)
        {
            Assert.AreEqual(expected, ProblemReport.UnityVersion(applicationPath, unityVersion));
        }
    }
}
