using System;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class CodeView
    {
        public const float NumberWidth = 44;

        public static readonly SkinStyle Text = new SkinStyle(() => new GUIStyle(EditorStyles.label)
        {
            font = EditorGUIUtility.Load("Fonts/RobotoMono/RobotoMono-Regular.ttf") as Font,
            fontSize = 12,
            wordWrap = false,
            richText = false,
            padding = new RectOffset(0, 0, 1, 1)
        });

        public static readonly SkinStyle Number = new SkinStyle(() => new GUIStyle(Text)
        {
            alignment = TextAnchor.MiddleRight,
            padding = new RectOffset(0, 6, 1, 1),
            normal = { textColor = Color.gray }
        });

        public static void Draw(Rect position, ref Vector2 scroll, int count, int longestLine, float textX,
            Action<int, Rect> drawRow)
        {
            var lineHeight = Mathf.Ceil(Text.Style.lineHeight) + 2;
            var charWidth = Text.Style.CalcSize(new GUIContent("M")).x;
            var contentWidth = Mathf.Max(position.width - 16, textX + longestLine * charWidth + 16);

            scroll = GUILayout.BeginScrollView(scroll);
            var area = GUILayoutUtility.GetRect(contentWidth, count * lineHeight);
            var first = Mathf.Max(0, (int)(scroll.y / lineHeight));
            var last = Mathf.Min(count, first + (int)(position.height / lineHeight) + 2);
            for (var i = first; i < last; i++)
            {
                drawRow(i, new Rect(area.x, area.y + i * lineHeight, contentWidth, lineHeight));
            }

            GUILayout.EndScrollView();
        }
    }
}
