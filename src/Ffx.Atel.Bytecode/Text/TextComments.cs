// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ffx.Atel.Bytecode.Text;

/// <summary>The <c>// "…"</c> comments <c>atel decompile --text-comments</c> writes after <c>Text[n]</c> (docs/plan.md, "Text").</summary>
public static partial class TextComments {
    [GeneratedRegex(@"\bText\[(\d+)\]")]
    private static partial Regex TextIndex();

    /// <summary>
    ///     Adds <c>// "…"</c> after every line that uses <c>Text[n]</c> with the message's text (several:
    ///     <c>// 3: "…"; 4: "…"</c>). Comments only, so the script compiles to the same bytes.
    /// </summary>
    public static string Comment(string source, IReadOnlyList<string?> messages) {
        StringBuilder sb = new(source.Length + 1024);
        int start = 0;
        while (start < source.Length) {
            int nl = source.IndexOf('\n', start);
            int end = nl < 0 ? source.Length : nl;
            string line = source[start..end];
            sb.Append(line);
            List<(int Index, string Text)> used = [];
            foreach (Match m in TextIndex().Matches(line)) {
                if (int.TryParse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int i)
                    && i < messages.Count && messages[i] is string text && used.All(u => u.Index != i)) {
                    used.Add((i, text));
                }
            }
            if (used.Count == 1) sb.Append(" // ").Append(Quote(used[0].Text));
            else if (used.Count > 1) sb.Append(" // ").AppendJoin("; ", used.Select(u => string.Create(CultureInfo.InvariantCulture, $"{u.Index}: {Quote(u.Text)}")));
            if (nl < 0) break;
            sb.Append('\n');
            start = nl + 1;
        }
        return sb.ToString();
    }

    /// <summary>A message on one line, in quotes: line breaks as <c>\n</c>, quotes and backslashes escaped.</summary>
    public static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}
