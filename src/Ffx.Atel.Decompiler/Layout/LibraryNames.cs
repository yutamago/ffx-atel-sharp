// SPDX-License-Identifier: MIT

using System.Globalization;
using Ffx.Atel.Decompiler.Naming;

namespace Ffx.Atel.Decompiler.Layout;

/// <summary>
///     Hand-written names for library classes (<c>data/library-names.toml</c>, embedded in the decompiler;
///     docs/source-repo.md, "Library names"), keyed by the <see cref="WorkerSignature"/> of the code their
///     workers share. The numbers of <c>Shared0108</c> follow the order the classes are first used in, which
///     shifts when a script's workers stop or start sharing; the signature does not. A class without a name
///     here keeps its number.
/// </summary>
/// <remarks>
///     The file is a small subset of TOML: <c># comments</c>, a <c>[classes]</c> section with
///     <c>"signature" = "Name"</c> lines, and an optional <c>[summaries]</c> section with
///     <c>"signature" = "what it does"</c> lines (the class's doc comment).
///     <code>
///     [classes]
///     "0123456789abcdef" = "EventCamera"
///
///     [summaries]
///     "0123456789abcdef" = "Attaches camera 1."
///     </code>
/// </remarks>
public sealed class LibraryNames {
    /// <summary>No names: every class keeps its number.</summary>
    public static LibraryNames Empty { get; } = new(new Dictionary<string, string>(), new Dictionary<string, string>());

    private LibraryNames(IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<string, string> summaries) {
        Names = names;
        Summaries = summaries;
    }

    /// <summary>Class names by signature.</summary>
    public IReadOnlyDictionary<string, string> Names { get; }

    /// <summary>What a class does, by signature (its doc comment).</summary>
    public IReadOnlyDictionary<string, string> Summaries { get; }

    /// <summary>The resource name of the embedded file.</summary>
    private const string Resource = "library-names.toml";

    /// <summary>This repo's <c>data/library-names.toml</c>, embedded in the decompiler.</summary>
    public static LibraryNames Default => s_default.Value;

    private static readonly Lazy<LibraryNames> s_default = new(() => {
        System.Reflection.Assembly assembly = typeof(LibraryNames).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(Resource);
        if (stream is null) return Empty;
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd(), "data/" + Resource);
    });

    /// <summary>Reads a library names file.</summary>
    /// <param name="source">The file's name, for messages.</param>
    /// <exception cref="FormatException">A line is not a section, a pair or a comment; a key is not a signature, a name not a C# name, a key given twice.</exception>
    public static LibraryNames Parse(string text, string source) {
        Dictionary<string, string> names = new(StringComparer.Ordinal), summaries = new(StringComparer.Ordinal);
        HashSet<string> given = new(StringComparer.Ordinal);
        string? section = null;
        string[] lines = text.Split('\n');
        for (int n = 0; n < lines.Length; n++) {
            string line = NameOverlay.StripComment(lines[n]).Trim();
            if (line.Length == 0) continue;
            string where = string.Create(CultureInfo.InvariantCulture, $"{source}:{n + 1}");
            if (line.StartsWith('[')) {
                if (!line.EndsWith(']')) throw new FormatException($"{where}: a section is [classes] or [summaries]");
                section = line[1..^1].Trim();
                if (section is not ("classes" or "summaries")) throw new FormatException($"{where}: unknown section [{section}]; expected [classes] or [summaries]");
                continue;
            }
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) throw new FormatException($"{where}: expected \"signature\" = \"…\"");
            string key = NameOverlay.Unquote(line[..eq].Trim());
            string value = line[(eq + 1)..].Trim();
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"') throw new FormatException($"{where}: the value is a quoted string");
            value = value[1..^1];
            if (key.Length != WorkerSignature.Length || !key.All(char.IsAsciiHexDigitLower)) {
                throw new FormatException($"{where}: a key is a worker signature, {WorkerSignature.Length} lower-case hex digits");
            }
            switch (section) {
                case "classes":
                    if (!NameOverlay.IsIdentifier(value)) throw new FormatException($"{where}: \"{value}\" is not a C# name");
                    if (IsNumbered(value)) throw new FormatException($"{where}: \"{value}\" is a numbered library name (SharedNNNN)");
                    if (!names.TryAdd(key, value)) throw new FormatException($"{where}: {key} is named twice");
                    if (!given.Add(value)) throw new FormatException($"{where}: the name {value} is given twice");
                    break;
                case "summaries":
                    if (value.Length == 0) throw new FormatException($"{where}: an empty summary");
                    if (!summaries.TryAdd(key, value)) throw new FormatException($"{where}: {key} has two summaries");
                    break;
                default:
                    throw new FormatException($"{where}: a line before any section; start with [classes]");
            }
        }
        foreach (string key in summaries.Keys) {
            if (!names.ContainsKey(key)) throw new FormatException($"{source}: the summary of {key} names no class in [classes]");
        }
        return new LibraryNames(names, summaries);
    }

    /// <summary>Whether a name is one the library numbers (<c>Shared0108</c>), which a hand name may not take.</summary>
    public static bool IsNumbered(string name) => name.StartsWith("Shared", StringComparison.Ordinal) && name.Length > 6 && name[6..].All(char.IsAsciiDigit);
}
