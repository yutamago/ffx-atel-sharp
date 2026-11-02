// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Security.Cryptography;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Linker;

/// <summary>
///     Hashes of vanilla parts: what a package records for each part it attaches to, and the linker
///     checks against the player's file. SHA-256 over a fixed encoding, the first 8 bytes in hex.
/// </summary>
public static class VanillaHash {
    /// <summary>A worker: its type, code, entry and label offsets (relative to its code).</summary>
    public static string Worker(ushort type, ReadOnlySpan<byte> code, IReadOnlyList<uint> entries, IReadOnlyList<uint> labels) {
        using IncrementalHash h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendU32(h, 0x5752_4b31);    // "WRK1"
        AppendU32(h, type);
        AppendU32(h, (uint)code.Length);
        h.AppendData(code);
        AppendU32(h, (uint)entries.Count);
        foreach (uint e in entries) AppendU32(h, e);
        AppendU32(h, (uint)labels.Count);
        foreach (uint l in labels) AppendU32(h, l);
        return Finish(h);
    }

    public static string Worker(WorkerImage w) => Worker(w.Type, w.Code, w.Entries, w.Labels);

    /// <summary>
    ///     An entry method's code, <paramref name="start"/> to <paramref name="end"/> in the worker's code,
    ///     with the labels that point into it (relative to <paramref name="start"/>, in label order).
    /// </summary>
    public static string Range(ReadOnlySpan<byte> workerCode, IReadOnlyList<uint> labels, uint start, uint end) {
        if (start > end || end > workerCode.Length) throw new ArgumentOutOfRangeException(nameof(end), "the range is outside the worker's code");
        using IncrementalHash h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendU32(h, 0x524e_4731);    // "RNG1"
        AppendU32(h, end - start);
        h.AppendData(workerCode[(int)start..(int)end]);
        foreach (uint l in labels) {
            if (l >= start && l < end) AppendU32(h, l - start);
        }
        return Finish(h);
    }

    public static string Range(WorkerImage w, uint start, uint end) => Range(w.Code, w.Labels, start, end);

    /// <summary>A variable's table entry.</summary>
    public static string Variable(VarEntry v) {
        using IncrementalHash h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendU32(h, 0x5641_5231);    // "VAR1"
        AppendU32(h, v.Location);
        AppendU32(h, v.Length);
        AppendU32(h, v.RowLength);
        return Finish(h);
    }

    private static void AppendU32(IncrementalHash h, uint v) {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        h.AppendData(b);
    }

    private static string Finish(IncrementalHash h) => Convert.ToHexStringLower(h.GetHashAndReset().AsSpan(0, 8));
}
