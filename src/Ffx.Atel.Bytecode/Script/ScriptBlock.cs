// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace Ffx.Atel.Bytecode.Script;

/// <summary>A script block is malformed, or an edit to it does not fit its shape.</summary>
public sealed class ScriptFormatException(string message) : Exception(message);

/// <summary>One decoded instruction.</summary>
/// <param name="Offset">Code offset (relative to the block's code start).</param>
/// <param name="Code">The opcode (low 7 bits of the first byte).</param>
/// <param name="Operand">The <c>u16</c> operand, if the first byte has its high bit set.</param>
public readonly record struct Instr(uint Offset, byte Code, ushort? Operand) {
    public uint Length => Operand is null ? 1u : 3u;

    public Opcode Opcode => Opcodes.All[Code];

    /// <summary>Appends the instruction's bytes.</summary>
    public void Encode(List<byte> output) {
        if (Operand is ushort v) {
            output.Add((byte)(Code | 0x80));
            output.Add((byte)v);
            output.Add((byte)(v >> 8));
        }
        else {
            output.Add(Code);
        }
    }
}

/// <summary>One worker: a script object with its own entry points and jump labels.</summary>
/// <param name="Header">Offset of the worker header in the block.</param>
/// <param name="Type">Worker type (header <c>+0x00</c>).</param>
/// <param name="Entries">Entry points, as code offsets.</param>
/// <param name="Labels">Jump labels, as code offsets.</param>
/// <param name="Start">Start of the worker's code range (code offset).</param>
/// <param name="End">End of the worker's code range (code offset, exclusive).</param>
public sealed record Worker(int Header, ushort Type, uint[] Entries, uint[] Labels, uint Start, uint End);

/// <summary>
///     A script block: section 0 of a monster file or an event file, which the interpreter runs.
/// </summary>
/// <remarks>
///     Ported from ffx-rust's <c>crates/ffx-script/src/script.rs</c>; layout in its
///     <c>docs/formats/script.md</c> (<c>Script_Run</c>, <c>0x008641e0</c>):
///     <code>
///     block  +0x00 u32  code length          +0x10 u32  block length
///            +0x30 u32  code offset (the interpreter's only use of the block header)
///            +0x34 u16  worker count         +0x38 u32* worker header offsets
///     worker +0x00 u16  type
///            +0x02 u16  variable count      +0x14 u32  variable table (8 bytes each)
///            +0x04 u16  int constant count  +0x18 u32  int constants
///            +0x06 u16  float count         +0x1c u32  float constants
///            +0x08 u16  entry count         +0x20 u32  entry points (u32 code offsets)
///            +0x0a u16  label count         +0x24 u32  jump labels (u32 code offsets)
///     </code>
///     Every worker's entries and labels lie in its own contiguous code range, which runs from
///     its first entry to the next worker's.
/// </remarks>
public sealed class ScriptBlock {
    public const int WorkerHeaderLength = 0x34;

    private readonly byte[] _bytes;

    /// <summary>The block's bytes.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>Offset of the code in the block (header <c>+0x30</c>).</summary>
    public int CodeStart { get; }

    /// <summary>Length of the code (header <c>+0x00</c>).</summary>
    public int CodeLength { get; }

    public IReadOnlyList<Worker> Workers { get; }

    private ScriptBlock(byte[] bytes, int codeStart, int codeLength, Worker[] workers) {
        _bytes     = bytes;
        CodeStart  = codeStart;
        CodeLength = codeLength;
        Workers    = workers;
    }

    /// <summary>Copies of the block's bytes.</summary>
    public byte[] ToArray() => (byte[])_bytes.Clone();

    public ushort U16(int offset) => offset >= 0 && offset + 2 <= _bytes.Length
        ? BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(offset))
        : throw new ScriptFormatException("truncated script");

    public uint U32(int offset) => offset >= 0 && offset + 4 <= _bytes.Length
        ? BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset))
        : throw new ScriptFormatException("truncated script");

    /// <summary>Parses a script block.</summary>
    /// <exception cref="ScriptFormatException">
    ///     A table lies outside the block, or the entries and labels break the per-worker range
    ///     rule every shipped script follows.
    /// </exception>
    public static ScriptBlock Parse(byte[] bytes) {
        uint U16At(int o) => o >= 0 && o + 2 <= bytes.Length ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(o)) : throw new ScriptFormatException("truncated script");
        uint U32At(int o) => o >= 0 && o + 4 <= bytes.Length ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(o)) : throw new ScriptFormatException("truncated script");

        long code = U32At(0x30);
        long codeLength = U32At(0x00);
        if (code + codeLength > bytes.Length) throw new ScriptFormatException("code outside the block");
        int count = (int)U16At(0x34);

        (int header, ushort type, uint[] entries, uint[] labels)[] raw = new (int, ushort, uint[], uint[])[count];
        for (int w = 0; w < count; w++) {
            int header = checked((int)U32At(0x38 + 4 * w));
            if (header + WorkerHeaderLength > bytes.Length) throw new ScriptFormatException("worker header outside the block");
            uint[] Table(int countAt, int offsetAt) {
                int n = (int)U16At(header + countAt);
                int at = checked((int)U32At(header + offsetAt));
                uint[] t = new uint[n];
                for (int i = 0; i < n; i++) t[i] = U32At(at + 4 * i);
                return t;
            }
            raw[w] = (header, (ushort)U16At(header), Table(0x08, 0x20), Table(0x0a, 0x24));
        }

        // Each worker runs from its first entry to the next one in code order. Shipped scripts are
        // in worker order; rebuilt ones may have workers appended past the original code.
        uint[] starts = raw.Select(r => r.entries.Length > 0 ? r.entries.Min() : throw new ScriptFormatException("worker without entries")).ToArray();
        int[] order = Enumerable.Range(0, count).OrderBy(i => starts[i]).ToArray();
        for (int k = 1; k < order.Length; k++) {
            if (starts[order[k - 1]] == starts[order[k]]) throw new ScriptFormatException("two workers start at the same offset");
        }
        uint blockEnd = (uint)(bytes.Length - code);
        Worker[] workers = new Worker[count];
        for (int k = 0; k < order.Length; k++) {
            int i = order[k];
            uint end = k + 1 < order.Length ? starts[order[k + 1]]
                : starts[i] >= codeLength ? blockEnd
                : (uint)codeLength;
            (int header, ushort type, uint[] entries, uint[] labels) = raw[i];
            if (entries.Concat(labels).Any(t => t < starts[i] || t >= end)) {
                throw new ScriptFormatException("entry or label outside its worker");
            }
            workers[i] = new Worker(header, type, entries, labels, starts[i], end);
        }

        ScriptBlock script = new(bytes, (int)code, (int)codeLength, workers);
        foreach (Worker w in workers) {
            HashSet<uint> instrStarts = script.Decode(w.Start, w.End).Select(i => i.Offset).ToHashSet();
            if (w.Entries.Concat(w.Labels).Any(t => !instrStarts.Contains(t))) {
                throw new ScriptFormatException("entry or label inside an instruction");
            }
        }
        return script;
    }

    /// <summary>Decodes the instructions of a worker.</summary>
    public List<Instr> Decode(Worker worker) => Decode(worker.Start, worker.End);

    /// <summary>Decodes the instructions in a code range.</summary>
    /// <exception cref="ScriptFormatException">An instruction runs past the range.</exception>
    public List<Instr> Decode(uint start, uint end) {
        List<Instr> output = [];
        uint pc = start;
        while (pc < end) {
            int at = CodeStart + (int)pc;
            byte b = _bytes[at];
            ushort? operand = null;
            if ((b & 0x80) != 0) {
                if (at + 3 > _bytes.Length) throw new ScriptFormatException("truncated instruction");
                operand = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(at + 1));
            }
            Instr instr = new(pc, (byte)(b & 0x7f), operand);
            pc += instr.Length;
            if (pc > end) throw new ScriptFormatException("instruction runs past its worker");
            output.Add(instr);
        }
        return output;
    }

    /// <summary>Integer constant <paramref name="i"/> of a worker.</summary>
    public int? IntConst(int worker, ushort i) {
        if ((uint)worker >= Workers.Count) return null;
        int h = Workers[worker].Header;
        if (i >= U16(h + 4)) return null;
        int at = (int)U32(h + 0x18) + 4 * i;
        return at + 4 <= _bytes.Length ? (int)U32(at) : null;
    }

    /// <summary>Float constant <paramref name="i"/> of a worker.</summary>
    public float? FloatConst(int worker, ushort i) {
        if ((uint)worker >= Workers.Count) return null;
        int h = Workers[worker].Header;
        if (i >= U16(h + 6)) return null;
        int at = (int)U32(h + 0x1c) + 4 * i;
        return at + 4 <= _bytes.Length ? BitConverter.UInt32BitsToSingle(U32(at)) : null;
    }

    /// <summary>
    ///     Variable <paramref name="i"/> of a worker: its location word (type in bits 28–31,
    ///     storage in bits 25–27, offset in bits 0–23; <c>AtelGetRealVarAdrs</c>, <c>0x0086c2e0</c>)
    ///     and array length.
    /// </summary>
    public (uint Location, ushort Length)? Variable(int worker, ushort i) {
        if ((uint)worker >= Workers.Count) return null;
        int h = Workers[worker].Header;
        if (i >= U16(h + 2)) return null;
        int at = (int)U32(h + 0x14) + 8 * i;
        return at + 8 <= _bytes.Length ? (U32(at), U16(at + 4)) : null;
    }

    /// <summary>
    ///     Replaces each worker's code, entry points and labels.
    /// </summary>
    /// <remarks>
    ///     <paramref name="code"/>[w] is worker w's new code; <paramref name="entries"/>[w] and
    ///     <paramref name="labels"/>[w] are offsets into it and must keep the original counts. Code
    ///     that fits in the worker's original range is written there, padded with <c>nop</c>; longer
    ///     code is appended to the end of the block, which the interpreter allows (it only uses the
    ///     code offset at <c>+0x30</c>). Nothing else in the block moves.
    /// </remarks>
    public ScriptBlock Rebuild(IReadOnlyList<byte[]> code, IReadOnlyList<uint[]> entries, IReadOnlyList<uint[]> labels) {
        if (code.Count != Workers.Count || entries.Count != code.Count || labels.Count != code.Count) {
            throw new ScriptFormatException("one code, entry and label list per worker");
        }
        List<byte> bytes = [.. _bytes];
        for (int w = 0; w < Workers.Count; w++) {
            Worker worker = Workers[w];
            if (entries[w].Length != worker.Entries.Length || labels[w].Length != worker.Labels.Length) {
                throw new ScriptFormatException($"worker {w} must keep {worker.Entries.Length} entries and {worker.Labels.Length} labels");
            }
            uint length = (uint)code[w].Length;
            if (entries[w].Concat(labels[w]).Any(t => t >= Math.Max(length, 1u))) {
                throw new ScriptFormatException($"worker {w}: an entry or label points past its code");
            }
            uint original = worker.End - worker.Start;
            uint baseOffset;
            if (length <= original) {
                int at = CodeStart + (int)worker.Start;
                for (int i = 0; i < original; i++) bytes[at + i] = 0;
                for (int i = 0; i < length; i++) bytes[at + i] = code[w][i];
                baseOffset = worker.Start;
            }
            else {
                while (bytes.Count % 4 != 0) bytes.Add(0);
                baseOffset = (uint)(bytes.Count - CodeStart);
                bytes.AddRange(code[w]);
            }
            foreach ((int tableAt, uint[] values) in new[] { (0x20, entries[w]), (0x24, labels[w]) }) {
                int table = (int)U32(worker.Header + tableAt);
                for (int i = 0; i < values.Length; i++) PutU32(bytes, table + 4 * i, baseOffset + values[i]);
            }
        }
        if (bytes.Count != _bytes.Length) PutU32(bytes, 0x10, (uint)bytes.Count);
        return Parse([.. bytes]);
    }

    private static void PutU32(List<byte> b, int o, uint v) {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 16);
        b[o + 3] = (byte)(v >> 24);
    }
}
