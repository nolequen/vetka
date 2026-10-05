using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    [FilePath("UserSettings/VetkaCommitMessages.asset", FilePathAttribute.Location.ProjectFolder)]
    internal class CommitMessageHistory : ScriptableSingleton<CommitMessageHistory>
    {
        internal const int Limit = 20;

        [SerializeField] private List<string> _messages = new List<string>();

        public IReadOnlyList<string> Messages => _messages;

        public void Remember(string message)
        {
            var updated = Prepend(_messages, message, Limit);
            if (updated == null)
            {
                return;
            }

            _messages = updated;
            Save(true);
        }

        internal static List<string> Prepend(IEnumerable<string> messages, string message, int limit)
        {
            var text = message?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var result = new List<string> { text };
            foreach (var existing in messages)
            {
                if (result.Count >= limit)
                {
                    break;
                }

                if (existing != text)
                {
                    result.Add(existing);
                }
            }

            return result;
        }
    }
}
