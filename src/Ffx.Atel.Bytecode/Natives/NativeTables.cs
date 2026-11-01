// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace Ffx.Atel.Bytecode.Natives;

/// <summary>The four handler slots of a native: start, poll, float result, integer result.</summary>
public readonly record struct NativeSlots(uint? Start, uint? Poll, uint? Float, uint? Int) {
    /// <summary>The handler that does the call's work: the result function if any, else start, else poll.</summary>
    public uint? Handler => Int ?? Float ?? Start ?? Poll;

    public IEnumerable<uint> All => new[] { Start, Poll, Float, Int }.Where(h => h is not null).Select(h => h!.Value);
}

/// <summary>
///     The native function tables in the player's <c>FFX.exe</c>: what <c>func id</c> calls.
/// </summary>
/// <remarks>
///     An ID splits into a group (<c>id &gt;&gt; 12</c>) and an index into the group's table of 16-byte
///     entries, registered by <c>AtelInitTotal</c> (<c>0x0086d6d0</c>). Ported from ffx-rust's
///     <c>crates/ffx-script/src/natives.rs</c>; the highest index per group is Fahrenheit's.
/// </remarks>
public sealed class NativeTables {
    /// <summary>(group, table address), from <c>AtelInitTotal</c>.</summary>
    public static readonly IReadOnlyList<(int Group, uint Table, int MaxIndex)> Groups = [
        (0x0, 0x00c5_0050, 0x267), (0x1, 0x00c5_2be0, 0x1d), (0x4, 0x00c8_8d88, 0x46), (0x5, 0x00c8_91f8, 0x90),
        (0x6, 0x00c4_3998, 0x89), (0x7, 0x00c4_2628, 0x127), (0x8, 0x00c5_dc90, 0x6b), (0x9, 0x00c5_d8c0, 0x0),
        (0xb, 0x00c4_0e30, 0x0f), (0xc, 0x00c5_2dd8, 0x5d), (0xd, 0x00c8_5eb0, 0x0),
    ];

    private const uint ImageBase = 0x40_0000;
    private const uint TextStart = 0x0040_1000, TextEnd = 0x00b0_bc00;

    private readonly byte[] _exe;
    private readonly List<(uint Va, uint Size, uint Raw)> _sections = [];

    public NativeTables(byte[] exe) {
        _exe = exe;
        int nt = BinaryPrimitives.ReadInt32LittleEndian(exe.AsSpan(0x3c));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(nt + 6));
        int table = nt + 0x18 + BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(nt + 0x14));
        for (int i = 0; i < count; i++) {
            ReadOnlySpan<byte> s = exe.AsSpan(table + 40 * i + 12);
            _sections.Add((BinaryPrimitives.ReadUInt32LittleEndian(s), BinaryPrimitives.ReadUInt32LittleEndian(s[4..]), BinaryPrimitives.ReadUInt32LittleEndian(s[8..])));
        }
    }

    public static NativeTables Open(string gameDir) => new(File.ReadAllBytes(Path.Combine(gameDir, "FFX.exe")));

    /// <summary>A <c>u32</c> at a virtual address, or null outside the image.</summary>
    public uint? ReadU32(uint va) {
        uint rva = va - ImageBase;
        foreach ((uint start, uint size, uint raw) in _sections) {
            if (rva >= start && rva < start + size) {
                int at = (int)(raw + rva - start);
                return at + 4 <= _exe.Length ? BinaryPrimitives.ReadUInt32LittleEndian(_exe.AsSpan(at)) : null;
            }
        }
        return null;
    }

    /// <summary>The handler slots of <c>func id</c>, or null for an ID outside every table.</summary>
    public NativeSlots? Slots(ushort id) {
        int group = id >> 12, index = id & 0xfff;
        foreach ((int g, uint table, int max) in Groups) {
            if (g != group || index > max) continue;
            uint entry = table + (uint)index * 16;
            uint? Slot(int k) => ReadU32(entry + 4 * (uint)k) is uint h && h is >= TextStart and < TextEnd ? h : null;
            return new NativeSlots(Slot(0), Slot(1), Slot(2), Slot(3));
        }
        return null;
    }

    /// <summary>Every ID with at least one handler.</summary>
    public IEnumerable<(ushort Id, NativeSlots Slots)> All() {
        foreach ((int g, _, int max) in Groups) {
            for (int i = 0; i <= max; i++) {
                ushort id = (ushort)(g << 12 | i);
                if (Slots(id) is NativeSlots s && s.Handler is not null) yield return (id, s);
            }
        }
    }
}
