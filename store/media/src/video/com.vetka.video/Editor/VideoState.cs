using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Upwake.Vetka.Video
{
    [InitializeOnLoad]
    internal static class VideoState
    {
        private const string StatePath = "Temp/Video.state.json";
        private const string TempPath = "Temp/Video.state.tmp";

        private static double _nextWrite;
        private static int _started;

        static VideoState()
        {
            EditorApplication.update += Update;
            Progress.added += items => _started++;
            CompilationPipeline.compilationStarted += context => _started++;
        }

        private static void Update()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now < _nextWrite)
            {
                return;
            }

            _nextWrite = now + 0.2;
            var json = new StringBuilder();
            json.Append("{\"time\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            json.Append(",\"compiling\":").Append(Bool(EditorApplication.isCompiling));
            json.Append(",\"updating\":").Append(Bool(EditorApplication.isUpdating));
            json.Append(",\"progress\":").Append(Bool(Progress.running));
            json.Append(",\"started\":").Append(_started);
            var focused = EditorWindow.focusedWindow;
            json.Append(",\"focused\":").Append(Text(focused != null ? focused.GetType().FullName : null));
            json.Append(",\"main\":").Append(Rect(EditorGUIUtility.GetMainWindowPosition()));
            json.Append(",\"windows\":[");
            var first = true;
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (!first)
                {
                    json.Append(',');
                }

                first = false;
                json.Append("{\"type\":").Append(Text(window.GetType().FullName));
                json.Append(",\"title\":").Append(Text(window.titleContent.text));
                json.Append(",\"rect\":").Append(Rect(window.position)).Append('}');
            }

            json.Append("]}");
            try
            {
                File.WriteAllText(TempPath, json.ToString(), new UTF8Encoding(false));
                if (File.Exists(StatePath))
                {
                    File.Replace(TempPath, StatePath, null);
                }
                else
                {
                    File.Move(TempPath, StatePath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string Rect(Rect rect)
        {
            return string.Format(CultureInfo.InvariantCulture, "[{0},{1},{2},{3}]",
                Mathf.RoundToInt(rect.x), Mathf.RoundToInt(rect.y), Mathf.RoundToInt(rect.width),
                Mathf.RoundToInt(rect.height));
        }

        private static string Text(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var text = new StringBuilder("\"");
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        text.Append("\\\"");
                        break;
                    case '\\':
                        text.Append("\\\\");
                        break;
                    default:
                        if (c < ' ')
                        {
                            text.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            text.Append(c);
                        }

                        break;
                }
            }

            return text.Append('"').ToString();
        }
    }
}
