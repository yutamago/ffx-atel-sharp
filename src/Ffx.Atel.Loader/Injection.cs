// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Ffx.Atel.Bytecode.Text;
using Ffx.Atel.Linker;

namespace Ffx.Atel.Loader;

/// <summary>
///     Runtime injection packages: mod packages (and their messages and debug texts) put into the loader's
///     <c>inject/</c> folder while the game runs (docs/formats/debug.md, "Runtime injection"). This part is
///     the game-free logic; <see cref="RuntimeInjector"/> applies it.
/// </summary>
/// <remarks>
///     What is swapped, and when: a package's code is linked onto its file after every enabled mod and the
///     file the loader serves is rewritten, so the game reads it the next time it loads the script (the map
///     entered again, the next battle); a running script is never changed in memory. Its debug texts count
///     at once. Its messages are set in the running event's message table at once (ctrl work 0
///     <c>+0x34</c>), and again whenever that event loads.
/// </remarks>
public static class Injection {
    /// <summary>The folder in the loader's own folder that it watches.</summary>
    public const string Folder = "inject";

    /// <summary>The id the injected packages link under, after every mod.</summary>
    public const string ModId = "inject";

    /// <summary>The packages of <c>&lt;loader&gt;/inject</c>, as a mod without a manifest.</summary>
    public static AtelMod Read(string loaderDir) => AtelMod.FromFolder(ModId, Path.Combine(loaderDir, Folder));

    /// <summary>
    ///     Links one file again: the enabled mods of <paramref name="linked"/> (the startup link, without its
    ///     injected packages) and then <paramref name="injected"/>. Null when no package is left for the file.
    /// </summary>
    public static LinkedFile? Relink(IReadOnlyList<AtelMod> linked, AtelMod injected, string path, Func<string, byte[]?> readVanilla) {
        List<AtelMod> mods = [.. linked.Where(m => m.Id != ModId), injected];
        return ModSet.Link([.. mods.Where(m => m.Packages.ContainsKey(path))], readVanilla).SingleOrDefault();
    }

    /// <summary>
    ///     The length of a message table in memory, from its entries: the first string follows them, and the
    ///     table ends after the 00 of the string that starts last. <paramref name="u16"/> and
    ///     <paramref name="u8"/> read at an offset into it; null when it does not look like a table.
    /// </summary>
    public static int? TableLength(Func<int, ushort> u16, Func<int, byte> u8) {
        int first = u16(0);
        if (first == 0 || first % 8 != 0) return null;
        int last = 0;
        for (int at = 0; at < first; at += 8) {
            last = Math.Max(last, Math.Max(u16(at), u16(at + 4)));
        }
        int end = last;
        while (end < 0x10000 && u8(end) != 0) end++;
        return end < 0x10000 ? end + 1 : null;
    }

    /// <summary>
    ///     A message table with <paramref name="messages"/> set by index: an index past the end grows the table,
    ///     the entries between get an empty text; an existing entry gets the new text and keeps its flags.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a table, or the text outgrows its 16-bit offsets.</exception>
    public static byte[] SetMessages(ReadOnlySpan<byte> table, IReadOnlyList<(int Index, byte[] Text)> messages) {
        List<Message> list = [.. MessageTable.Parse(table).Messages];
        foreach ((int index, byte[] text) in messages) {
            if (index is < 0 or > 0x1fff) throw new InvalidDataException($"message {index}: not an index of a table");
            while (list.Count <= index) list.Add(new Message([], 0));
            list[index] = new Message(text, list[index].Flags);
        }
        return new MessageTable(list).Write();
    }

    /// <summary>
    ///     The language of <c>TOGetFFXLang()</c> (<c>0x006410e0</c>), which picks the <c>new_&lt;lang&gt;pc</c> folder
    ///     the event's messages come from: 0 Japanese, 1 English, 2 French, 3 Spanish, 4 German, 5 Italian,
    ///     9 Korean, 10 Chinese; null for the others. (<c>TkGetLang</c> is the PS2 setting: 0 on PC.)
    /// </summary>
    public static GameLanguage? Language(int ffxLang) => ffxLang switch {
        0 => GameLanguage.Japanese,
        1 => GameLanguage.English,
        2 => GameLanguage.French,
        3 => GameLanguage.Spanish,
        4 => GameLanguage.German,
        5 => GameLanguage.Italian,
        9 => GameLanguage.Korean,
        10 => GameLanguage.Chinese,
        _ => null,
    };

    /// <summary>Whether <paramref name="eventName"/> (<c>g_EventName</c>, <c>ka/kami0000</c>) is the event <paramref name="script"/> (<c>kami0000</c>).</summary>
    public static bool IsEvent(string eventName, string script) =>
        string.Equals(eventName[(eventName.LastIndexOf('/') + 1)..], script, StringComparison.OrdinalIgnoreCase);
}
