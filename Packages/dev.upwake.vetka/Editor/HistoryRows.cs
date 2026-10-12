using System;
using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class HistoryRows
    {
        private static readonly SkinStyle TagStyle =
            new SkinStyle(() => new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter });

        public static void Commit(Rect rect, GitCommit commit)
        {
            var style = commit.IsPushed
                ? EditorStyles.label
                : GitColors.TextStyle(EditorStyles.label, GitColors.Unpushed);
            GUI.Label(new Rect(rect.x, rect.y, 60, rect.height), commit.Hash, style);
            GUI.Label(new Rect(rect.x + 64, rect.y, 80, rect.height), commit.Date, style);
            GUI.Label(new Rect(rect.x + 148, rect.y, 110, rect.height), commit.Author, style);
            var x = rect.x + 262;
            foreach (var tag in commit.Tags)
            {
                var content = new GUIContent(tag);
                var width = TagStyle.Style.CalcSize(content).x + 8;
                var chip = new Rect(x, rect.y + 1, Mathf.Max(0, Mathf.Min(width, rect.xMax - x)), rect.height - 2);
                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(chip, GitColors.Tag);
                }

                GUI.Label(chip, content, TagStyle);
                x += width + 4;
            }

            GUI.Label(new Rect(x, rect.y, Mathf.Max(0, rect.xMax - x), rect.height), commit.Subject, style);
        }

        public static void File(Rect rect, GitFileChange file, string label, ref string selected, Action open,
            Action<GenericMenu> fillMenu, Action repaint, string note = null, Color noteColor = default)
        {
            var current = Event.current;
            if (current.type == EventType.MouseDown && rect.Contains(current.mousePosition))
            {
                selected = file.Path;
                if (current.button == 0 && current.clickCount == 2)
                {
                    open();
                }

                current.Use();
                repaint();
            }

            if (current.type == EventType.ContextClick && rect.Contains(current.mousePosition))
            {
                var menu = new GenericMenu();
                fillMenu(menu);
                menu.ShowAsContext();
                current.Use();
            }

            if (current.type == EventType.Repaint && file.Path == selected)
            {
                EditorGUI.DrawRect(rect, GitColors.Selection);
            }

            var labelRect = rect;
            if (!string.IsNullOrEmpty(note))
            {
                var content = new GUIContent(note);
                var width = Mathf.Min(EditorStyles.miniBoldLabel.CalcSize(content).x + 4, rect.width / 2);
                labelRect.width = Mathf.Max(0, rect.width - width - 8);
                GUI.Label(new Rect(rect.xMax - width, rect.y, width, rect.height), content,
                    GitColors.TextStyle(EditorStyles.miniBoldLabel, noteColor));
            }

            GUI.Label(labelRect, label, GitColors.StatusStyle(GUI.skin.label, file.Status));
        }
    }
}
