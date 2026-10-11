using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class GitColors
    {
        public static Color Selection => Skin(new Color(0.24f, 0.37f, 0.59f, 0.6f), new Color(0.24f, 0.49f, 0.9f, 0.3f));

        public static Color Status(GitStatus status) => status switch
        {
            GitStatus.Added => Skin(Color.green, new Color(0.05f, 0.4f, 0.05f)),
            GitStatus.Modified or GitStatus.Renamed or GitStatus.Copied or GitStatus.TypeChanged =>
                Skin(Color.cyan, new Color(0f, 0.35f, 0.48f)),
            GitStatus.Unmerged => Skin(Color.red, new Color(0.75f, 0.05f, 0.05f)),
            GitStatus.Untracked => Skin(new Color(0.76f, 0.62f, 0.96f), new Color(0.4f, 0.2f, 0.7f)),
            _ => Skin(Color.gray, new Color(0.4f, 0.4f, 0.4f))
        };

        public static Color Unpushed => Skin(new Color(0.55f, 0.85f, 1f), new Color(0f, 0.33f, 0.6f));

        public static Color Tag => Skin(new Color(0.45f, 0.39f, 0.18f), new Color(0.98f, 0.87f, 0.52f));

        public static Color Highlight => Skin(new Color(0.25f, 0.35f, 0.55f, 0.35f), new Color(0.8f, 0.87f, 1f, 0.8f));

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

        private static Color Skin(Color dark, Color light) => EditorGUIUtility.isProSkin ? dark : light;
    }
}
