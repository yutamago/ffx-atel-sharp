// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;

namespace Ffx.Atel.Bytecode.Script;

/// <summary>An entry of the variable table: location word, array length and the second <c>u16</c>.</summary>
/// <param name="Location">Type in bits 28–31, storage in bits 25–27, offset in bits 0–23.</param>
/// <param name="Length">Array length (<c>+4</c>).</param>
/// <param name="Extra">The <c>u16</c> at <c>+6</c> (<c>AtelGetVarArrayRange</c>).</param>
public readonly record struct VarEntry(uint Location, ushort Length, ushort Extra) {
    public int Type => (int)(Location >> 28);
    public int Storage => (int)((Location >> 25) & 7);
    public int Offset => (int)(Location & 0xff_ffff);

    /// <summary>Bytes per element: 1 for <c>u8</c>/<c>i8</c>, 2 for <c>u16</c>/<c>i16</c>, else 4.</summary>
    public int ElementSize => Type switch { 0 or 1 => 1, 2 or 3 => 2, _ => 4 };
}

/// <summary>One worker of a <see cref="ScriptImage"/>.</summary>
public sealed class WorkerImage {
    /// <summary>Worker type (header <c>+0x00</c>).</summary>
    public ushort Type { get; set; }

    /// <summary>Header <c>+0x0c</c>, not yet understood.</summary>
    public uint Unknown0C { get; set; }

    /// <summary>Header <c>+0x10</c>, Fahrenheit's <c>priv_data_len</c>.</summary>
    public uint PrivateLength { get; set; }

    /// <summary>The worker's code, from its first entry to the next worker's.</summary>
    public byte[] Code { get; set; } = [];

    /// <summary>Entry points, as offsets into <see cref="Code"/>.</summary>
    public uint[] Entries { get; set; } = [];

    /// <summary>Jump labels, as offsets into <see cref="Code"/>.</summary>
    public uint[] Labels { get; set; } = [];

    /// <summary>
    ///     The private data block after the worker's tables (header <c>+0x2c</c>, storage 3), or null;
    ///     <see cref="ScriptImage.PrivateSize"/> bytes.
    /// </summary>
    public byte[]? Private { get; set; }
}

/// <summary>
///     A script block as data: everything that makes up its bytes, without any offsets the layout
///     rules can compute. <see cref="Write"/> lays it out; <see cref="FromBlock"/> takes a block
///     apart and records where the original deviates from the rules (<see cref="Pins"/>) and the
///     bytes the rules don't produce (<see cref="Patches"/>), so every block comes back exactly.
/// </summary>
/// <remarks>
///     The layout, from all 758 shipped scripts (header names after Fahrenheit's
///     <c>AtelScriptChunk</c>; see docs/formats/script-block.md):
///     <code>
///     header (0x38 + 4 per worker), worker headers (0x34 each),
///     map entrances (0x20 each), author string, name string (align 4),
///     variable table (align 4), int constants, float constants, code,
///     shared data (storage 4; align 32), other (0x40), event data (storage 6), area,
///     per worker: entry table, label table, private data (0x10),
///     end (align 16 = block length)
///     </code>
/// </remarks>
public sealed class ScriptImage {
    // Header words not yet understood, kept as they are.
    public uint Header14 { get; set; }
    public uint Header18 { get; set; }
    public uint Header1C { get; set; }
    public uint Header24 { get; set; }

    /// <summary>Map entrances (block <c>+0x04</c>), 0x20 bytes each; null when the pointer is 0.</summary>
    public byte[]? Map { get; set; }

    /// <summary>The author string (block <c>+0x08</c>), without its NUL.</summary>
    public byte[] Author { get; set; } = [];

    /// <summary>The name string (block <c>+0x0c</c>), without its NUL.</summary>
    public byte[] Name { get; set; } = [];

    public List<VarEntry> Variables { get; } = [];
    public List<uint> IntConstants { get; } = [];
    public List<uint> FloatConstants { get; } = [];

    /// <summary>Shared data (storage 4, every worker's <c>+0x30</c>): from the end of the code to the other area.</summary>
    public byte[]? Shared { get; set; }

    /// <summary>The 0x40-byte area at block <c>+0x2c</c> (Fahrenheit's <c>offset_other</c>).</summary>
    public byte[]? Other { get; set; }

    /// <summary>Event data (storage 6, block <c>+0x20</c>).</summary>
    public byte[]? Event { get; set; }

    /// <summary>The area at block <c>+0x28</c> (Fahrenheit's <c>offset_area</c>).</summary>
    public byte[]? Area { get; set; }

    public List<WorkerImage> Workers { get; } = [];

    /// <summary>Where the block deviates from the layout rules: region name to offset.</summary>
    public SortedDictionary<string, int> Pins { get; } = new(StringComparer.Ordinal);

    /// <summary>Bytes the layout does not produce (non-zero padding and the like), by offset.</summary>
    public SortedDictionary<int, byte[]> Patches { get; } = [];

    private static int Align(int x, int n) => (x + n - 1) / n * n;

    // ----- Layout ---------------------------------------------------------------------------

    /// <summary>Region offsets, computed by the rules unless pinned.</summary>
    private sealed class Layout {
        public readonly Dictionary<string, int> At = new(StringComparer.Ordinal);
        public int End;
    }

    /// <summary>
    ///     Lays the regions out. <paramref name="actual"/> gives the original's offsets while taking a
    ///     block apart (a region placed elsewhere than the rule says is pinned there); otherwise
    ///     <see cref="Pins"/> override the rules.
    /// </summary>
    private Layout Place(Func<string, int?>? actual) {
        Layout l = new();
        int Put(string name, int rule) {
            int at = rule;
            if (actual?.Invoke(name) is int real) {
                if (real != rule) Pins[name] = real;
                at = real;
            }
            else if (actual is null && Pins.TryGetValue(name, out int pinned)) {
                at = pinned;
            }
            l.At[name] = at;
            return at;
        }
        int pos = 0x38 + 4 * Workers.Count;
        pos = Put("workers", pos) + 0x34 * Workers.Count;
        if (Map is not null) pos = Put("map", pos) + Map.Length;
        pos = Put("author", pos) + Author.Length + 1;
        pos = Put("name", Align(pos, 4)) + Name.Length + 1;
        pos = Put("vars", Align(pos, 4)) + 8 * Variables.Count;
        pos = Put("ints", pos) + 4 * IntConstants.Count;
        pos = Put("floats", pos) + 4 * FloatConstants.Count;
        pos = Put("code", pos) + Workers.Sum(w => w.Code.Length);
        if (Shared is not null) pos = Put("shared", Align(pos, 32)) + Shared.Length;
        if (Other is not null) pos = Put("other", pos) + Other.Length;
        if (Event is not null) pos = Put("event", pos) + Event.Length;
        if (Area is not null) pos = Put("area", pos) + Area.Length;
        for (int w = 0; w < Workers.Count; w++) {
            WorkerImage wi = Workers[w];
            pos = Put($"w{w}.entries", pos) + 4 * wi.Entries.Length;
            pos = Put($"w{w}.labels", pos) + 4 * wi.Labels.Length;
            if (wi.Private is not null) pos = Put($"w{w}.private", pos) + wi.Private.Length;
        }
        l.End = Put("end", Align(pos, 16));
        return l;
    }

    // ----- Writing --------------------------------------------------------------------------

    /// <summary>Writes the block: <see cref="Pins"/> and <see cref="Patches"/> applied.</summary>
    public byte[] Write() {
        Layout l = Place(null);
        int size = Math.Max(l.End, Patches.Count == 0 ? 0 : Patches.Max(p => p.Key + p.Value.Length));
        byte[] b = new byte[size];
        void U16(int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)v);
        void U32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
        void Bytes(int o, byte[] v) => v.CopyTo(b, o);
        int At(string name) => l.At.TryGetValue(name, out int v) ? v : 0;

        int code = At("code");
        int codeLength = Workers.Sum(w => w.Code.Length);
        U32(0x00, (uint)codeLength);
        U32(0x04, (uint)At("map"));
        U32(0x08, (uint)At("author"));
        U32(0x0c, (uint)At("name"));
        U32(0x10, (uint)l.End);
        U32(0x14, Header14);
        U32(0x18, Header18);
        U32(0x1c, Header1C);
        U32(0x20, (uint)At("event"));
        U32(0x24, Header24);
        U32(0x28, (uint)At("area"));
        U32(0x2c, (uint)At("other"));
        U32(0x30, (uint)code);
        U16(0x34, Workers.Count);
        U16(0x36, Workers.Count(w => w.Type != 0));

        if (Map is not null) Bytes(At("map"), Map);
        Bytes(At("author"), Author);
        Bytes(At("name"), Name);
        int vars = At("vars");
        for (int i = 0; i < Variables.Count; i++) {
            U32(vars + 8 * i, Variables[i].Location);
            U16(vars + 8 * i + 4, Variables[i].Length);
            U16(vars + 8 * i + 6, Variables[i].Extra);
        }
        for (int i = 0; i < IntConstants.Count; i++) U32(At("ints") + 4 * i, IntConstants[i]);
        for (int i = 0; i < FloatConstants.Count; i++) U32(At("floats") + 4 * i, FloatConstants[i]);
        if (Shared is not null) Bytes(At("shared"), Shared);
        if (Other is not null) Bytes(At("other"), Other);
        if (Event is not null) Bytes(At("event"), Event);
        if (Area is not null) Bytes(At("area"), Area);

        int workerStart = 0;
        for (int w = 0; w < Workers.Count; w++) {
            WorkerImage wi = Workers[w];
            int h = At("workers") + 0x34 * w;
            U32(0x38 + 4 * w, (uint)h);
            U16(h + 0x00, wi.Type);
            U16(h + 0x02, Variables.Count);
            U16(h + 0x04, IntConstants.Count);
            U16(h + 0x06, FloatConstants.Count);
            U16(h + 0x08, wi.Entries.Length);
            U16(h + 0x0a, wi.Labels.Length);
            U32(h + 0x0c, wi.Unknown0C);
            U32(h + 0x10, wi.PrivateLength);
            U32(h + 0x14, (uint)vars);
            U32(h + 0x18, (uint)At("ints"));
            U32(h + 0x1c, (uint)At("floats"));
            int entries = At($"w{w}.entries"), labels = At($"w{w}.labels");
            U32(h + 0x20, (uint)entries);
            U32(h + 0x24, (uint)labels);
            U32(h + 0x2c, (uint)At($"w{w}.private"));
            U32(h + 0x30, (uint)At("shared"));
            Bytes(code + workerStart, wi.Code);
            for (int i = 0; i < wi.Entries.Length; i++) U32(entries + 4 * i, (uint)workerStart + wi.Entries[i]);
            for (int i = 0; i < wi.Labels.Length; i++) U32(labels + 4 * i, (uint)workerStart + wi.Labels[i]);
            if (wi.Private is not null) Bytes(At($"w{w}.private"), wi.Private);
            workerStart += wi.Code.Length;
        }
        foreach ((int o, byte[] v) in Patches) v.CopyTo(b, o);
        return b;
    }

    // ----- Reading --------------------------------------------------------------------------

    /// <summary>
    ///     Takes a block apart. The result writes back to the block's first <c>+0x10</c> bytes
    ///     (its own length); what follows up to the next section is the file's padding.
    /// </summary>
    /// <exception cref="ScriptFormatException">The block has a shape the image cannot hold.</exception>
    public static ScriptImage FromBlock(ScriptBlock block) {
        ReadOnlySpan<byte> raw = block.Bytes;
        int blockLength = (int)block.U32(0x10);
        if (blockLength > raw.Length) throw new ScriptFormatException("block length past the section");
        byte[] b = raw[..blockLength].ToArray();
        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
        byte[] Slice(int from, int to) => from <= to && to <= b.Length ? b[from..to] : throw new ScriptFormatException("region out of order");
        int StrEnd(int o) {
            int e = o;
            while (e < b.Length && b[e] != 0) e++;
            return e;
        }

        ScriptImage img = new() {
            Header14 = U32(0x14), Header18 = U32(0x18), Header1C = U32(0x1c), Header24 = U32(0x24),
        };
        int n = block.Workers.Count;
        Dictionary<string, int> actual = new(StringComparer.Ordinal);

        int map = (int)U32(0x04), author = (int)U32(0x08), name = (int)U32(0x0c);
        if (n > 0) actual["workers"] = block.Workers[0].Header;
        if (map != 0) {
            img.Map = Slice(map, author);
            actual["map"] = map;
        }
        img.Author = Slice(author, StrEnd(author));
        img.Name = Slice(name, StrEnd(name));
        actual["author"] = author;
        actual["name"] = name;

        // One variable, int and float table for the whole block (true of every shipped script).
        int vars = n > 0 ? (int)U32(block.Workers[0].Header + 0x14) : Align(StrEnd(name) + 1, 4);
        if (n > 0) {
            int h0 = block.Workers[0].Header;
            foreach (Worker w in block.Workers) {
                for (int o = 0x02; o < 0x08; o += 2) {
                    if (U16(w.Header + o) != U16(h0 + o)) throw new ScriptFormatException("workers with their own variable or constant tables");
                }
                for (int o = 0x14; o < 0x20; o += 4) {
                    if (U32(w.Header + o) != U32(h0 + o)) throw new ScriptFormatException("workers with their own variable or constant tables");
                }
                if (U32(w.Header + 0x28) != 0) throw new ScriptFormatException("a worker with data in storage 2");
                if (U32(w.Header + 0x30) != U32(h0 + 0x30)) throw new ScriptFormatException("workers with different shared data");
            }
            int nv = U16(h0 + 2), ni = U16(h0 + 4), nf = U16(h0 + 6);
            for (int i = 0; i < nv; i++) img.Variables.Add(new VarEntry(U32(vars + 8 * i), U16(vars + 8 * i + 4), U16(vars + 8 * i + 6)));
            int ints = (int)U32(h0 + 0x18), floats = (int)U32(h0 + 0x1c);
            for (int i = 0; i < ni; i++) img.IntConstants.Add(U32(ints + 4 * i));
            for (int i = 0; i < nf; i++) img.FloatConstants.Add(U32(floats + 4 * i));
            actual["ints"] = ints;
            actual["floats"] = floats;
        }
        actual["vars"] = vars;
        actual["code"] = block.CodeStart;

        // Workers' code, in worker order; the code region must be exactly their ranges back to back.
        uint expected = 0;
        foreach (Worker w in block.Workers) {
            if (w.Start != expected) throw new ScriptFormatException("worker code not in worker order");
            expected = w.End;
        }
        if (expected != block.CodeLength) throw new ScriptFormatException("code past the last worker");

        // Data areas: each runs to the next region that starts after it.
        int codeEnd = block.CodeStart + block.CodeLength;
        int shared = n > 0 ? (int)U32(block.Workers[0].Header + 0x30) : 0;
        int other = (int)U32(0x2c), ev = (int)U32(0x20), area = (int)U32(0x28);
        int tables = n > 0 ? block.Workers.Min(w => (int)U32(w.Header + 0x20)) : blockLength;
        List<int> starts = [codeEnd, tables, blockLength];
        foreach (int s in new[] { shared, other, ev, area }) if (s != 0) starts.Add(s);
        // A region that starts where the worker tables do is empty.
        int NextAfter(int s) => s == tables ? s : starts.Where(x => x > s).DefaultIfEmpty(blockLength).Min();
        // A block without workers (the empty monster slots) has no pointer to its shared data, but
        // the data is there, where the rule puts it.
        if (n == 0 && other != 0 && Align(codeEnd, 32) <= other) shared = Align(codeEnd, 32);
        if (shared != 0) { img.Shared = Slice(shared, NextAfter(shared)); actual["shared"] = shared; }
        if (other != 0) { img.Other = Slice(other, NextAfter(other)); actual["other"] = other; }
        if (ev != 0) { img.Event = ev == area ? [] : Slice(ev, NextAfter(ev)); actual["event"] = ev; }
        if (area != 0) { img.Area = Slice(area, NextAfter(area)); actual["area"] = area; }

        for (int w = 0; w < n; w++) {
            Worker wk = block.Workers[w];
            int h = wk.Header;
            int priv = (int)U32(h + 0x2c);
            WorkerImage wi = new() {
                Type = wk.Type,
                Unknown0C = U32(h + 0x0c),
                PrivateLength = U32(h + 0x10),
                Code = Slice(block.CodeStart + (int)wk.Start, block.CodeStart + (int)wk.End),
                Entries = wk.Entries.Select(e => e - wk.Start).ToArray(),
                Labels = wk.Labels.Select(e => e - wk.Start).ToArray(),
            };
            actual[$"w{w}.entries"] = (int)U32(h + 0x20);
            actual[$"w{w}.labels"] = (int)U32(h + 0x24);
            if (priv != 0) {
                wi.Private = Slice(priv, Math.Min(priv + PrivateSize(wi.PrivateLength), blockLength));
                actual[$"w{w}.private"] = priv;
            }
            img.Workers.Add(wi);
        }
        actual["end"] = blockLength;

        img.Place(name => actual.TryGetValue(name, out int v) ? v : null);

        // Whatever the layout does not reproduce becomes a patch.
        byte[] written = img.Write();
        if (written.Length != b.Length) throw new ScriptFormatException($"layout writes {written.Length} bytes, the block has {b.Length}");
        for (int i = 0; i < b.Length; i++) {
            if (written[i] == b[i]) continue;
            int start = i;
            while (i < b.Length && written[i] != b[i]) i++;
            img.Patches[start] = b[start..i];
        }
        return img;
    }

    /// <summary>Room the private data block takes: its length (<c>+0x10</c>) rounded up to 16, at least 16.</summary>
    public static int PrivateSize(uint length) => Align((int)Math.Max(length, 1), 16);

    /// <summary>The author and name as text (they are ASCII in every shipped script).</summary>
    public string AuthorText => Encoding.Latin1.GetString(Author);

    public string NameText => Encoding.Latin1.GetString(Name);
}
