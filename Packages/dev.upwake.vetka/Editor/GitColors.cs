using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class GitColors
    {
        public static Color Selection => EditorGUIUtility.isProSkin
            ? new Color(0.24f, 0.37f, 0.59f, 0.6f)
            : new Color(0.24f, 0.49f, 0.9f, 0.3f);

        public static Color Status(GitStatus status) =>
            EditorGUIUtility.isProSkin ? DarkStatusColor(status) : LightStatusColor(status);

        public static Color Unpushed => EditorGUIUtility.isProSkin
            ? new Color(0.55f, 0.85f, 1f)
            : new Color(0f, 0.33f, 0.6f);

        public static Color Tag => EditorGUIUtility.isProSkin
            ? new Color(0.45f, 0.39f, 0.18f)
            : new Color(0.98f, 0.87f, 0.52f);

        public static GUIStyle StatusStyle(GUIStyle baseStyle, GitStatus status) =>
            TextStyle(baseStyle, Status(status));

        public static GUIStyle TextStyle(GUIStyle baseStyle, Color color)
        {
            var key = (baseStyle, color, EditorGUIUtility.isProSkin);
            if (TextStyles.TryGetValue(key, out var style))
            {
                return style;
            }

            var text = EditorGUIUtility.isProSkin ? color * baseStyle.normal.textColor : color;
            text.a = 1;
            style = new GUIStyle(baseStyle) { normal = { textColor = text } };
            TextStyles[key] = style;
            return style;
        }

        private static readonly Dictionary<(GUIStyle, Color, bool), GUIStyle> TextStyles =
            new Dictionary<(GUIStyle, Color, bool), GUIStyle>();

        private static Color DarkStatusColor(GitStatus status)
        {
            return status switch
            {
                GitStatus.Added => Color.green,
                GitStatus.Modified => Color.cyan,
                GitStatus.Deleted => Color.grey,
                GitStatus.Renamed => Color.cyan,
                GitStatus.Copied => Color.cyan,
                GitStatus.TypeChanged => Color.cyan,
                GitStatus.Unmerged => Color.red,
                GitStatus.Untracked => new Color(0.76f, 0.62f, 0.96f),
                _ => Color.gray
            };
        }

        private static Color LightStatusColor(GitStatus status)
        {
            return status switch
            {
                GitStatus.Added => new Color(0.05f, 0.4f, 0.05f),
                GitStatus.Modified => new Color(0f, 0.35f, 0.48f),
                GitStatus.Deleted => new Color(0.4f, 0.4f, 0.4f),
                GitStatus.Renamed => new Color(0f, 0.35f, 0.48f),
                GitStatus.Copied => new Color(0f, 0.35f, 0.48f),
                GitStatus.TypeChanged => new Color(0f, 0.35f, 0.48f),
                GitStatus.Unmerged => new Color(0.75f, 0.05f, 0.05f),
                GitStatus.Untracked => new Color(0.4f, 0.2f, 0.7f),
                _ => new Color(0.4f, 0.4f, 0.4f)
            };
        }
    }
}
