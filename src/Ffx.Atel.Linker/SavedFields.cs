// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ffx.Atel.Linker;

// Saved data for mods (docs/research/mod-saves.md, docs/formats/mod-package.md "Saved fields"). A [Saved] field is a
// storage 0 variable in the save's free tail; the loader gives every enabled mod's fields their offsets there, from one
// table, before it links any file, and keeps their values by key in its Fahrenheit local state.

/// <summary>The part of storage 0 that no vanilla script and no part of FFX.exe uses: the save's last <c>0x408</c> bytes.</summary>
public static class SaveArea {
    /// <summary>
    ///     The first storage 0 offset of the area (<c>g_SaveData + 0x64b8</c>; storage 0 starts at <c>g_SaveData + 0x1ec</c>).
    /// </summary>
    public const int Start = 0x62cc;

    /// <summary>The area's length in bytes: <c>g_SaveData + 0x64b8</c> .. <c>+0x68c0</c>.</summary>
    public const int Length = 0x408;

    /// <summary>The area's start in <c>g_SaveData</c> (the game's save block).</summary>
    public const int BlockOffset = 0x64b8;

    /// <summary>The area's start in a save file (the block follows the file's <c>0x40</c>-byte header).</summary>
    public const int FileOffset = 0x64f8;

    /// <summary>A save file's length on PC.</summary>
    public const int FileLength = 0x6900;
}

/// <summary>
///     One <c>[Saved]</c> field a mod declares: its key (<c>LarvaCount.Progress.LarvasMet</c>, or the field's <c>Key</c>),
///     its variable type and length, and its initial value. The full key, the one the loader saves under, is
///     <c>&lt;mod id&gt;/&lt;key&gt;</c>.
/// </summary>
public sealed class SavedField {
    public string Key { get; set; } = "";

    /// <summary>The variable type: 0 <c>u8</c>, 1 <c>i8</c>, 2 <c>u16</c>, 3 <c>i16</c>, 4 <c>u32</c>, 5 <c>i32</c>, 6 <c>f32</c>.</summary>
    public int Type { get; set; }

    /// <summary>Elements (an array's size; 1 for a single value).</summary>
    public int Length { get; set; } = 1;

    public int RowLength { get; set; }

    /// <summary>Declared as an array (location bit 24).</summary>
    public bool Array { get; set; }

    /// <summary>The initial value, little-endian, at most <see cref="Size"/> bytes (the rest are zeros); null for zeros.</summary>
    public byte[]? Initial { get; set; }

    [JsonIgnore]
    public int ElementSize => Type switch { 0 or 1 => 1, 2 or 3 => 2, _ => 4 };

    /// <summary>Its bytes in the save area.</summary>
    [JsonIgnore]
    public int Size => ElementSize * Length;

    /// <summary>The initial value as the field's whole bytes.</summary>
    public byte[] InitialBytes() {
        byte[] bytes = new byte[Size];
        Initial?.AsSpan(0, Math.Min(Initial.Length, bytes.Length)).CopyTo(bytes);
        return bytes;
    }

    /// <summary>The key of a field of mod <paramref name="mod"/>: <c>&lt;mod&gt;/&lt;key&gt;</c>.</summary>
    public static string FullKey(string mod, string key) => $"{mod}/{key}";

    /// <summary>
    ///     A package's <see cref="NewVariable.Saved"/> as a full key: a key with a <c>/</c> names another mod's field
    ///     (<c>yuta.larvacount/LarvaCount.Progress.LarvasMet</c>), one without the package's own mod's.
    /// </summary>
    public static string Resolve(string mod, string key) => key.Contains('/', StringComparison.Ordinal) ? key : FullKey(mod, key);

    /// <summary>What is wrong with a key: empty, or with a character the loader's file cannot keep apart.</summary>
    public static string? KeyProblem(string key) =>
        key.Length == 0 ? "an empty key"
        : key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? $"the key \"{key}\" has a space or a control character"
        : null;
}

/// <summary>A mod's <c>atel/saved.json</c>: every <c>[Saved]</c> field it declares.</summary>
public sealed class SavedFile {
    /// <summary>The file in a mod's <c>atel/</c> folder.</summary>
    public const string FileName = "saved.json";

    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;

    public List<SavedField> Fields { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, ScriptPackage.JsonOptions);

    /// <exception cref="JsonException">Not a saved-fields file.</exception>
    public static SavedFile FromJson(string json) =>
        JsonSerializer.Deserialize<SavedFile>(json, ScriptPackage.JsonOptions) ?? throw new JsonException("an empty saved-fields file");
}

/// <summary>A saved field with its place in the save area for this run.</summary>
/// <param name="Mod">The mod that declares it.</param>
/// <param name="Offset">Its storage 0 offset, in <see cref="SaveArea.Start"/> .. <see cref="SaveArea.Start"/> + <see cref="SaveArea.Length"/>.</param>
public sealed record PlacedField(string Mod, SavedField Field, int Offset) {
    public string FullKey => SavedField.FullKey(Mod, Field.Key);

    /// <summary>Its first byte in the save area (0 .. <see cref="SaveArea.Length"/>).</summary>
    public int AreaOffset => Offset - SaveArea.Start;
}

/// <summary>
///     Where every enabled mod's saved fields are for one run: one table for every script, so a key has the same offset
///     in each (docs/formats/mod-package.md, "Saved fields").
/// </summary>
/// <remarks>
///     Fields are placed in load order, each mod's in the order of its <c>saved.json</c>, from <see cref="SaveArea.Start"/>,
///     each aligned to its element size. A mod whose fields do not fit after the earlier mods' is left out, all of it (its
///     scripts share the fields); the later mods are still placed when theirs fit (in the room the left-out mod and its
///     readers leave). A mod that uses a field no enabled mod
///     declares, another mod's without that mod in its <c>requires</c>, or one of another type or length than its
///     declaration, is left out too, and so is every mod that uses a field of a mod left out. Offsets are never saved: the
///     loader keeps values by key, so a different set of mods only moves them.
/// </remarks>
public sealed class SavedLayout {
    private readonly Dictionary<string, PlacedField> _byKey = new(StringComparer.Ordinal);

    /// <summary>The placed fields, in load order.</summary>
    public IReadOnlyList<PlacedField> Fields { get; }

    /// <summary>The mods whose fields are placed (every mod of the run that declares one).</summary>
    public IReadOnlySet<string> Mods { get; }

    /// <summary>Bytes of the area in use, alignment included.</summary>
    public int Used { get; }

    public static SavedLayout Empty { get; } = new([], 0);

    private SavedLayout(List<PlacedField> fields, int used) {
        Fields = fields;
        Used = used;
        foreach (PlacedField f in fields) _byKey[f.FullKey] = f;
        Mods = new HashSet<string>(fields.Select(f => f.Mod), StringComparer.Ordinal);
    }

    /// <summary>The field of a full key (<c>&lt;mod&gt;/&lt;key&gt;</c>); null when no linked mod declares it.</summary>
    public PlacedField? Find(string fullKey) => _byKey.GetValueOrDefault(fullKey);

    /// <summary>The field a package variable of mod <paramref name="mod"/> names (<see cref="NewVariable.Saved"/>).</summary>
    public PlacedField? Find(string mod, string key) => Find(SavedField.Resolve(mod, key));

    /// <summary>
    ///     Places the saved fields of <paramref name="mods"/> (in load order) and returns the layout; <paramref name="kept"/>
    ///     gets the mods that link, in load order, and <paramref name="messages"/> why the others do not.
    /// </summary>
    public static SavedLayout Build(IReadOnlyList<AtelMod> mods, List<LinkMessage> messages, out List<AtelMod> kept) {
        List<AtelMod> set = [.. mods];
        while (true) {
            RemoveUnresolved(set, messages);
            (List<PlacedField> fields, int used, List<AtelMod> overflow) = Place(set);
            if (overflow.Count == 0) {
                kept = set;
                foreach (AtelMod m in set.Where(m => m.Saved.Count > 0)) {
                    List<PlacedField> own = [.. fields.Where(f => f.Mod == m.Id)];
                    messages.Add(new(LinkSeverity.Info, m.Id, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{own.Count} saved field(s), {own.Sum(f => f.Field.Size)} bytes at storage 0 0x{own.Min(f => f.Offset):x4}..0x{own.Max(f => f.Offset + f.Field.Size):x4}")));
                }
                return new SavedLayout(fields, used);
            }
            // One at a time: leaving a mod out may leave out its readers too, and their room goes to the mods after them.
            AtelMod first = overflow[0];
            messages.Add(new(LinkSeverity.Error, first.Id, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"its saved fields ({first.Saved.Sum(f => f.Size)} bytes) do not fit in the save area after the earlier mods' ({SaveArea.Length} bytes in all); not linked")));
            set.Remove(first);
        }
    }

    private static (List<PlacedField> Fields, int Used, List<AtelMod> Overflow) Place(List<AtelMod> set) {
        List<PlacedField> fields = [];
        List<AtelMod> overflow = [];
        int at = 0;
        foreach (AtelMod m in set) {
            int start = at;
            List<PlacedField> own = [];
            bool fits = true;
            foreach (SavedField f in m.Saved) {
                at = (at + f.ElementSize - 1) / f.ElementSize * f.ElementSize;
                if (at + f.Size > SaveArea.Length) { fits = false; break; }
                own.Add(new PlacedField(m.Id, f, SaveArea.Start + at));
                at += f.Size;
            }
            if (fits) fields.AddRange(own);
            else {
                overflow.Add(m);
                at = start;
            }
        }
        return (fields, at, overflow);
    }

    /// <summary>Leaves out every mod whose packages use a saved field that does not resolve, until none is left.</summary>
    private static void RemoveUnresolved(List<AtelMod> set, List<LinkMessage> messages) {
        bool changed = true;
        while (changed) {
            changed = false;
            Dictionary<string, (AtelMod Mod, SavedField Field)> declared = new(StringComparer.Ordinal);
            foreach (AtelMod m in set) {
                foreach (SavedField f in m.Saved) declared.TryAdd(SavedField.FullKey(m.Id, f.Key), (m, f));
            }
            foreach (AtelMod m in set.ToList()) {
                if (Problem(m, declared) is not string problem) continue;
                messages.Add(new(LinkSeverity.Error, m.Id, $"{problem}; not linked"));
                set.Remove(m);
                changed = true;
            }
        }
    }

    /// <summary>Why mod <paramref name="m"/>'s saved fields do not resolve; null when they do.</summary>
    private static string? Problem(AtelMod m, Dictionary<string, (AtelMod Mod, SavedField Field)> declared) {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (SavedField f in m.Saved) {
            if (SavedField.KeyProblem(f.Key) is string bad) return $"{SavedFile.FileName}: {bad}";
            if (f.Key.Contains('/', StringComparison.Ordinal)) return $"{SavedFile.FileName}: the key \"{f.Key}\" has a /; a mod declares only its own fields";
            if (!keys.Add(f.Key)) return $"{SavedFile.FileName}: two saved fields have the key \"{f.Key}\"";
            if (f.Type is < 0 or > 6 || f.Length < 1 || f.Size > SaveArea.Length) return $"{SavedFile.FileName}: the saved field {f.Key} has no valid type or length";
        }
        foreach ((string script, NewVariable v) in m.Packages.Values.SelectMany(p => p.Variables.Where(v => v.Saved is not null).Select(v => (p.Script, v)))) {
            string full = SavedField.Resolve(m.Id, v.Saved!);
            string owner = full[..full.IndexOf('/', StringComparison.Ordinal)];
            if (!declared.TryGetValue(full, out var d)) {
                return owner == m.Id
                    ? $"{script}: the saved field {v.Saved} is not in its {SavedFile.FileName}"
                    : $"{script}: reads the saved field {full}, which no linked mod declares ({owner} is not enabled, was left out, or has no such field)";
            }
            if (owner != m.Id && !m.Manifest.Requires.Contains(owner)) {
                return $"{script}: reads {owner}'s saved field {d.Field.Key}, but its mod.toml does not require {owner}";
            }
            if (d.Field.Type != v.Type || d.Field.Length != v.Length || d.Field.RowLength != v.RowLength) {
                return $"{script}: the saved field {full} is type {v.Type} × {v.Length} here, but {owner} declares type {d.Field.Type} × {d.Field.Length}";
            }
        }
        return null;
    }
}
