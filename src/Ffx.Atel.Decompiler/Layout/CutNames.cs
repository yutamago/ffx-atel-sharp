// SPDX-License-Identifier: MIT

using System.Globalization;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler.Naming;
using Ffx.Atel.Linker;

namespace Ffx.Atel.Decompiler.Layout;

/// <summary>
///     Names for dead <c>if (false)</c> blocks (<c>data/cut-names.toml</c>, embedded in the decompiler;
///     docs/research/dead-code.md, "Names"), keyed by the <see cref="DeadSignature"/> of the dead code. A named
///     block is written <c>if (Cut.ZanarkandCrowdFacingReaction)</c>, a <c>const bool</c> of <c>Ffx.Cut</c> that
///     is <c>false</c>, so the source still compiles to the shipped bytes. Several signatures may share a
///     name: the copies of one feature in several workers are one switch.
/// </summary>
/// <remarks>
///     The file is a small subset of TOML: <c># comments</c>, a <c>[cuts]</c> section with
///     <c>"signature" = "Name"</c> lines, and a <c>[scripts]</c> section with the scripts each name's blocks are
///     in (<c>Name = ["script", …]</c>), which <c>atel compile-mod</c> reads for
///     <c>[assembly: EnableCut(…)]</c>. What a name means is the doc comment of its constant in
///     <c>src/Ffx.Atel.Game/Cut.cs</c>.
///     <code>
///     [cuts]
///     "0123456789abcdef" = "ZanarkandCrowdFacingReaction"
///
///     [scripts]
///     ZanarkandCrowdFacingReaction = ["znkd1300"]
///     </code>
/// </remarks>
public sealed class CutNames {
    /// <summary>No names: every dead block stays <c>if (false)</c>.</summary>
    public static CutNames Empty { get; } = new(new Dictionary<string, string>());

    private CutNames(IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<string, string[]>? scripts = null) {
        Names = names;
        Scripts = scripts ?? new Dictionary<string, string[]>();
    }

    /// <summary>Names by signature.</summary>
    public IReadOnlyDictionary<string, string> Names { get; }

    /// <summary>The scripts each name's blocks are in (<c>[scripts]</c>), by name.</summary>
    public IReadOnlyDictionary<string, string[]> Scripts { get; }

    /// <summary>The resource name of the embedded file.</summary>
    private const string Resource = "cut-names.toml";

    /// <summary>This repo's <c>data/cut-names.toml</c>, embedded in the decompiler.</summary>
    public static CutNames Default => s_default.Value;

    private static readonly Lazy<CutNames> s_default = new(() => {
        System.Reflection.Assembly assembly = typeof(CutNames).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(Resource);
        if (stream is null) return Empty;
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd(), "data/" + Resource);
    });

    /// <summary>Reads a cut names file.</summary>
    /// <param name="source">The file's name, for messages.</param>
    /// <exception cref="FormatException">A line is not a section, a pair or a comment; a key is not a signature, a name not a C# name, a key given twice.</exception>
    public static CutNames Parse(string text, string source) {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        Dictionary<string, string[]> scripts = new(StringComparer.Ordinal);
        Dictionary<string, string> scriptLines = new(StringComparer.Ordinal);
        string? section = null;
        string[] lines = text.Split('\n');
        for (int n = 0; n < lines.Length; n++) {
            string line = NameOverlay.StripComment(lines[n]).Trim();
            if (line.Length == 0) continue;
            string where = string.Create(CultureInfo.InvariantCulture, $"{source}:{n + 1}");
            if (line.StartsWith('[')) {
                if (line is not ("[cuts]" or "[scripts]")) throw new FormatException($"{where}: unknown section {line}; expected [cuts] or [scripts]");
                section = line;
                continue;
            }
            if (section is null) throw new FormatException($"{where}: a line before any section; start with [cuts]");
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (section == "[scripts]") {
                if (eq <= 0) throw new FormatException($"{where}: expected Name = [\"script\", …]");
                string name = line[..eq].Trim();
                string list = line[(eq + 1)..].Trim();
                if (!NameOverlay.IsIdentifier(name)) throw new FormatException($"{where}: \"{name}\" is not a C# name");
                if (list.Length < 2 || list[0] != '[' || list[^1] != ']') throw new FormatException($"{where}: the value is a list of quoted script names");
                string[] items = [.. list[1..^1].Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Select(x =>
                    x.Length >= 2 && x[0] == '"' && x[^1] == '"' && x[1..^1].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')
                        ? x[1..^1] : throw new FormatException($"{where}: {x} is not a quoted script name (lower case, as atel list shows it)"))];
                if (items.Length == 0) throw new FormatException($"{where}: {name} lists no script");
                if (!scripts.TryAdd(name, items)) throw new FormatException($"{where}: {name} is listed twice");
                scriptLines[name] = where;
                continue;
            }
            if (eq <= 0) throw new FormatException($"{where}: expected \"signature\" = \"Name\"");
            string key = NameOverlay.Unquote(line[..eq].Trim());
            string value = line[(eq + 1)..].Trim();
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"') throw new FormatException($"{where}: the value is a quoted string");
            value = value[1..^1];
            if (key.Length != DeadSignature.Length || !key.All(char.IsAsciiHexDigitLower)) {
                throw new FormatException($"{where}: a key is a dead code signature, {DeadSignature.Length} lower-case hex digits");
            }
            if (!NameOverlay.IsIdentifier(value)) throw new FormatException($"{where}: \"{value}\" is not a C# name");
            if (!names.TryAdd(key, value)) throw new FormatException($"{where}: {key} is named twice");
        }
        foreach ((string name, string where) in scriptLines) {
            if (!names.ContainsValue(name)) throw new FormatException($"{where}: {name} names no block in [cuts]");
        }
        return new CutNames(names, scripts);
    }

    /// <summary>The signatures of <paramref name="name"/>'s blocks; empty for a name the file does not have.</summary>
    public string[] SignaturesOf(string name) => [.. Names.Where(kv => kv.Value == name).Select(kv => kv.Key).Order(StringComparer.Ordinal)];

    /// <summary>
    ///     What a mod's package for the script <paramref name="vanilla"/> carries to switch <paramref name="name"/> on:
    ///     its signatures and how many blocks with them the script has (<see cref="DeadJumps"/>); null when it has none.
    /// </summary>
    public CutSwitch? Switch(string name, ScriptBlock vanilla) {
        string[] signatures = SignaturesOf(name);
        int blocks = DeadJumps.Find(vanilla, signatures.ToHashSet(StringComparer.Ordinal)).Count;
        return blocks == 0 ? null : new CutSwitch(name, signatures, blocks);
    }

    /// <summary>The name of the dead code with <paramref name="signature"/>, or null.</summary>
    public string? NameOf(string signature) => Names.GetValueOrDefault(signature);
}
