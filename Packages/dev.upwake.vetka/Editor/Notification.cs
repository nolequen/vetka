using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class Notification
    {
        private const int DialogLines = 25;
        private const int DialogCharacters = 3000;

        public static void Show(GitResult result)
        {
            if (result.IsSuccess || result.IsCancelled)
            {
                Show(result.Message);
                return;
            }

            if (GitSettings.VerboseLogging)
            {
                Debug.LogWarning(result.Message);
            }

            var shown = Shorten(result.Message);
            if (shown != result.Message && !GitSettings.VerboseLogging)
            {
                Show(result.Message);
            }

            EditorUtility.DisplayDialog("Git", shown, "OK");
        }

        public static void Show(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "Vetka: {0}", message);
        }

        internal static string Shorten(string message)
        {
            var kept = new List<string>();
            var length = 0;
            foreach (var line in message.Split('\n'))
            {
                if (kept.Count == DialogLines || length + line.Length > DialogCharacters)
                {
                    if (kept.Count == 0)
                    {
                        kept.Add(line.Substring(0, DialogCharacters));
                    }

                    return $"{string.Join("\n", kept)}\nThe whole message is in the Console";
                }

                kept.Add(line);
                length += line.Length + 1;
            }

            return message;
        }
    }
}
