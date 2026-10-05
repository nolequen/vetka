using UnityEditor;
using UnityEngine;

namespace Upwake.Vetka
{
    internal static class StarIcon
    {
        private const float Size = 12;

        private static readonly Color Fill = new Color(0.95f, 0.75f, 0.25f);

        public static Rect RectIn(Rect row) =>
            new Rect(row.x + 4, row.y + (row.height - Size) / 2, Size, Size);

        public static bool Toggle(Rect row)
        {
            var rect = RectIn(row);
            var current = Event.current;
            if (current.type != EventType.MouseDown || current.button != 0 ||
                !rect.Contains(current.mousePosition))
            {
                return false;
            }

            current.Use();
            return true;
        }

        public static void Draw(Rect row, bool filled)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            var rect = RectIn(row);
            var center = rect.center;
            var outer = rect.width / 2;
            var inner = outer * 0.42f;

            var points = new Vector3[11];
            for (var i = 0; i < 10; i++)
            {
                var angle = Mathf.PI / 2 + i * Mathf.PI / 5;
                var radius = i % 2 == 0 ? outer : inner;
                points[i] = new Vector3(center.x + Mathf.Cos(angle) * radius, center.y - Mathf.Sin(angle) * radius);
            }

            points[10] = points[0];

            var previous = Handles.color;
            if (filled)
            {
                Handles.color = Fill;
                var core = new Vector3[5];
                for (var i = 0; i < 5; i++)
                {
                    core[i] = points[i * 2 + 1];
                    Handles.DrawAAConvexPolygon(points[i * 2], points[i * 2 + 1], points[(i * 2 + 9) % 10]);
                }

                Handles.DrawAAConvexPolygon(core);
                Handles.DrawAAPolyLine(1.5f, points);
            }
            else
            {
                Handles.color = new Color(0.6f, 0.6f, 0.6f);
                Handles.DrawAAPolyLine(1.5f, points);
            }

            Handles.color = previous;
        }
    }
}
