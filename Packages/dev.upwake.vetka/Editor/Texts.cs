namespace Upwake.Vetka
{
    internal static class Texts
    {
        public static string Counted(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

        public static string Shortened(string text, int length) =>
            text.Length > length ? text.Substring(0, length - 3) + "..." : text;

        public static string Capitalized(string text) =>
            string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

        public static string MenuLabel(string text) => text.Replace('/', '\u2215');
    }
}
