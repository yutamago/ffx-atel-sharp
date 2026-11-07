// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace Ffx.Atel.Bytecode.Script;

/// <summary>
///     One handler of a <see cref="HandlerTable"/>: the battle's signal <see cref="Signal"/> runs entry
///     <c>Entries[k]</c> of worker <see cref="Worker"/>, <c>k</c> the signal's value (a motion, a voice, a
///     camera, a command), at level 7. A null entry (<c>0xffff</c>) runs nothing for that <c>k</c>.
/// </summary>
/// <param name="Signal">The signal number the battle asks for (<c>MsAtelRequestExe</c>'s <c>n</c>).</param>
/// <param name="Worker">The worker, by its number in the script.</param>
/// <param name="Flag">The record's second byte (read by nothing found yet).</param>
/// <param name="Entries">The entry per value <c>k</c>.</param>
public sealed record Handler(int Signal, int Worker, int Flag, IReadOnlyList<int?> Entries);

/// <summary>
///     The table through which the battle runs a monster or battle script (section 1 of the file;
///     FFX.exe's <c>Mon_FindHandler</c>, <c>0x00797410</c>, ported in ffx-rust's
///     <c>crates/ffx-battle/src/minor.rs</c>): <c>+0</c> the script's work count, <c>+1</c> the signal
///     count <c>n</c>, <c>+2</c> one byte per signal (the handler's slot, <c>0xff</c> none), then from
///     halfword <c>(n + 1) / 2</c> one 4-byte record per slot (<c>+0</c> the worker, <c>+1</c> a byte,
///     <c>+2</c> an <c>i16</c> offset from the section to a <c>u16</c> count and as many entries).
/// </summary>
public sealed class HandlerTable {
    /// <summary>The script's work count (<c>+0</c>; the battle counts the next script's works from it).</summary>
    public int WorkCount { get; }

    /// <summary>The handlers by signal, in signal order.</summary>
    public IReadOnlyList<Handler> Handlers { get; }

    private HandlerTable(int workCount, IReadOnlyList<Handler> handlers) {
        WorkCount = workCount;
        Handlers = handlers;
    }

    /// <summary>The table of a monster or battle file, or null for an event file or a file without one.</summary>
    /// <exception cref="ScriptFormatException">The table points outside its section.</exception>
    public static HandlerTable? Of(ScriptFile file) {
        if (file.Kind != ScriptFileKind.Monster) return null;
        ReadOnlySpan<byte> bytes = file.Bytes;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) < 2) return null;
        uint at = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (at == 0 || at + 2 > bytes.Length) return null;
        return Parse(bytes[(int)at..]);
    }

    /// <summary>Reads a table from the start of its section.</summary>
    /// <exception cref="ScriptFormatException">The table points outside the bytes.</exception>
    public static HandlerTable Parse(ReadOnlySpan<byte> s) {
        if (s.Length < 2) throw new ScriptFormatException("handler table: too short");
        int n = s[1];
        if (s.Length < 2 + n) throw new ScriptFormatException("handler table: signal bytes past the end");
        List<Handler> handlers = [];
        for (int i = 0; i < n; i++) {
            int slot = s[2 + i];
            if (slot == 0xff) continue;
            int record = (((n + 1) >> 1) + 2 * slot) * 2 + 2;
            if (record + 4 > s.Length) throw new ScriptFormatException($"handler table: signal {i}'s record past the end");
            int table = BinaryPrimitives.ReadInt16LittleEndian(s[(record + 2)..]);
            if (table < 0 || table + 2 > s.Length) throw new ScriptFormatException($"handler table: signal {i}'s entries past the end");
            int count = BinaryPrimitives.ReadUInt16LittleEndian(s[table..]);
            if (table + 2 + 2 * count > s.Length) throw new ScriptFormatException($"handler table: signal {i}'s entries past the end");
            int?[] entries = new int?[count];
            for (int k = 0; k < count; k++) {
                ushort e = BinaryPrimitives.ReadUInt16LittleEndian(s[(table + 2 + 2 * k)..]);
                entries[k] = e == 0xffff ? null : e;
            }
            handlers.Add(new Handler(i, s[record], s[record + 1], entries));
        }
        return new HandlerTable(s[0], handlers);
    }
}
