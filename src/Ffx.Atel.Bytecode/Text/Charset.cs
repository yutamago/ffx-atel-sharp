// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace Ffx.Atel.Bytecode.Text;

/// <summary>A message that cannot be encoded.</summary>
public sealed class TextEncodeException(string message) : Exception(message);

/// <summary>
///     One language's character set, and FFX's text encoding: decodes game bytes to a readable string and
///     encodes it back.
/// </summary>
/// <remarks>
///     Ported from ffx-rust's <c>crates/ffx-text</c> (<c>Charset</c>; <c>docs/formats/text.md</c>), recovered
///     there from <c>Text_ToUtf8</c> (<c>0x00645f50</c>). A string is
///     <list type="table">
///         <item><term><c>00</c></term><description>end</description></item>
///         <item><term><c>01</c>, <c>02</c>, <c>05</c></term><description>control code, no argument</description></item>
///         <item><term><c>03</c></term><description>line break</description></item>
///         <item><term><c>04</c> + character</term><description>second bank: character index + <c>0x410</c></description></item>
///         <item><term><c>06</c>-<c>2b</c> + byte</term><description>control code with one argument</description></item>
///         <item><term><c>2c</c>-<c>2f</c> + <c>30</c>-<c>ff</c></term><description>character <c>(lead - 0x2b) * 0xd0 + (trail - 0x30)</c></description></item>
///         <item><term><c>30</c>-<c>ff</c></term><description>character <c>byte - 0x30</c></description></item>
///     </list>
///     Character index <i>n</i> is the <i>n</i>th character of the language's table
///     (<see cref="GameLanguages.CharsetPath"/>), UTF-8 text in code order. The table is game data: it is read
///     from the player's copy, never shipped.
///     <para>
///         <see cref="Decode"/> gives ffx-rust's lossless form (<c>{0a:41}</c>, <c>{c:1a2}</c>, <c>{raw:2c}</c>,
///         <c>{{</c>), which <see cref="Encode"/> turns back into the same bytes; <see cref="Display"/> gives
///         the form an editor shows, with named markers (<c>{name:Tidus}</c>, <c>{wait:4}</c>), which does not
///         round-trip.
///     </para>
/// </remarks>
public sealed class Charset {
    private const int Bank = 0x410;
    private const int Page = 0xd0;

    private readonly string[]                     _chars;
    private readonly Dictionary<int, string>      _aliases = [];
    private readonly Dictionary<string, int>      _index   = new(StringComparer.Ordinal);

    /// <summary>Builds a charset from the bytes of <c>ffxsjistbl_*.bin</c>.</summary>
    /// <exception cref="InvalidDataException">The table is not UTF-8.</exception>
    public Charset(byte[] table, GameLanguage language) {
        string text;
        try {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(table);
        }
        catch (DecoderFallbackException) {
            throw new InvalidDataException("the charset table is not UTF-8");
        }
        List<string> chars = [];
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1) {
            chars.Add(char.ConvertFromUtf32(char.ConvertToUtf32(text, i)));
        }
        _chars = [.. chars];
        // 0x006460a0: in Japanese, codes 0x5c-0x5e show the character at index 10; in Korean, index 10 shows a space.
        if (language == GameLanguage.Japanese && _chars.Length > 10) {
            for (int i = 0x2c; i <= 0x2e; i++) _aliases[i] = _chars[10];
        }
        else if (language == GameLanguage.Korean) {
            _aliases[10] = " ";
        }
        for (int i = 0; i < _chars.Length; i++) {
            if (!_aliases.ContainsKey(i)) _index.TryAdd(_chars[i], i);
        }
    }

    /// <summary>Number of characters in the table.</summary>
    public int Count => _chars.Length;

    private string? CharAt(int index) =>
        _aliases.TryGetValue(index, out string? alias) ? alias : index >= 0 && index < _chars.Length ? _chars[index] : null;

    /// <summary>Decodes one string (to its <c>00</c> or the end) into ffx-rust's lossless readable form.</summary>
    public string Decode(ReadOnlySpan<byte> bytes) => Decode(bytes, display: false);

    /// <summary>
    ///     Decodes one string for reading: line breaks as <c>\n</c>, control codes as named markers
    ///     (<see cref="ControlCodes.Display"/>), braces as they are. Not meant to be encoded back.
    /// </summary>
    public string Display(ReadOnlySpan<byte> bytes) => Decode(bytes, display: true);

    private string Decode(ReadOnlySpan<byte> bytes, bool display) {
        int end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        StringBuilder sb = new();
        int i = 0;
        while (i < bytes.Length) {
            byte b = bytes[i];
            switch (b) {
                case 0x03:
                    sb.Append('\n');
                    i++;
                    break;
                case 0x01 or 0x02 or 0x05:
                    sb.Append(display ? ControlCodes.Display(b, null) : Hex(b));
                    i++;
                    break;
                case 0x04:
                    if (ReadChar(bytes[(i + 1)..]) is (int index, int len)) {
                        PushChar(sb, index + Bank, banked: true, display);
                        i += 1 + len;
                    }
                    else {
                        sb.Append(CultureInfo.InvariantCulture, $"{{raw:{b:x2}}}");
                        i++;
                    }
                    break;
                case >= 0x06 and <= 0x2b:
                    if (i + 1 < bytes.Length) {
                        byte arg = bytes[i + 1];
                        sb.Append(display ? ControlCodes.Display(b, arg) : string.Create(CultureInfo.InvariantCulture, $"{{{b:x2}:{arg:x2}}}"));
                        i += 2;
                    }
                    else {
                        sb.Append(CultureInfo.InvariantCulture, $"{{raw:{b:x2}}}");
                        i++;
                    }
                    break;
                default:
                    if (ReadChar(bytes[i..]) is (int c, int n)) {
                        PushChar(sb, c, banked: false, display);
                        i += n;
                    }
                    else {
                        sb.Append(CultureInfo.InvariantCulture, $"{{raw:{b:x2}}}");
                        i++;
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Hex(byte b) => string.Create(CultureInfo.InvariantCulture, $"{{{b:x2}}}");

    private void PushChar(StringBuilder sb, int index, bool banked, bool display) {
        string? c = CharAt(index);
        if (display && c is not null && c != "\n") {
            sb.Append(c);
            return;
        }
        // `\n` stands for code 03, so a table newline needs the escape; so does a duplicate.
        if (c is not null && c != "\n" && _index.TryGetValue(c, out int canonical) && canonical == index && (index >= Bank) == banked) {
            sb.Append(c switch { "{" => "{{", "}" => "}}", _ => c });
        }
        else {
            sb.Append(CultureInfo.InvariantCulture, $"{{c:{index:x}}}");
        }
    }

    /// <summary>Encodes the lossless form (<see cref="Decode"/>) into game bytes, without the terminating <c>00</c>.</summary>
    /// <exception cref="TextEncodeException">A character missing from the charset, or a malformed escape.</exception>
    public byte[] Encode(string text) {
        List<byte> output = [];
        int i = 0;
        while (i < text.Length) {
            char ch = text[i];
            if (ch == '\n') {
                output.Add(0x03);
                i++;
            }
            else if (ch == '}' && i + 1 < text.Length && text[i + 1] == '}') {
                PushIndex(output, Lookup("}"));
                i += 2;
            }
            else if (ch == '{' && i + 1 < text.Length && text[i + 1] == '{') {
                PushIndex(output, Lookup("{"));
                i += 2;
            }
            else if (ch == '{') {
                int close = text.IndexOf('}', i + 1);
                string escape = close < 0 ? text[(i + 1)..] : text[(i + 1)..close];
                PushEscape(output, escape);
                i = close < 0 ? text.Length : close + 1;
            }
            else {
                int n = char.IsSurrogatePair(text, i) ? 2 : 1;
                PushIndex(output, Lookup(text.Substring(i, n)));
                i += n;
            }
        }
        return [.. output];
    }

    private int Lookup(string c) =>
        _index.TryGetValue(c, out int i) ? i : throw new TextEncodeException($"character {c} is not in this language's charset");

    private void PushEscape(List<byte> output, string escape) {
        TextEncodeException Bad() => new($"malformed escape {{{escape}}}");
        static byte? HexByte(string s) => s.Length == 2 && byte.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte b) ? b : null;
        if (escape.StartsWith("raw:", StringComparison.Ordinal)) {
            output.Add(HexByte(escape[4..]) ?? throw Bad());
        }
        else if (escape.StartsWith("c:", StringComparison.Ordinal)) {
            if (!int.TryParse(escape[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int index)) throw Bad();
            PushIndex(output, index);
        }
        else if (escape.IndexOf(':') is int colon and >= 0) {
            byte code = HexByte(escape[..colon]) is byte c and >= 0x06 and <= 0x2b ? c : throw Bad();
            output.Add(code);
            output.Add(HexByte(escape[(colon + 1)..]) ?? throw Bad());
        }
        else {
            output.Add(HexByte(escape) is byte c and (0x01 or 0x02 or 0x05) ? c : throw Bad());
        }
    }

#pragma warning disable CA1822 // an instance method, as ffx-rust's
    private void PushIndex(List<byte> output, int index) {
#pragma warning restore CA1822
        int original = index;
        if (index >= Bank) {
            output.Add(0x04);
            index -= Bank;
        }
        if (index is < 0 or >= Bank) throw new TextEncodeException($"character index 0x{original:x} cannot be encoded");
        if (index < Page) {
            output.Add((byte)(0x30 + index));
        }
        else {
            output.Add((byte)(0x2b + index / Page));
            output.Add((byte)(0x30 + index % Page));
        }
    }

    /// <summary>Reads one character code at the start: (index, byte length).</summary>
    private static (int Index, int Length)? ReadChar(ReadOnlySpan<byte> bytes) {
        if (bytes.Length >= 2 && bytes[0] is >= 0x2c and <= 0x2f && bytes[1] >= 0x30) return ((bytes[0] - 0x2b) * Page + (bytes[1] - 0x30), 2);
        if (bytes.Length >= 1 && bytes[0] >= 0x30) return (bytes[0] - 0x30, 1);
        return null;
    }
}
