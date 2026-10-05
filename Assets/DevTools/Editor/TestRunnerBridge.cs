using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Upwake.Vetka.DevTools
{
    [InitializeOnLoad]
    public static class TestRunnerBridge
    {
        private const string RequestFile = "Temp/VetkaTests.request";
        private const string ResultFile = "Temp/VetkaTests.results.xml";
        private const double CheckIntervalSeconds = 1;

        private static double _nextCheck;
        private static bool _running;

        static TestRunnerBridge()
        {
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (_running || EditorApplication.timeSinceStartup < _nextCheck)
            {
                return;
            }

            _nextCheck = EditorApplication.timeSinceStartup + CheckIntervalSeconds;
            if (!File.Exists(RequestFile) || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            var filter = File.ReadAllText(RequestFile).Trim();
            File.Delete(RequestFile);
            if (File.Exists(ResultFile))
            {
                File.Delete(ResultFile);
            }

            var settings = new Filter { testMode = TestMode.EditMode };
            if (filter.Length > 0)
            {
                settings.groupNames = new[] { filter };
            }

            _running = true;
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new Callbacks(api));
            api.Execute(new ExecutionSettings(settings));
        }

        private sealed class Callbacks : ICallbacks
        {
            private readonly TestRunnerApi _api;

            public Callbacks(TestRunnerApi api)
            {
                _api = api;
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                TestRunnerApi.SaveResultToFile(result, ResultFile);
                _api.UnregisterCallbacks(this);
                _running = false;
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
            }
        }
    }
}
