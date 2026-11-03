// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Text;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Decompiler.Layout;

namespace Ffx.Atel.Cli;

/// <summary>
///     <c>atel names export</c>: the game's names behind the typed values of the source (monsters, commands and
///     items, maps, battles), per language, as JSON beside the decompiled source, for the editor extensions
///     (<c>docs/editor.md</c>, "Names"). The counterpart of <see cref="TextCommand" /> for the name tables.
/// </summary>
internal static class NamesCommand {
    public const string Usage = """
          names export [-o <source dir>] [--lang en,de,…]
                                           the game's names for Target (monsters), Command (commands,
                                           items, abilities, key items), Map (places) and Battle (the
                                           formation's monsters), as <source dir>/.atel/names/<lang>/
                                           <table>.json for the editor extensions (docs/editor.md);
                                           folder and languages as text export; game text: stays on
                                           your machine
        """;

    /// <summary>Where the files go under a source root: <c>.atel/names</c>, which the root's <c>.gitignore</c> ignores.</summary>
    public static readonly string Folder = Path.Combine(ProjectLayout.ToolFolder, "names");

    private static readonly JsonSerializerOptions s_json = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    ///     The kernel tables of the ids <c>MsGetComData</c> (<c>0x0079a4b0</c>) names by their kind, <c>id &gt;&gt; 12</c>
    ///     (ffx-rust <c>ffx_battle::stat::com_data</c>): 2 items, 3 commands, 4 and 6 monster commands, 8
    ///     auto-abilities, <c>0xa</c> key items (<c>MsImportantName</c>); the record is <c>id &amp; 0xfff</c>.
    /// </summary>
    internal static readonly (int Kind, string Table)[] CommandTables =
        [(0x2, "item"), (0x3, "command"), (0x4, "monmagic1"), (0x6, "monmagic2"), (0x8, "a_ability"), (0xa, "important")];

    /// <summary>The monster tables, record id = monster id (<c>m</c><i>nnn</i>): <c>monster1</c> 0-100, <c>monster2</c> 101-180, <c>monster3</c> 181-.</summary>
    internal static readonly string[] MonsterTables = ["monster1", "monster2", "monster3"];

    /// <summary>One name: the value the source uses (<paramref name="Id" />), its member if the type has one, the name.</summary>
    internal sealed record Entry(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("member")] string? Member,
        [property: JsonPropertyName("name")] string Name);

    internal sealed record NamesFile(
        [property: JsonPropertyName("table")] string Table,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("names")] IReadOnlyList<Entry> Names);

    public static int Run(VbfArchive archive, List<string> args, string? output) {
        if (args.Count == 0 || args[0] != "export") throw new Program.UsageException("names export [-o <source dir>] [--lang en,de,…]");
        args.RemoveAt(0);
        string? langOption = TakeOption(args, "--lang");
        if (args.Count > 0) throw new Program.UsageException($"names export takes no scripts ({args[0]})");
        IReadOnlyList<GameLanguage> languages = langOption is null
            ? GameLanguages.All
            : [.. langOption.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(TextCommand.ParseLanguage).Distinct()];
        string root = output ?? (File.Exists(ProjectLayout.Solution) ? "." : throw new Program.UsageException("names export needs -o <source dir> (the folder atel decompile wrote)"));

        // What does not depend on the language: each map's save locations, each battle's monsters.
        List<(int Id, string Member, List<int> Places)> maps = MapPlaces(archive);
        List<(int Id, string Member, List<int> Monsters)> battles = BattleMonsters(archive);
        ScriptMessages messages = new(archive);
        List<string> counts = [];
        foreach (GameLanguage language in languages) {
            string dir = Path.Combine(root, Folder, language.IsoCode());
            Directory.CreateDirectory(dir);
            Charset charset = messages.Charset(language);
            Dictionary<int, string> monsterNames = Monsters(archive, language, charset);
            List<NamesFile> files = [
                new("monsters", "Target", language.IsoCode(), Members(typeof(Ffx.Target), monsterNames.Select(m => (0x1000 | m.Key, m.Value)))),
                new("commands", "Command", language.IsoCode(), Members(typeof(Ffx.Command), Commands(archive, language, charset))),
                new("maps", "Map", language.IsoCode(), Members(typeof(Ffx.Map), MapNames(maps, AreaIndex.Places(archive, language)))),
                new("battles", "Battle", language.IsoCode(), Members(typeof(Ffx.Battle), BattleNames(battles, monsterNames))),
            ];
            foreach (NamesFile file in files) File.WriteAllText(Path.Combine(dir, file.Table + ".json"), Json(file));
            counts.Add($"{language.IsoCode()}: " + string.Join(", ", files.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Table} {f.Names.Count}"))));
        }
        foreach (string line in counts) Console.Error.WriteLine(line);
        Console.Error.WriteLine($"{languages.Count * 4} files in {languages.Count} languages: {Path.Combine(root, Folder)}");
        return 0;
    }

    public static string Json(NamesFile file) => JsonSerializer.Serialize(file, s_json) + "\n";

    /// <summary>
    ///     The entries of a table, by id, each with the member of <paramref name="type" /> (an enum, or a class of
    ///     <c>int</c> constants as <see cref="Ffx.Battle" />) that has the id, if one does.
    /// </summary>
    internal static List<Entry> Members(Type type, IEnumerable<(int Id, string Name)> names) {
        Dictionary<int, string> members = [];
        foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral)) {
            members.TryAdd(Convert.ToInt32(f.GetRawConstantValue(), CultureInfo.InvariantCulture), f.Name);
        }
        return [.. names.OrderBy(n => n.Id).Select(n => new Entry(n.Id, members.GetValueOrDefault(n.Id), n.Name))];
    }

    /// <summary>A name as the editor shows it; null for none (empty, or only spaces).</summary>
    private static string? Name(Charset charset, byte[]? bytes) {
        if (bytes is null) return null;
        string name = charset.Display(bytes).Trim();
        return name.Length == 0 ? null : name;
    }

    private static KernelTable? Kernel(VbfArchive archive, GameLanguage language, string table) {
        string path = KernelTable.PathOf(language, table);
        return archive.Find(path) is null ? null : KernelTable.Parse(archive.Read(path));
    }

    /// <summary>Monster id → name (string 0 of <c>monster1</c>-<c>3</c>; <c>MsBtlReadMonster</c> copies it into the actor).</summary>
    private static Dictionary<int, string> Monsters(VbfArchive archive, GameLanguage language, Charset charset) {
        Dictionary<int, string> names = [];
        foreach (string t in MonsterTables) {
            if (Kernel(archive, language, t) is not KernelTable table) continue;
            foreach (int id in table.Ids) {
                if (Name(charset, table.String(id)) is string name) names.TryAdd(id, name);
            }
        }
        return names;
    }

    /// <summary>Command id (<c>kind &lt;&lt; 12 | record</c>) → name, over <see cref="CommandTables" />.</summary>
    private static IEnumerable<(int, string)> Commands(VbfArchive archive, GameLanguage language, Charset charset) {
        foreach ((int kind, string t) in CommandTables) {
            if (Kernel(archive, language, t) is not KernelTable table) continue;
            foreach (int id in table.Ids.Where(i => i < 0x1000)) {
                if (Name(charset, table.String(id)) is string name) yield return (kind << 12 | id, name);
            }
        }
    }

    /// <summary>
    ///     Each map (<c>event/header/eventid.bin</c>'s index, as <see cref="Ffx.Map" />) whose event has a script, with
    ///     the save locations the script names (<see cref="AreaIndex.PlaceIndices" />).
    /// </summary>
    private static List<(int, string, List<int>)> MapPlaces(VbfArchive archive) {
        Dictionary<string, string> events = GameData.ScriptPaths(archive)
            .Where(p => p.StartsWith(GameData.EventPrefix, StringComparison.Ordinal))
            .ToDictionary(p => GameData.Identify(p)!.Value.Name, StringComparer.Ordinal);
        List<(int, string, List<int>)> maps = [];
        foreach (FieldInfo f in typeof(Ffx.Map).GetFields(BindingFlags.Public | BindingFlags.Static)) {
            if (!events.TryGetValue(f.Name.ToLowerInvariant(), out string? path)) continue;
            List<int> places = [.. AreaIndex.PlaceIndices(archive.Read(path)).Where(i => i > 0).Distinct()];
            if (places.Count > 0) maps.Add(((int)f.GetRawConstantValue()!, f.Name, places));
        }
        return maps;
    }

    /// <summary>A map's name: its save locations (macro dictionary section 11), several joined by <c>/</c>.</summary>
    private static IEnumerable<(int, string)> MapNames(List<(int Id, string Member, List<int> Places)> maps, string[]? places) {
        if (places is null) yield break;
        foreach ((int id, _, List<int> indices) in maps) {
            List<string> names = [.. indices.Where(i => i < places.Length).Select(i => places[i].Trim()).Where(n => n.Length > 0).Distinct()];
            if (names.Count > 0) yield return (id, string.Join(" / ", names));
        }
    }

    /// <summary>Each <see cref="Ffx.Battle" /> with a battle file (<c>battle/btl/&lt;name&gt;/</c>), with the monsters of its formation.</summary>
    private static List<(int, string, List<int>)> BattleMonsters(VbfArchive archive) {
        Dictionary<string, string> files = [];
        foreach (string p in GameData.BattlePaths(archive)) files.TryAdd(GameData.Identify(p)!.Value.Name, p);
        List<(int, string, List<int>)> battles = [];
        foreach (FieldInfo f in typeof(Ffx.Battle).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral)) {
            if (!files.TryGetValue(f.Name.ToLowerInvariant(), out string? path)) continue;
            List<int> monsters = AreaIndex.Monsters(archive.Read(path));
            if (monsters.Count > 0) battles.Add(((int)f.GetRawConstantValue()!, f.Name, monsters));
        }
        return battles;
    }

    /// <summary>A battle's name: its formation's monster names, in slot order, joined by <c>, </c>.</summary>
    private static IEnumerable<(int, string)> BattleNames(List<(int Id, string Member, List<int> Monsters)> battles, Dictionary<int, string> monsters) {
        foreach ((int id, _, List<int> ids) in battles) {
            List<string> names = [.. ids.Select(m => monsters.GetValueOrDefault(m)).OfType<string>().Distinct()];
            if (names.Count > 0) yield return (id, string.Join(", ", names));
        }
    }

    private static string? TakeOption(List<string> args, string name) {
        int i = args.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= args.Count) throw new Program.UsageException($"{name} needs a value");
        string value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }
}
