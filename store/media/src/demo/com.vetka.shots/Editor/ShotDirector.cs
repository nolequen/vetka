using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Upwake.Vetka.Shots
{
    [InitializeOnLoad]
    internal static class ShotDirector
    {
        private const string RequestPath = "Temp/Shots.request";
        private const string DonePath = "Temp/Shots.done";
        private const string Player = "Assets/Game/Scripts/Player/PlayerController.cs";
        private const string Enemy = "Assets/Game/Scripts/Enemies/EnemyAI.cs";
        private const string Message = "Add dash ability\n\nQ dashes in the move direction with a 0.8 s cooldown.";

        private static double _nextPoll;
        private static double _doneAt;
        private static string _pending;
        private static Func<bool> _ready;

        static ShotDirector()
        {
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            var now = EditorApplication.timeSinceStartup;
            if (_pending != null)
            {
                if (_ready != null && !_ready())
                {
                    _doneAt = now + 2.5;
                    return;
                }

                if (now < _doneAt)
                {
                    return;
                }

                InternalEditorUtility.RepaintAllViews();
                File.WriteAllText(DonePath, _pending);
                _pending = null;
                return;
            }

            if (now < _nextPoll)
            {
                return;
            }

            _nextPoll = now + 0.5;
            if (!File.Exists(RequestPath))
            {
                return;
            }

            var command = File.ReadAllText(RequestPath).Trim();
            File.Delete(RequestPath);
            if (File.Exists(DonePath))
            {
                File.Delete(DonePath);
            }

            _ready = null;
            try
            {
                Run(command);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }

            _pending = command;
            _doneAt = now + 3;
        }

        private static void Run(string command)
        {
            var git = new Git();
            switch (command)
            {
                case "setup":
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Game/Scenes/Main.unity");
                    foreach (var window in Resources.FindObjectsOfTypeAll<ChangesWindow>())
                    {
                        window.Close();
                    }

                    break;
                case "changes":
                    EditorWindow.GetWindow<ChangesWindow>("Changes", true, typeof(SceneView));
                    ChangesWindow.ShowWindow(git, Message);
                    Ping(Enemy);
                    break;
                case "branches":
                    SessionState.SetBool("Vetka.Branches.Local", true);
                    SessionState.SetBool("Vetka.Branches.Remote", true);
                    SelectPlayerObject();
                    EditorWindow.GetWindow<BranchesWindow>("Git branches", true, typeof(SceneView));
                    BranchesWindow.ShowWindow();
                    break;
                case "log":
                    SelectPlayerObject();
                    var log = EditorWindow.GetWindow<GitLogWindow>("Git log", true, typeof(SceneView));
                    GitLogWindow.ShowWindow(git);
                    _ready = () => SelectCommit(log, "Fix camera jitter");
                    break;
                case "diff":
                    EditorWindow.GetWindow<DiffWindow>("Diff", true, typeof(SceneView));
                    DiffWindow.ShowWindow(git, Player, false);
                    Ping(Player);
                    break;
                case "blame":
                    EditorWindow.GetWindow<BlameWindow>("Blame", true, typeof(SceneView));
                    BlameWindow.ShowWindow(git, Player);
                    Ping(Player);
                    break;
                case "project":
                    Ping(Enemy);
                    break;
                default:
                    Debug.LogWarning("Unknown shot: " + command);
                    break;
            }
        }

        private static void SelectPlayerObject()
        {
            var player = GameObject.Find("Player");
            if (player != null)
            {
                Selection.activeGameObject = player;
            }
        }

        private static void Ping(string path)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        private static bool SelectCommit(GitLogWindow window, string subject)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var commits = typeof(GitLogWindow).GetField("_commits", flags)?.GetValue(window) as System.Collections.IEnumerable;
            if (commits == null)
            {
                return false;
            }

            var commit = commits.Cast<object>().FirstOrDefault(c => Read(c, "Subject") == subject);
            if (commit == null)
            {
                return true;
            }

            var hash = Read(commit, "Hash");
            if (typeof(GitLogWindow).GetField("_selected", flags)?.GetValue(window) as string == hash)
            {
                return true;
            }

            typeof(GitLogWindow).GetMethod("Select", flags)?.Invoke(window, new object[] { hash });
            return false;
        }

        private static string Read(object target, string name)
        {
            var type = target.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var value = type.GetProperty(name, flags)?.GetValue(target) ?? type.GetField(name, flags)?.GetValue(target);
            return value as string;
        }
    }
}
