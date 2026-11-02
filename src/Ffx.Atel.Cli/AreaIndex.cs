// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Text;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Layout;

namespace Ffx.Atel.Cli;

/// <summary>
///     <c>AREAS.md</c> at the root of a decompiled tree: per area folder, its scripts with the place the game
///     names for them, their named workers and notes, and per battle the monsters it loads. Written by
///     <c>atel decompile -o</c> (and so <c>atel export</c>) from the player's own files: the place names are
///     game text, which stays in the decompiled tree, never in this repo.
/// </summary>
internal static class AreaIndex {
    public const string FileName = "AREAS.md";

    /// <summary>The index for the decompiled scripts (their source as written, after the library classes).</summary>
    public static string Build(VbfArchive archive, IReadOnlyList<(string Path, ScriptSource Source)> scripts) {
        string[]? places = Places(archive, GameLanguage.English);
        StringBuilder sb = new();
        sb.Append("""
            # Areas

            Written by `atel decompile` (do not edit: the next export replaces it). Per folder: its
            scripts, the place the game names for each (its save location, in English), the workers
            that have a name, and per battle the monsters it loads. A script folder's name is the map
            code; the area's English name is the folder's suffix (ffx-atel-sharp `data/areas.json`).

            """);
        foreach (IGrouping<string, (string Path, ScriptSource Source)> area in scripts.Where(s => s.Source.Names.Category != ScriptCategory.Monster)
                     .GroupBy(s => ProjectLayout.AreaFolder(s.Source.Names), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)) {
            bool battles = area.First().Source.Names.Category == ScriptCategory.Battle;
            sb.Append(CultureInfo.InvariantCulture, $"\n## {area.Key}\n\n");
            if (battles) sb.Append("| Battle | Monsters |\n|---|---|\n");
            else sb.Append("| Script | Place | Workers | Note |\n|---|---|---|---|\n");
            foreach ((string path, ScriptSource source) in area.OrderBy(s => s.Source.Names.Name, StringComparer.Ordinal)) {
                string link = $"[{source.Names.Name}]({SourceLayout.Folder(source.Names)}/)";
                if (battles) {
                    sb.Append(CultureInfo.InvariantCulture, $"| {link} | {string.Join(", ", Monsters(archive.Read(path)).Select(m => $"[m{m:d3}](Monsters/M{m:d3}/)"))} |\n");
                    continue;
                }
                string place = places is null ? "" : string.Join("; ", PlaceIndices(archive.Read(path)).Where(i => i > 0 && i < places.Length).Select(i => places[i]).Distinct());
                List<string> named = [.. source.Workers.Select(w => w.Name).Where(n => !IsDefaultName(n))];
                string workers = named.Count == 0 ? $"{source.Workers.Count}" : $"{string.Join(", ", named)} ({source.Workers.Count} in all)";
                sb.Append(CultureInfo.InvariantCulture, $"| {link} | {Cell(place)} | {Cell(workers)} | {Cell(AreaNames.ScriptNote(source.Names.Name) ?? "")} |\n");
            }
        }
        return sb.ToString();
    }

    private static bool IsDefaultName(string name) => name.Length > 1 && name[0] == 'W' && name[1..].All(char.IsAsciiDigit);

    private static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>
    ///     A battle file's monsters: the formation (section 2) holds eight <c>u16</c> slots at <c>+0x0c</c>,
    ///     <c>0x1000 | n</c> for monster m<i>n</i>, <c>0xffff</c> empty (ffx-rust docs/formats/battle.md).
    /// </summary>
    internal static List<int> Monsters(byte[] file) {
        List<int> found = [];
        if (file.Length < 16) return found;
        int count = BinaryPrimitives.ReadInt32LittleEndian(file);
        if (count < 3 || count > 16 || file.Length < 4 + 4 * count) return found;
        int at = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4 + 2 * 4));
        if (at <= 0 || at + 0x1c > file.Length) return found;
        for (int k = 0; k < 8; k++) {
            ushort slot = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 0x0c + 2 * k));
            if ((slot & 0xf000) == 0x1000 && !found.Contains(slot & 0xfff)) found.Add(slot & 0xfff);
        }
        return found;
    }

    /// <summary>
    ///     The save locations an event names: its script block's <c>u16 +0x1e</c>, or, with bit 15 set, the
    ///     <c>i16</c> table at the block's <c>+0x28</c> of that many entries (<c>AtelSetSaveDicIndex</c>,
    ///     <c>0x00870ef0</c>; ffx-rust crates/ffx-script helpers.rs).
    /// </summary>
    internal static List<int> PlaceIndices(byte[] ebp) {
        List<int> result = [];
        ReadOnlySpan<byte> block;
        try {
            block = Bytecode.Script.ScriptFile.Parse(ebp).Script.Bytes;
        }
        catch (Bytecode.Script.ScriptFormatException) {
            return result;
        }
        if (block.Length < 0x2c) return result;
        int v = BinaryPrimitives.ReadUInt16LittleEndian(block[0x1e..]);
        if ((v & 0x8000) == 0) {
            result.Add(v);
            return result;
        }
        int table = BinaryPrimitives.ReadInt32LittleEndian(block[0x28..]);
        for (int k = 0; k < (v & 0x7fff) && table >= 0 && table + 2 * k + 2 <= block.Length; k++) result.Add(BinaryPrimitives.ReadInt16LittleEndian(block[(table + 2 * k)..]));
        return result;
    }

    /// <summary>
    ///     The location names, section 11 of the language's macro dictionary (<c>menu/macrodic.dcp</c>: sixteen
    ///     section offsets, then per section <c>u16</c> pairs of text offsets, the first one's offset / 4 the
    ///     count); null when the archive has none.
    /// </summary>
    internal static string[]? Places(VbfArchive archive, GameLanguage language) {
        string path = $"ffx_ps2/ffx/master/new_{language.GameCode()}pc/menu/macrodic.dcp";
        if (archive.Find(path) is null) return null;
        byte[] f = archive.Read(path);
        if (f.Length < 0x40) return null;
        int[] offsets = [.. Enumerable.Range(0, 16).Select(i => BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(4 * i)))];
        int start = offsets[11];
        if (start <= 0 || start >= f.Length) return null;
        int end = offsets.Where(o => o > start).DefaultIfEmpty(f.Length).Min();
        ReadOnlySpan<byte> section = f.AsSpan(start, end - start);
        Charset charset = new ScriptMessages(archive).Charset(language);
        int n = BinaryPrimitives.ReadUInt16LittleEndian(section) / 4;
        string[] names = new string[n];
        for (int i = 0; i < n; i++) {
            int o = BinaryPrimitives.ReadUInt16LittleEndian(section[(4 * i)..]);
            int e = o;
            while (e < section.Length && section[e] != 0) e++;
            names[i] = o < section.Length ? charset.Display(section[o..e]) : "";
        }
        return names;
    }
}
