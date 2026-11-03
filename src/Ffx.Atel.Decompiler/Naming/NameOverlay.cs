// SPDX-License-Identifier: MIT

using System.Globalization;
using Ffx.Atel.Bytecode;
using Microsoft.CodeAnalysis.CSharp;

namespace Ffx.Atel.Decompiler.Naming;

/// <summary>
///     Hand-written names for one script, kept in this repo (<c>data/names/events/kami0000.toml</c>,
///     docs/source-repo.md, "Names"; embedded in the decompiler) and applied by the decompiler, so that a
///     rename survives the next decompile and every decompile names the same. Our own names keyed by
///     numbers: no game code or text. Every key is something the bytes keep from one decompile
///     to the next: a worker by its number, an entry by worker and entry number, a variable by its storage
///     and offset. Names only: none of them changes what the source compiles to.
/// </summary>
/// <remarks>
///     The file is a small subset of TOML: <c># comments</c>, <c>[workers]</c>, <c>[entries]</c> and
///     <c>[variables]</c> sections, and <c>key = "Name"</c> lines.
///     <code>
///     [workers]
///     18 = "LightningTowers"         # W18
///
///     [entries]
///     "18.2" = "StrikeNearPlayer"    # W18's entry 2: StrikeNearPlayer(), or StrikeNearPlayerAsync() when requested
///
///     [variables]
///     "Event:0x0012" = "DodgeCount"  # [Var(Storage.Event, 0x0012)]
///     </code>
/// </remarks>
public sealed class NameOverlay {
    /// <summary>The overlay that names nothing.</summary>
    public static NameOverlay Empty { get; } = new("", new Dictionary<int, string>(), new Dictionary<(int, int), string>(), new Dictionary<(int, int), string>());

    private NameOverlay(string source, IReadOnlyDictionary<int, string> workers, IReadOnlyDictionary<(int, int), string> entries, IReadOnlyDictionary<(int, int), string> variables) {
        Source = source;
        Workers = workers;
        Entries = entries;
        Variables = variables;
    }

    /// <summary>Where the names come from (the file, for messages).</summary>
    public string Source { get; }

    /// <summary>Worker class names by worker number.</summary>
    public IReadOnlyDictionary<int, string> Workers { get; }

    /// <summary>Entry method names (without <c>Async</c>) by worker and entry number.</summary>
    public IReadOnlyDictionary<(int Worker, int Entry), string> Entries { get; }

    /// <summary>Variable names by storage (<see cref="Storage"/>) and offset.</summary>
    public IReadOnlyDictionary<(int Storage, int Offset), string> Variables { get; }

    public bool IsEmpty => Workers.Count == 0 && Entries.Count == 0 && Variables.Count == 0;

    /// <summary>The file of a script's names under a names folder: <c>events/kami0000.toml</c>.</summary>
    public static string RelativePath(ScriptCategory category, string name) => $"{CategoryFolder(category)}/{name}.toml";

    /// <summary><c>events</c>, <c>monsters</c>, <c>battles</c>.</summary>
    public static string CategoryFolder(ScriptCategory category) => category switch {
        ScriptCategory.Event => "events",
        ScriptCategory.Monster => "monsters",
        _ => "battles",
    };

    /// <summary>
    ///     A script's names: from <paramref name="folder"/> when given (a folder laid out as <c>data/names/</c>),
    ///     else this repo's <c>data/names/</c>, embedded in the decompiler. <see cref="Empty"/> when the script has
    ///     no file there.
    /// </summary>
    /// <exception cref="FormatException">The file is not a names file.</exception>
    public static NameOverlay Load(string? folder, string archivePath) {
        if (GameData.Identify(archivePath) is not var (category, name)) return Empty;
        string relative = RelativePath(category, name);
        if (folder is not null) {
            string path = Path.Combine(folder, relative);
            return File.Exists(path) ? Parse(File.ReadAllText(path), relative) : Empty;
        }
        return Embedded.Value.TryGetValue(relative, out string? text) ? Parse(text, "data/names/" + relative) : Empty;
    }

    /// <summary>The prefix of the embedded files' resource names (<c>names/events/kami0000.toml</c>).</summary>
    private const string ResourcePrefix = "names/";

    /// <summary>The embedded <c>data/names/</c> files, by path below it (<c>events/kami0000.toml</c>).</summary>
    private static readonly Lazy<Dictionary<string, string>> Embedded = new(() => {
        System.Reflection.Assembly assembly = typeof(NameOverlay).Assembly;
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        foreach (string resource in assembly.GetManifestResourceNames()) {
            string normal = resource.Replace('\\', '/');
            if (!normal.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !normal.EndsWith(".toml", StringComparison.Ordinal)) continue;
            using StreamReader reader = new(assembly.GetManifestResourceStream(resource)!);
            files[normal[ResourcePrefix.Length..]] = reader.ReadToEnd();
        }
        return files;
    });

    /// <summary>The scripts the embedded <c>data/names/</c> names (<c>events/kami0000.toml</c>).</summary>
    public static IReadOnlyCollection<string> EmbeddedFiles => Embedded.Value.Keys;

    /// <summary>Reads a names file.</summary>
    /// <param name="source">The file's name, for messages.</param>
    /// <exception cref="FormatException">A line is not a section, a <c>key = "Name"</c> pair or a comment; a key or name is not valid.</exception>
    public static NameOverlay Parse(string text, string source) {
        Dictionary<int, string> workers = [];
        Dictionary<(int, int), string> entries = [];
        Dictionary<(int, int), string> variables = [];
        string? section = null;
        string[] lines = text.Split('\n');
        for (int n = 0; n < lines.Length; n++) {
            string line = StripComment(lines[n]).Trim();
            if (line.Length == 0) continue;
            string where = string.Create(CultureInfo.InvariantCulture, $"{source}:{n + 1}");
            if (line.StartsWith('[')) {
                if (!line.EndsWith(']')) throw new FormatException($"{where}: a section is [workers], [entries] or [variables]");
                section = line[1..^1].Trim();
                if (section is not ("workers" or "entries" or "variables")) throw new FormatException($"{where}: unknown section [{section}]; expected [workers], [entries] or [variables]");
                continue;
            }
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) throw new FormatException($"{where}: expected key = \"Name\"");
            string key = Unquote(line[..eq].Trim());
            string value = line[(eq + 1)..].Trim();
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"') throw new FormatException($"{where}: the name is a quoted string");
            value = value[1..^1];
            if (!IsIdentifier(value)) throw new FormatException($"{where}: \"{value}\" is not a C# name (letters, digits and _, not starting with a digit, not a keyword)");
            switch (section) {
                case "workers":
                    if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int w)) throw new FormatException($"{where}: a worker is its number (18 = \"Name\")");
                    if (!workers.TryAdd(w, value)) throw new FormatException($"{where}: worker {w} is named twice");
                    break;
                case "entries":
                    string[] parts = key.Split('.');
                    if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int ew) ||
                        !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int e)) {
                        throw new FormatException($"{where}: an entry is \"worker.entry\" (\"18.2\" = \"Name\")");
                    }
                    if (value.EndsWith("Async", StringComparison.Ordinal) && value.Length > 5) value = value[..^5];
                    if (!entries.TryAdd((ew, e), value)) throw new FormatException($"{where}: entry {key} is named twice");
                    break;
                case "variables":
                    if (VariableKey(key) is not var (storage, offset)) {
                        throw new FormatException($"{where}: a variable is \"Storage:offset\" as in its [Var(Storage.Event, 0x0012)] (\"Event:0x0012\" = \"Name\")");
                    }
                    if (!variables.TryAdd((storage, offset), value)) throw new FormatException($"{where}: variable {key} is named twice");
                    break;
                default:
                    throw new FormatException($"{where}: a name before any section; start with [workers], [entries] or [variables]");
            }
        }
        return new NameOverlay(source, workers, entries, variables);
    }

    /// <summary><c>Event:0x0012</c> → (6, 0x12); the storage by its <see cref="Storage"/> name or number.</summary>
    private static (int Storage, int Offset)? VariableKey(string key) {
        int colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return null;
        string s = key[..colon].Trim(), o = key[(colon + 1)..].Trim();
        int storage;
        if (Enum.TryParse(s, ignoreCase: false, out Storage named) && Enum.IsDefined(named) && !char.IsAsciiDigit(s[0])) storage = (int)named;
        else if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out storage) || storage > 7) return null;
        bool hex = o.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return int.TryParse(hex ? o[2..] : o, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int offset) ? (storage, offset) : null;
    }

    internal static string StripComment(string line) {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++) {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == '#' && !quoted) return line[..i];
        }
        return line;
    }

    internal static string Unquote(string key) => key.Length >= 2 && key[0] == '"' && key[^1] == '"' ? key[1..^1] : key;

    /// <summary>A C# identifier that is not a keyword (contextual keywords are fine).</summary>
    public static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') &&
        SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;
}
