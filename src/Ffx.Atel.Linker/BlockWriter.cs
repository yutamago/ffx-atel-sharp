// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Linker;

/// <summary>
///     Writes a linked script block over the vanilla one: every vanilla byte stays where it was, and
///     what grew goes after the block's end (see <see cref="ScriptLinker"/>).
/// </summary>
/// <remarks>
///     After the vanilla block, each 4- or 16-aligned: the variable table and the constant pools
///     when they grew, worker headers (new workers', and vanilla ones that sat where the larger
///     header table needs room), entry and label tables that grew, the data of new variables, the
///     private data of workers whose private data grew (a copy of the vanilla block, then zeros), and
///     the code region when any code changed. Tables that keep their size are rewritten in place.
/// </remarks>
internal static class BlockWriter {
    /// <param name="GrownPrivate">The worker's new private data length (<c>+0x10</c>), when its private data grew.</param>
    /// <param name="NewPrivate">A new worker's private data's initial bytes (its block is <paramref name="PrivateLength"/> long).</param>
    public sealed record LinkedWorker(int VanillaIndex, ushort Type, uint Word0C, uint PrivateLength, byte[] Code, uint[] Entries, uint[] Labels, bool CodeChanged, uint? GrownPrivate = null, byte[]? NewPrivate = null);

    /// <summary>
    ///     The linked parts: workers in their final order, variables (with their offset in <see cref="Data"/>
    ///     when allocated there; for storage 3 in the new private area, which starts at
    ///     <see cref="PrivateStart"/> in each grown worker's private data), pools.
    /// </summary>
    public sealed record Linked(List<LinkedWorker> Workers, List<(VarEntry Entry, int? DataOffset)> Vars, List<uint> Ints, List<uint> Floats, byte[] Data, int PrivateStart = 0);

    private static int Align(int x, int n) => (x + n - 1) / n * n;

    public static byte[] Write(ScriptBlock block, ScriptImage vanilla, Linked l) {
        ReadOnlySpan<byte> raw = block.Bytes;
        int length = (int)block.U32(0x10);
        int n0 = block.Workers.Count, n = l.Workers.Count;
        if (n0 == 0) throw new ScriptLinker.LinkException("the script has no workers");
        int h0 = block.Workers[0].Header;
        List<byte> b = [.. raw[..length]];
        int pos = Align(length, 16);
        int Alloc(int size, int align) {
            pos = Align(pos, align);
            int at = pos;
            pos += size;
            return at;
        }

        bool varsChanged = l.Vars.Count != vanilla.Variables.Count;
        bool intsChanged = l.Ints.Count != vanilla.IntConstants.Count;
        bool floatsChanged = l.Floats.Count != vanilla.FloatConstants.Count;
        bool codeChanged = n != n0 || l.Workers.Any(w => w.CodeChanged);
        int varsAt = varsChanged ? Alloc(8 * l.Vars.Count, 4) : (int)block.U32(h0 + 0x14);
        int intsAt = intsChanged ? Alloc(4 * l.Ints.Count, 4) : (int)block.U32(h0 + 0x18);
        int floatsAt = floatsChanged ? Alloc(4 * l.Floats.Count, 4) : (int)block.U32(h0 + 0x1c);
        int sharedAt = (int)block.U32(h0 + 0x30);
        int eventAt = (int)block.U32(0x20);

        // The header table at 0x38 grows into the first worker headers, which then move.
        int tableEnd = 0x38 + 4 * n;
        int[] vanillaHeaders = [.. block.Workers.Select(w => w.Header)];
        if (n > n0) {
            int covered = 0x38 + 4 * n0;
            foreach (int h in vanillaHeaders.Order()) {
                if (h != covered) break;
                covered += ScriptBlock.WorkerHeaderLength;
            }
            if (covered < tableEnd) throw new ScriptLinker.LinkException($"no room for {n - n0} more workers in the header table");
        }
        int[] headers = new int[n];
        for (int i = 0; i < n; i++) {
            LinkedWorker w = l.Workers[i];
            if (w.VanillaIndex >= 0 && vanillaHeaders[w.VanillaIndex] >= tableEnd) {
                headers[i] = vanillaHeaders[w.VanillaIndex];
            }
            else {
                headers[i] = Alloc(ScriptBlock.WorkerHeaderLength, 4);
            }
        }

        int[] entriesAt = new int[n], labelsAt = new int[n];
        for (int i = 0; i < n; i++) {
            LinkedWorker w = l.Workers[i];
            if (w.VanillaIndex >= 0) {
                int h = vanillaHeaders[w.VanillaIndex];
                Worker vw = block.Workers[w.VanillaIndex];
                if (w.Entries.Length == vw.Entries.Length && w.Labels.Length == vw.Labels.Length) {
                    entriesAt[i] = (int)block.U32(h + 0x20);
                    labelsAt[i] = (int)block.U32(h + 0x24);
                    continue;
                }
            }
            entriesAt[i] = Alloc(4 * w.Entries.Length, 4);
            labelsAt[i] = Alloc(4 * w.Labels.Length, 4);
        }

        int dataAt = l.Data.Length > 0 ? Alloc(l.Data.Length, 16) : 0;

        // Private data that grew: a new block (storage 3 is the block plus +0x2c), with the vanilla bytes.
        int[] privateAt = new int[n];
        for (int i = 0; i < n; i++) {
            LinkedWorker w = l.Workers[i];
            if (w.GrownPrivate is uint grown) privateAt[i] = Alloc(ScriptImage.PrivateSize(grown), 16);
            else if (w.VanillaIndex < 0 && w.PrivateLength > 0) privateAt[i] = Alloc(ScriptImage.PrivateSize(w.PrivateLength), 16);
        }

        int codeAt = block.CodeStart, codeLength = block.CodeLength;
        uint[] starts = new uint[n];
        if (codeChanged) {
            codeLength = l.Workers.Sum(w => w.Code.Length);
            codeAt = Alloc(codeLength, 16);
            uint s = 0;
            for (int i = 0; i < n; i++) {
                starts[i] = s;
                s += (uint)l.Workers[i].Code.Length;
            }
        }
        else {
            for (int i = 0; i < n; i++) starts[i] = block.Workers[l.Workers[i].VanillaIndex].Start;
        }
        int end = Align(pos, 16);
        while (b.Count < end) b.Add(0);

        void U16(int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(b)[o..], (ushort)v);
        void U32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(b)[o..], v);
        void Bytes(int o, byte[] v) {
            for (int i = 0; i < v.Length; i++) b[o + i] = v[i];
        }

        if (varsChanged) {
            for (int i = 0; i < l.Vars.Count; i++) {
                (VarEntry v, int? data) = l.Vars[i];
                uint location = v.Location;
                if (data is int d) {
                    if (v.Storage == 3) {
                        long at = (long)l.PrivateStart + d;
                        if (at > 0xff_ffff) throw new ScriptLinker.LinkException($"variable {i}: its private offset is out of reach");
                        U32(varsAt + 8 * i, (location & ~0xff_ffffu) | (uint)at);
                        U16(varsAt + 8 * i + 4, v.Length);
                        U16(varsAt + 8 * i + 6, v.RowLength);
                        continue;
                    }
                    int baseAt = v.Storage switch {
                        4 => sharedAt,
                        6 => eventAt,
                        _ => throw new ScriptLinker.LinkException($"variable {i}: storage {v.Storage} is not allocated by the linker"),
                    };
                    long offset = (long)dataAt + d - baseAt;
                    if (baseAt == 0 || offset is < 0 or > 0xff_ffff) throw new ScriptLinker.LinkException($"variable {i}: its data is out of its storage's reach");
                    location = (location & ~0xff_ffffu) | (uint)offset;
                }
                U32(varsAt + 8 * i, location);
                U16(varsAt + 8 * i + 4, v.Length);
                U16(varsAt + 8 * i + 6, v.RowLength);
            }
        }
        if (intsChanged) for (int i = 0; i < l.Ints.Count; i++) U32(intsAt + 4 * i, l.Ints[i]);
        if (floatsChanged) for (int i = 0; i < l.Floats.Count; i++) U32(floatsAt + 4 * i, l.Floats[i]);
        if (dataAt != 0) Bytes(dataAt, l.Data);

        for (int i = 0; i < n; i++) {
            LinkedWorker w = l.Workers[i];
            int h = headers[i];
            if (w.VanillaIndex >= 0) {
                int from = vanillaHeaders[w.VanillaIndex];
                if (from != h) Bytes(h, raw.Slice(from, ScriptBlock.WorkerHeaderLength).ToArray());
            }
            else {
                // A new worker: no storage 2, the script's shared data, and its own private block when it has private data.
                U32(h + 0x28, 0);
                U32(h + 0x2c, (uint)privateAt[i]);
                U32(h + 0x30, (uint)sharedAt);
                if (privateAt[i] != 0 && w.NewPrivate is { } initial) Bytes(privateAt[i], initial);
            }
            U16(h + 0x00, w.Type);
            U16(h + 0x02, l.Vars.Count);
            U16(h + 0x04, l.Ints.Count);
            U16(h + 0x06, l.Floats.Count);
            U16(h + 0x08, w.Entries.Length);
            U16(h + 0x0a, w.Labels.Length);
            U32(h + 0x0c, w.Word0C);
            U32(h + 0x10, w.GrownPrivate ?? w.PrivateLength);
            if (w.GrownPrivate is not null) {
                // The vanilla private bytes, then zeros (Alloc's); in battle the game clears the work's own
                // copy at set-up anyway (AtelSetMyWorkAdrs).
                if (w.VanillaIndex >= 0 && block.U32(vanillaHeaders[w.VanillaIndex] + 0x2c) is uint from and not 0) {
                    int count = (int)Math.Min(w.PrivateLength, (uint)Math.Max(0, length - (int)from));
                    Bytes(privateAt[i], raw.Slice((int)from, count).ToArray());
                }
                U32(h + 0x2c, (uint)privateAt[i]);
            }
            U32(h + 0x14, (uint)varsAt);
            U32(h + 0x18, (uint)intsAt);
            U32(h + 0x1c, (uint)floatsAt);
            U32(h + 0x20, (uint)entriesAt[i]);
            U32(h + 0x24, (uint)labelsAt[i]);
            for (int e = 0; e < w.Entries.Length; e++) U32(entriesAt[i] + 4 * e, starts[i] + w.Entries[e]);
            for (int k = 0; k < w.Labels.Length; k++) U32(labelsAt[i] + 4 * k, starts[i] + w.Labels[k]);
            if (codeChanged) Bytes(codeAt + (int)starts[i], w.Code);
            U32(0x38 + 4 * i, (uint)h);
        }
        U16(0x34, n);
        U16(0x36, l.Workers.Count(w => w.Type != 0));
        U32(0x00, (uint)codeLength);
        U32(0x30, (uint)codeAt);
        U32(0x10, (uint)end);
        return [.. b];
    }
}
