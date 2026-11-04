// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ffx.Atel.Decompiler.Layout;

/// <summary>
///     Hand-written names for the inline methods the decompiler writes for a worker's repeated code
///     (<c>data/inline-names.toml</c>, embedded in the decompiler): by the signature of the method's body
///     (<see cref="Signature"/>), a name and, optionally, the parameters' names.
/// </summary>
public sealed partial class InlineNames {
    private readonly Dictionary<string, (string Name, IReadOnlyList<string>? Params)> _names;

    private InlineNames(Dictionary<string, (string, IReadOnlyList<string>?)> names) => _names = names;

    /// <summary>No names.</summary>
    public static InlineNames Empty { get; } = new([]);

    /// <summary>This repo's <c>data/inline-names.toml</c>.</summary>
    public static InlineNames Default => s_default.Value;

    private static readonly Lazy<InlineNames> s_default = new(() => {
        using Stream? stream = typeof(InlineNames).Assembly.GetManifestResourceStream("inline-names.toml");
        if (stream is null) return Empty;
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd(), "data/inline-names.toml");
    });

    /// <summary>The name and parameter names for a body's signature, if the table has them.</summary>
    public (string Name, IReadOnlyList<string>? Params)? Find(string signature) => _names.TryGetValue(signature, out var x) ? x : null;

    /// <summary>
    ///     A body's signature: 16 hex digits of a hash of its C# (as written with parameters <c>p0</c>, <c>p1</c>, …)
    ///     with the script's numbered variables (<c>V12</c>) as <c>V</c>, so the same code names the same in every script.
    /// </summary>
    public static string Signature(string code) {
        string text = Variables().Replace(code, "V").Replace("\r", "", StringComparison.Ordinal);
        ulong h = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(text)) {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\bV\d+\b")]
    private static partial Regex Variables();

    /// <summary>Reads a names file: <c>"signature" = "Name"</c> or <c>"Name(a, b)"</c> under <c>[helpers]</c>.</summary>
    /// <exception cref="FormatException">A line is not a section, a pair or a comment; a name not a C# name; a key given twice.</exception>
    public static InlineNames Parse(string text, string source) {
        Dictionary<string, (string, IReadOnlyList<string>?)> names = new(StringComparer.Ordinal);
        bool inSection = false;
        string[] lines = text.Split('\n');
        for (int n = 0; n < lines.Length; n++) {
            string line = Naming.NameOverlay.StripComment(lines[n]).Trim();
            if (line.Length == 0) continue;
            string where = string.Create(CultureInfo.InvariantCulture, $"{source}:{n + 1}");
            if (line == "[helpers]") {
                inSection = true;
                continue;
            }
            if (!inSection) throw new FormatException($"{where}: start with [helpers]");
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) throw new FormatException($"{where}: expected \"signature\" = \"Name\"");
            string key = Naming.NameOverlay.Unquote(line[..eq].Trim());
            string value = line[(eq + 1)..].Trim();
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"') throw new FormatException($"{where}: the value is a quoted string");
            value = value[1..^1];
            if (key.Length != 16 || !key.All(char.IsAsciiHexDigitLower)) throw new FormatException($"{where}: a key is a signature, 16 lower-case hex digits");
            string name = value;
            IReadOnlyList<string>? ps = null;
            int open = value.IndexOf('(', StringComparison.Ordinal);
            if (open >= 0) {
                if (!value.EndsWith(')')) throw new FormatException($"{where}: Name(a, b)");
                name = value[..open];
                ps = [.. value[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
                if (ps.Any(p => !Naming.NameOverlay.IsIdentifier(p))) throw new FormatException($"{where}: a parameter is a C# name");
            }
            if (!Naming.NameOverlay.IsIdentifier(name)) throw new FormatException($"{where}: \"{name}\" is not a C# name");
            if (!names.TryAdd(key, (name, ps))) throw new FormatException($"{where}: {key} is named twice");
        }
        return new InlineNames(names);
    }
}
