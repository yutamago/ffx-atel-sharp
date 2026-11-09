// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;

namespace Ffx.Atel.Loader;

/// <summary>A message table pointer in game memory, as <see cref="MessageSlot"/> uses it.</summary>
public interface ITableMemory {
    /// <summary>The table the game reads now; 0 for none.</summary>
    nint Get();

    /// <summary>Points the game at <paramref name="table"/> (and wherever it kept the same table, as ctrl work 0's <c>+0x38</c>).</summary>
    void Set(nint table);

    /// <summary>Reads a 16-bit value at <paramref name="at"/>.</summary>
    ushort U16(nint at);

    /// <summary>Reads a byte at <paramref name="at"/>.</summary>
    byte U8(nint at);

    /// <summary>Copies <paramref name="length"/> bytes from <paramref name="at"/>.</summary>
    byte[] Read(nint at, int length);

    /// <summary>Memory of the loader's own, holding <paramref name="bytes"/>.</summary>
    nint Allocate(byte[] bytes);

    /// <summary>Frees memory <see cref="Allocate"/> gave.</summary>
    void Free(nint at);
}

/// <summary>
///     One message table the game reads through a pointer it keeps (docs/formats/debug.md, "Runtime injection"): the
///     event's (ctrl work 0 <c>+0x34</c>), the battle's (<c>0x0112a9b8</c>) or <c>system_01</c>'s (<c>0x0112a998</c>). It
///     keeps a copy with the injected messages in the pointer's place and puts it back after the game loads its own.
/// </summary>
/// <remarks>
///     The game frees its tables through pointers of its own (<c>g_EventMessageBuffer</c>, <c>g_BtlFileTextData</c>), never
///     through the pointer it reads, so it never frees the loader's copy. The loader frees its copies of an earlier
///     table when the game loads another, keeping the latest (a window may still show its text).
/// </remarks>
/// <param name="what">The table, for the log: <c>ka/kami0000</c>, <c>battle kami00_00</c>.</param>
public sealed class MessageSlot(string what) {
    private nint _installed;       // our copy in the pointer, or 0
    private nint _original;        // the game's table we replaced
    private int _applied = -1;
    private readonly List<nint> _ours = [];

    /// <summary>The copy in place now; 0 when the game's own table is.</summary>
    public nint Installed => _installed;

    /// <summary>The copies still allocated.</summary>
    public int Allocated => _ours.Count;

    /// <summary>
    ///     Sets <paramref name="messages"/> in the table the game reads, when they changed (<paramref name="version"/>) or
    ///     the game loaded its table again. Null messages put the game's table back.
    /// </summary>
    /// <param name="name">The table's script now, for the log (it changes with the table).</param>
    /// <returns>A line for the log, or null when nothing changed.</returns>
    /// <exception cref="InvalidDataException">The game's table does not look like a message table.</exception>
    public string? Apply(ITableMemory memory, int version, IReadOnlyList<(int Index, byte[] Text)>? messages, string? name = null) {
        nint table = memory.Get();
        if (table == 0) return null;
        if (table == _installed && version == _applied) return null;
        if (table != _installed) {
            // Another table, or the same script loaded again: the game's own.
            _original = table;
            _installed = 0;
            FreeOurs(memory);
        }
        _applied = version;
        string label = name ?? what;
        if (messages is null || messages.Count == 0) {
            if (_installed == 0) return null;
            Install(memory, _original);
            return $"{label}: the game's own message table again";
        }
        nint original = _original;
        int? length = Injection.TableLength(o => memory.U16(original + o), o => memory.U8(original + o));
        if (length is null) throw new InvalidDataException($"{label}: the message table does not look like one; not changed");
        byte[] bytes = Injection.SetMessages(memory.Read(original, length.Value), messages);
        nint ours = memory.Allocate(bytes);
        _ours.Add(ours);
        Install(memory, ours);
        return $"{label}: {messages.Count} message(s) set ({length} -> {bytes.Length} bytes)";
    }

    private void Install(ITableMemory memory, nint table) {
        memory.Set(table);
        _installed = table == _original ? 0 : table;
    }

    private void FreeOurs(ITableMemory memory) {
        for (int i = 0; i < _ours.Count - 1; i++) memory.Free(_ours[i]);
        if (_ours.Count > 1) _ours.RemoveRange(0, _ours.Count - 1);
    }
}

/// <summary>Where a battle's message tables are in FFX.exe, and when they may be read (docs/formats/debug.md).</summary>
public static class BattleTables {
    /// <summary>The battle script whose table stays loaded from the game's start (<c>MsBattleInit</c>).</summary>
    public const string SystemScript = "system_01";

    /// <summary>
    ///     Whether the battle's own table (<c>*0x0112a9b8</c>) is the current battle's and allocated, by
    ///     <c>g_BtlStatus</c> (<c>0x0112a8e0</c>). Status 5 starts the read: its task frees the last battle's text
    ///     (<c>g_BtlFileTextData</c>) and reads the new one; status 7 (<c>MsBtlReadSetScene</c>) points
    ///     <c>0x0112a9b8</c> at it. Status 4 waits before that, 0 is no battle (the pointer may still be the last battle's,
    ///     which the next read frees). From 8 on, and in the battle (1, 2, 3, 0x17-0x22), it is the battle's.
    /// </summary>
    public static bool Ready(sbyte status) => status is not (0 or 4 or 5 or 6 or 7);

    /// <summary>The battle's name in <c>g_BtlFileName</c> (<c>0x023112e0</c>): the read's task cuts the path to the folder's name, <c>kami00_00</c>.</summary>
    public static bool IsBattle(string fileName, string script) => string.Equals(fileName, script, StringComparison.OrdinalIgnoreCase);
}
