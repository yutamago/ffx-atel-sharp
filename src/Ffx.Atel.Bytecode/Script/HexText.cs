// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace Ffx.Atel.Bytecode.Script;

/// <summary>
///     A compact, readable text form for a run of bytes that is mostly zero: a hex dump that leaves
///     zeros out.
/// </summary>
/// <remarks>
///     <code>
///     0000: 30312f32 38203133 3a32303a 3434
///     0040: ffffffff ffffffff
///     </code>
///     Each line is an offset (hex), a colon and up to <see cref="LineBytes"/> bytes as hex digits,
///     grouped in fours for reading; spaces between digits are ignored. Lines that would be all
///     zero are left out and a line ends at its last non-zero byte, so every byte no line gives is
///     zero. The length is kept apart from the text.
/// </remarks>
public static class HexText {
    public const int LineBytes = 32;

    /// <summary>Writes the lines, each ending in <c>\n</c> and starting with <paramref name="indent"/>.</summary>
    public static void Write(StringBuilder sb, ReadOnlySpan<byte> data, string indent = "") {
        int digits = Math.Max(4, (Math.Max(data.Length, 1) - 1).ToString("x", CultureInfo.InvariantCulture).Length);
        for (int line = 0; line < data.Length; line += LineBytes) {
            ReadOnlySpan<byte> chunk = data.Slice(line, Math.Min(LineBytes, data.Length - line));
            int last = chunk.LastIndexOfAnyExcept((byte)0);
            if (last < 0) continue;
            sb.Append(indent).Append(line.ToString("x" + digits, CultureInfo.InvariantCulture)).Append(':');
            for (int i = 0; i <= last; i++) {
                if (i % 4 == 0) sb.Append(' ');
                sb.Append(chunk[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            sb.Append('\n');
        }
    }

    /// <summary>The text form of <paramref name="data"/>.</summary>
    public static string Format(ReadOnlySpan<byte> data, string indent = "") {
        StringBuilder sb = new();
        Write(sb, data, indent);
        return sb.ToString();
    }

    /// <summary>Reads the text back into <paramref name="length"/> bytes.</summary>
    /// <exception cref="FormatException">A line is malformed or gives bytes past the length.</exception>
    public static byte[] Parse(string text, int length) {
        byte[] data = new byte[length];
        int lineNo = 0;
        foreach (string raw in text.Split('\n')) {
            lineNo++;
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0 || !int.TryParse(line.AsSpan(0, colon), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int at)) {
                throw new FormatException($"hex line {lineNo}: expected `offset: bytes`");
            }
            int nibble = -1;
            foreach (char c in line.AsSpan(colon + 1)) {
                if (c is ' ' or '\t') continue;
                int v = char.IsAsciiHexDigit(c) ? (c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10) : throw new FormatException($"hex line {lineNo}: `{c}` is not a hex digit");
                if (nibble < 0) {
                    nibble = v;
                    continue;
                }
                if (at >= length) throw new FormatException($"hex line {lineNo}: past the length 0x{length:x}");
                data[at++] = (byte)(nibble << 4 | v);
                nibble = -1;
            }
            if (nibble >= 0) throw new FormatException($"hex line {lineNo}: odd number of digits");
        }
        return data;
    }
}
