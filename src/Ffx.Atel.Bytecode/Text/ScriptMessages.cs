// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Collections.Concurrent;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Bytecode.Text;

/// <summary>
///     The messages a script's <c>Text[n]</c> reads, per language, from the player's archive.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>An event reads its table, <c>new_&lt;lang&gt;pc/event/obj_ps3/&lt;xx&gt;/&lt;event&gt;/&lt;event&gt;.bin</c>;
///         an entry with offset 0, or every entry when the language has no table for the event, is the same
///         entry of the <c>.ebp</c>'s section 1, the original Japanese table (<c>AtelEventSetUp</c>,
///         <c>0x00872ef0</c>), decoded with the Japanese charset.</item>
///         <item>A battle script reads <c>new_&lt;lang&gt;pc/battle/btl/&lt;name&gt;/&lt;name&gt;.bin</c>; 163 of the
///         863 battles have one. What a battle without a table shows is not known.</item>
///         <item>A monster's script has no table of its own: it runs in the battle's ctrl work, so a message
///         index means the current battle's message (<c>docs/editor.md</c>).</item>
///     </list>
/// </remarks>
public sealed class ScriptMessages(VbfArchive archive) {
    private readonly ConcurrentDictionary<GameLanguage, Charset> _charsets = new();

    /// <summary>The language's charset, read once from the archive.</summary>
    public Charset Charset(GameLanguage language) =>
        _charsets.GetOrAdd(language, l => new Charset(archive.Read(l.CharsetPath()), l));

    /// <summary>The archive path of a script's table in a language; null for monsters.</summary>
    public static string? TablePath(ScriptCategory category, string name, GameLanguage language) => category switch {
        ScriptCategory.Event  => language.EventMessagesPath(name),
        ScriptCategory.Battle => language.BattleMessagesPath(name),
        _                     => null,
    };

    /// <summary>
    ///     The messages of a script in a language, for reading (<see cref="Charset.Display"/>), index by
    ///     index; null entries have no text. Null when the script has no table in the language and no
    ///     fallback.
    /// </summary>
    /// <param name="scriptPath">The script's archive path (<see cref="GameData.Identify"/>).</param>
    public IReadOnlyList<string?>? Display(string scriptPath, GameLanguage language) {
        if (GameData.Identify(scriptPath) is not var (category, name)) return null;
        MessageTable? table = Read(TablePath(category, name, language));
        MessageTable? original = category == ScriptCategory.Event ? OriginalTable(archive.Read(scriptPath)) : null;
        if (table is null && original is null) return null;
        Charset charset = Charset(language);
        int count = Math.Max(table?.Messages.Count ?? 0, original?.Messages.Count ?? 0);
        string?[] result = new string?[count];
        for (int i = 0; i < count; i++) {
            Message? m = table is not null && i < table.Messages.Count ? table.Messages[i] : null;
            if (m?.Text is byte[] text) {
                result[i] = charset.Display(text);
            }
            else if (original is not null && i < original.Messages.Count && original.Messages[i].Text is byte[] fallback && (m is not null || table is null)) {
                result[i] = Charset(GameLanguage.Japanese).Display(fallback);
            }
        }
        return result;
    }

    private MessageTable? Read(string? path) {
        if (path is null || archive.Find(path) is null) return null;
        try {
            return MessageTable.Parse(archive.Read(path));
        }
        catch (InvalidDataException) {
            return null;
        }
    }

    /// <summary>
    ///     Section 1 of an event file (<c>"EV01"</c>, then section offsets up to <c>0xffffffff</c>): the
    ///     original Japanese message table; null when absent or not a table.
    /// </summary>
    public static MessageTable? OriginalTable(byte[] ebp) {
        if (!ebp.AsSpan().StartsWith("EV01"u8) || ebp.Length < 12) return null;
        uint start = BinaryPrimitives.ReadUInt32LittleEndian(ebp.AsSpan(8));
        if (start is 0 or 0xffff_ffff || start >= ebp.Length) return null;
        uint end = (uint)ebp.Length;
        for (int o = 12; o + 4 <= Math.Min(ebp.Length, 0x40); o += 4) {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(ebp.AsSpan(o));
            if (v == 0xffff_ffff) break;
            if (v > start) {
                end = Math.Min(end, v);
                break;
            }
        }
        try {
            return MessageTable.Parse(ebp.AsSpan((int)start, (int)(end - start)));
        }
        catch (InvalidDataException) {
            return null;
        }
    }
}
