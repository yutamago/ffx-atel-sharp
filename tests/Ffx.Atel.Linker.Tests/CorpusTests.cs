// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Linker.Tests;

/// <summary>Linking onto every shipped script. Needs <c>FFX_GAME_DIR</c>; skipped without it.</summary>
public sealed class CorpusTests(ITestOutputHelper output) {
    /// <summary>
    ///     A package with a prefix and a postfix on the first worker's first method, a new entry that
    ///     requests itself, a new shared variable and a private one with an element per level (a hidden
    ///     local) links onto every script with workers and shared data: the result parses, lays itself
    ///     out again byte for byte, and keeps the vanilla data areas, the other workers' code, the first
    ///     worker's private bytes (copied into its grown private data) and the file's other sections.
    /// </summary>
    [Fact]
    public void EveryScriptTakesAPackage() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        using VbfArchive archive = GameData.OpenArchiveExclusive(dir!);
        List<string> paths = GameData.AllScriptPaths(archive);
        object read = new();
        ConcurrentBag<string> failures = [];
        int linked = 0, skipped = 0;
        Growth growth = new(), regionGrowth = new();
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = 4 }, path => {
            (string Path, byte[] Bytes) f;
            lock (read) f = (path, archive.Read(path));
            try {
                ScriptFile vanilla = ScriptFile.Parse(f.Bytes);
                ScriptImage image = ScriptImage.FromBlock(vanilla.Script);
                if (image.Workers.Count == 0 || image.Shared is null) {
                    Interlocked.Increment(ref skipped);
                    return;
                }
                WorkerImage w0 = image.Workers[0];
                uint start = w0.Entries[0];
                uint end = w0.Entries.Where(e => e > start).DefaultIfEmpty((uint)w0.Code.Length).Min();
                int[] entries = [.. Enumerable.Range(0, w0.Entries.Length).Where(e => w0.Entries[e] == start)];
                ScriptPackage p = new() { File = f.Path, Script = f.Path };
                p.Vanilla.Workers.Add(new WorkerRef(0, "W00", VanillaHash.Worker(w0)));
                p.Vanilla.Entries.Add(new EntryRef(0, "First", entries, start, end, VanillaHash.Range(w0, start, end)));
                p.Variables.Add(new NewVariable { Name = "Turns", Type = 5 });
                p.Variables.Add(new NewVariable { Name = "<local>kept#1", Type = 5, Storage = 3, Length = 9, Array = true });
                string exit = w0.Type == 0 ? "ret" : "end";
                p.Patches.Add(new EntryPatch { Kind = PatchKind.Prefix, Worker = 0, Name = "Before", Target = "First", Code = LinkerTests.Frag("pushimm 1\npopv @newvar:0\nfunc 0x00eb\npushimm 5\npopva @newvar:1") });
                p.Patches.Add(new EntryPatch { Kind = PatchKind.Postfix, Worker = 0, Name = "After", Target = "First", Code = LinkerTests.Frag("pushc @int:1234567\npopv @newvar:0") });
                p.Patches.Add(new EntryPatch {
                    Kind = PatchKind.Entry, Worker = 0, Name = "Again",
                    Code = LinkerTests.Frag($"pushimm 1\npushimm @worker:0\npushimm @newentry:2\nreq0\npopfacc\n{exit}"),
                });
                LinkResult r = ScriptLinker.Link(f.Bytes, [new ModScript("test", p)]);
                if (!r.Changed) {
                    failures.Add($"{f.Path}: {string.Join("; ", r.Messages.Where(m => m.Severity == LinkSeverity.Error))}");
                    return;
                }
                ScriptFile result = ScriptFile.Parse(r.File);
                ScriptBlock b = result.Script;
                ScriptImage again = ScriptImage.FromBlock(b);
                if (!again.Write().AsSpan().SequenceEqual(b.Bytes[..(int)b.U32(0x10)])) failures.Add($"{f.Path}: does not lay itself out again");
                if (KeptInPlace(vanilla.Script, b, image, again, [0]) is string moved) failures.Add($"{f.Path}: {moved}");
                regionGrowth.Add(f.Path, LinkRegion(f.Bytes, [new ModScript("test", p)], failures, f.Path) - f.Bytes.Length);
                // Every data area keeps its bytes at its vanilla offset, and the pointers to it.
                ScriptBlock vb = vanilla.Script;
                int vh0 = vb.Workers[0].Header, lh0 = b.Workers[0].Header;
                foreach ((string name, int at, int linkedAt, byte[]? bytes) in new[] {
                    ("shared", (int)vb.U32(vh0 + 0x30), (int)b.U32(lh0 + 0x30), image.Shared),
                    ("other", (int)vb.U32(0x2c), (int)b.U32(0x2c), image.Other),
                    ("event", (int)vb.U32(0x20), (int)b.U32(0x20), image.Event),
                    ("area", (int)vb.U32(0x28), (int)b.U32(0x28), image.Area),
                }) {
                    if (bytes is null) continue;
                    if (at != linkedAt || !b.Bytes.Slice(at, bytes.Length).SequenceEqual(bytes)) failures.Add($"{f.Path}: the {name} data changed");
                }
                for (int w = 1; w < image.Workers.Count; w++) {
                    if (!image.Workers[w].Code.AsSpan().SequenceEqual(again.Workers[w].Code)) failures.Add($"{f.Path}: worker {w}'s code changed");
                }
                if (again.Workers[0].Entries.Length != w0.Entries.Length + 1) failures.Add($"{f.Path}: no new entry");
                // The first worker's private data grows by the hidden local (36 bytes past its own, rounded
                // to 4), with its vanilla bytes; a function worker's does not (storage 3 is its caller's).
                WorkerImage l0 = again.Workers[0];
                uint grown = w0.Type == 0 ? w0.PrivateLength : ((w0.PrivateLength + 3) & ~3u) + 36;
                if (l0.PrivateLength != grown) failures.Add($"{f.Path}: private data {l0.PrivateLength} bytes, not {grown}");
                else if (w0.Type != 0 && (l0.Private is null || w0.Private is not null && !l0.Private.AsSpan(0, (int)w0.PrivateLength).SequenceEqual(w0.Private.AsSpan(0, (int)w0.PrivateLength)))) {
                    failures.Add($"{f.Path}: the private data lost its vanilla bytes");
                }
                if (w0.Private is not null && vb.U32(vh0 + 0x2c) is uint vp && !b.Bytes.Slice((int)vp, w0.Private.Length).SequenceEqual(w0.Private)) {
                    failures.Add($"{f.Path}: the vanilla private block changed");
                }
                // The sections after the script are the vanilla ones, shifted as a whole.
                int tail = vanilla.Bytes.Length - (vanilla.SectionStart + vanilla.SectionLength);
                if (!vanilla.Bytes[^tail..].SequenceEqual(result.Bytes[^tail..])) failures.Add($"{f.Path}: the later sections changed");
                Interlocked.Increment(ref linked);
                growth.Add(f.Path, r.File.Length - f.Bytes.Length);
            }
            catch (Exception ex) when (ex is ScriptFormatException or ArgumentException) {
                failures.Add($"{f.Path}: {ex.Message}");
            }
        });
        output.WriteLine($"{linked} scripts linked, {skipped} without workers or shared data; files grew by {growth} (code region laid out again: {regionGrowth})");
        foreach (string f in failures.Order(StringComparer.Ordinal).Take(40)) output.WriteLine(f);
        Assert.Empty(failures);
        Assert.Equal(paths.Count, linked + skipped);
    }

    /// <summary>
    ///     Data edits on every shipped script: a byte of the shared data, of the "other" area and of the event data, and on a
    ///     monster its HP (in sections 2 and 6) and gil. The linked file is the vanilla file with exactly those bytes changed.
    /// </summary>
    [Fact]
    public void EveryScriptTakesDataEdits() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        using VbfArchive archive = GameData.OpenArchiveExclusive(dir!);
        List<string> paths = GameData.AllScriptPaths(archive);
        ConcurrentBag<string> failures = [];
        int linked = 0, edits = 0, monsters = 0;
        foreach (string path in paths) {
            byte[] bytes = archive.Read(path);
            try {
                ScriptFile file = ScriptFile.Parse(bytes);
                ScriptImage image = ScriptImage.FromBlock(file.Script);
                ScriptPackage p = new() { File = path, Script = path, Data = [] };
                HashSet<int> expected = [];
                void Edit(DataEditArea area, int offset, byte[] value) {
                    if (DataAreas.Find(file, image, path, area, null) is not DataAreas.Area a) return;
                    // A value that differs from the vanilla byte, so the change shows.
                    byte[] v = [.. value.Select((b, i) => (byte)(bytes[a.At + offset + i] == b ? b ^ 0x5a : b))];
                    p.Data.Add(new DataEdit { Area = area, Offset = offset, Bytes = v, Field = area.ToString() });
                    for (int i = 0; i < v.Length; i++) expected.Add(a.At + offset + i);
                    if (area == DataEditArea.MonsterStats) {
                        int copy = file.Section(DataAreas.StatsCopySection)!.Value.Start;
                        for (int i = 0; i < v.Length; i++) expected.Add(copy + offset + i);
                    }
                }
                Edit(DataEditArea.Shared, 0, [0x11]);
                Edit(DataEditArea.Other, 0x3f, [0x22]);
                Edit(DataEditArea.Event, 0, [0x33]);
                Edit(DataEditArea.MonsterStats, 0x14, [0x39, 0x30, 0, 0]);
                Edit(DataEditArea.MonsterRewards, 0, [0xe7, 0x03]);
                if (p.Data.Count == 0) continue;
                if (p.Data.Any(e => e.Area == DataEditArea.MonsterStats)) monsters++;
                DataAreas.Attach(p, bytes);
                LinkResult r = ScriptLinker.Link(bytes, [new ModScript("test", ScriptPackage.FromJson(p.ToJson()))]);
                if (!r.Changed) {
                    failures.Add($"{path}: {string.Join("; ", r.Messages.Where(m => m.Severity == LinkSeverity.Error))}");
                    continue;
                }
                if (r.File.Length != bytes.Length) failures.Add($"{path}: the file changed its size");
                List<int> changed = [.. Enumerable.Range(0, Math.Min(bytes.Length, r.File.Length)).Where(i => bytes[i] != r.File[i])];
                if (!changed.SequenceEqual(expected.Order())) failures.Add($"{path}: {changed.Count} bytes changed, {expected.Count} expected");
                linked++;
                edits += p.Data.Count;
            }
            catch (Exception ex) when (ex is ScriptFormatException or ArgumentException) {
                failures.Add($"{path}: {ex.Message}");
            }
        }
        output.WriteLine($"{linked} scripts took {edits} data edits ({monsters} monsters their stats); only those bytes changed");
        foreach (string f in failures.Order(StringComparer.Ordinal).Take(40)) output.WriteLine(f);
        Assert.Empty(failures);
        Assert.True(monsters >= 361, $"only {monsters} monsters");
    }

    /// <summary>
    ///     <c>[Inject]</c> on every shipped script: in the first methods of every worker, code before a statement in the
    ///     middle of the method and code after it (a statement: from an empty stack to the next). Each result parses and
    ///     lays itself out again byte for byte, keeps its data areas, its unpatched workers' code and the file's later
    ///     sections, and each patched method's new code is its vanilla code with the injects at their points (its
    ///     <c>end</c>s jumps to one <c>end</c> after it).
    /// </summary>
    [Fact]
    public void EveryScriptTakesInjects() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        using VbfArchive archive = GameData.OpenArchiveExclusive(dir!);
        Bytecode.Analysis.NativeSignatures natives = Bytecode.Analysis.NativeSignatures.FromAssembly(typeof(Ffx.Atel.Worker).Assembly);
        List<string> paths = GameData.AllScriptPaths(archive);
        object read = new();
        ConcurrentBag<string> failures = [];
        int linked = 0, skipped = 0, methods = 0, tooLarge = 0;
        Growth growth = new(), regionGrowth = new();
        const int MethodsPerWorker = 6;
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = 4 }, path => {
            byte[] bytes;
            lock (read) bytes = archive.Read(path);
            try {
                ScriptFile vanilla = ScriptFile.Parse(bytes);
                ScriptImage image = ScriptImage.FromBlock(vanilla.Script);
                ScriptPackage p = new() { File = path, Script = path };
                List<(int Worker, uint Start, uint End, uint Before, uint After)> targets = [];
                for (int w = 0; w < image.Workers.Count; w++) {
                    WorkerImage wi = image.Workers[w];
                    List<uint> starts = [.. wi.Entries.Distinct().Order()];
                    bool listed = false;
                    for (int m = 0; m < starts.Count && m < MethodsPerWorker; m++) {
                        uint start = starts[m], end = m + 1 < starts.Count ? starts[m + 1] : (uint)wi.Code.Length;
                        // A copy of the method needs a label number for each of its labels; a worker has 65,536
                        // (one method of blitzball's worker 36 has 53,203 of its 53,229).
                        if (wi.Labels.Length + wi.Labels.Count(l => l >= start && l < end) > 0xffff) {
                            Interlocked.Increment(ref tooLarge);
                            continue;
                        }
                        if (Statements(wi.Code, start, end, natives) is not { Count: > 0 } statements) continue;
                        (uint s, uint e) = statements[statements.Count / 2];
                        string name = $"M{m}";
                        if (!listed) p.Vanilla.Workers.Add(new WorkerRef(w, $"W{w}", VanillaHash.Worker(wi)));
                        listed = true;
                        int[] entries = [.. Enumerable.Range(0, wi.Entries.Length).Where(k => wi.Entries[k] == start)];
                        p.Vanilla.Entries.Add(new EntryRef(w, name, entries, start, end, VanillaHash.Range(wi, start, end)));
                        p.Patches.Add(new EntryPatch { Kind = PatchKind.Inject, Worker = w, Name = $"{name}Before", Target = name, Code = LinkerTests.Frag("nop"), At = new InjectPoint(InjectPosition.Before, s - start, e - start, "test") });
                        p.Patches.Add(new EntryPatch { Kind = PatchKind.Inject, Worker = w, Name = $"{name}After", Target = name, Code = LinkerTests.Frag("nop\nnop"), At = new InjectPoint(InjectPosition.After, s - start, e - start, "test") });
                        targets.Add((w, start, end, s, e));
                    }
                }
                if (targets.Count == 0) {
                    Interlocked.Increment(ref skipped);
                    return;
                }
                LinkResult r = ScriptLinker.Link(bytes, [new ModScript("test", ScriptPackage.FromJson(p.ToJson()))]);
                if (!r.Changed) {
                    failures.Add($"{path}: {string.Join("; ", r.Messages.Where(m => m.Severity == LinkSeverity.Error))}");
                    return;
                }
                ScriptFile result = ScriptFile.Parse(r.File);
                ScriptBlock b = result.Script;
                ScriptImage again = ScriptImage.FromBlock(b);
                if (!again.Write().AsSpan().SequenceEqual(b.Bytes[..(int)b.U32(0x10)])) failures.Add($"{path}: does not lay itself out again");
                ScriptBlock vb = vanilla.Script;
                if (KeptInPlace(vb, b, image, again, [.. targets.Select(t => t.Worker).Distinct()]) is string moved) failures.Add($"{path}: {moved}");
                regionGrowth.Add(path, LinkRegion(bytes, [new ModScript("test", ScriptPackage.FromJson(p.ToJson()))], failures, path) - bytes.Length);
                foreach ((string name, int at, int linkedAt, byte[]? area) in new[] {
                    ("other", (int)vb.U32(0x2c), (int)b.U32(0x2c), image.Other),
                    ("event", (int)vb.U32(0x20), (int)b.U32(0x20), image.Event),
                    ("area", (int)vb.U32(0x28), (int)b.U32(0x28), image.Area),
                }) {
                    if (area is null) continue;
                    if (at != linkedAt || !b.Bytes.Slice(at, area.Length).SequenceEqual(area)) failures.Add($"{path}: the {name} data changed");
                }
                if (image.Shared is { } shared && !b.Bytes.Slice((int)vb.U32(vb.Workers[0].Header + 0x30), shared.Length).SequenceEqual(shared)) failures.Add($"{path}: the shared data changed");
                HashSet<int> patched = [.. targets.Select(t => t.Worker)];
                for (int w = 0; w < image.Workers.Count; w++) {
                    if (!patched.Contains(w) && !image.Workers[w].Code.AsSpan().SequenceEqual(again.Workers[w].Code)) failures.Add($"{path}: worker {w}'s code changed");
                }
                foreach ((int w, uint start, uint end, uint before, uint after) in targets) {
                    WorkerImage vw = image.Workers[w], lw = again.Workers[w];
                    int entry = Array.IndexOf(vw.Entries, start);
                    if (Copied(vw, lw, entry, start, end, before, after) is string problem) failures.Add($"{path}: worker {w} @{start:x}: {problem}");
                }
                int tail = vanilla.Bytes.Length - (vanilla.SectionStart + vanilla.SectionLength);
                if (!vanilla.Bytes[^tail..].SequenceEqual(result.Bytes[^tail..])) failures.Add($"{path}: the later sections changed");
                Interlocked.Increment(ref linked);
                Interlocked.Add(ref methods, targets.Count);
                growth.Add(path, r.File.Length - bytes.Length);
            }
            catch (Exception ex) when (ex is ScriptFormatException or ArgumentException or InvalidOperationException) {
                failures.Add($"{path}: {ex.GetType().Name}: {ex.Message}");
            }
        });
        output.WriteLine($"{linked} scripts linked with injects in {methods} methods, {skipped} without a method to inject into, {tooLarge} method(s) with too many labels to copy; files grew by {growth} (code region laid out again: {regionGrowth})");
        foreach (string f in failures.Order(StringComparer.Ordinal).Take(40)) output.WriteLine(f);
        Assert.Empty(failures);
        Assert.Equal(paths.Count, linked + skipped);
    }

    /// <summary>How much linked files grew: the average and the largest, with its file.</summary>
    private sealed class Growth {
        private readonly object _lock = new();
        private long _sum;
        private int _count;
        private (long Bytes, string Path) _max = (long.MinValue, "");

        public void Add(string path, long bytes) {
            lock (_lock) {
                _sum += bytes;
                _count++;
                if (bytes > _max.Bytes) _max = (bytes, path);
            }
        }

        public override string ToString() =>
            _count == 0 ? "nothing" : $"{_sum / _count} bytes on average, at most {_max.Bytes} ({Path.GetFileName(_max.Path)})";
    }

    /// <summary>
    ///     Whether the code region is the vanilla one, in place (<see cref="CodeLayout.InPlace"/>): the same code offset and
    ///     length, every worker's code at its vanilla offsets with its vanilla bytes, the patched workers' too (their new
    ///     code is in their tails) but for the trampoline where their first method was patched (a <c>jmp</c> to its new
    ///     code, then <c>nop</c>s to the end of the instructions it took); a problem when not.
    /// </summary>
    private static string? KeptInPlace(ScriptBlock vanilla, ScriptBlock linked, ScriptImage vanillaImage, ScriptImage linkedImage, int[] patched) {
        if (linked.CodeStart != vanilla.CodeStart || linked.CodeLength != vanilla.CodeLength) return "the code region moved";
        for (int w = 0; w < vanilla.Workers.Count; w++) {
            Bytecode.Script.Worker v = vanilla.Workers[w], l = linked.Workers[w];
            if (l.Start != v.Start || l.End != v.End) return $"worker {w}'s code moved";
            if (patched.Contains(w) != l.HasTail) return $"worker {w} {(l.HasTail ? "has" : "has no")} tail";
            byte[] was = vanillaImage.Workers[w].Code;
            ReadOnlySpan<byte> now = linkedImage.Workers[w].Code.AsSpan(0, linkedImage.RegionLength(w));
            int kept = 0;
            if (patched.Contains(w) && now.Length >= 3 && now[0] == (Opcodes.Jmp | 0x80) && !now[..3].SequenceEqual(was.AsSpan(0, 3))) {
                while (kept < 3) kept += (was[kept] & 0x80) != 0 ? 3 : 1;
                for (int i = 3; i < kept; i++) {
                    if (now[i] != Opcodes.Nop) return $"worker {w}'s trampoline is not a jmp and nops";
                }
            }
            if (!now[kept..].SequenceEqual(was.AsSpan(kept))) return $"worker {w}'s vanilla code changed";
        }
        return null;
    }

    /// <summary>Links with the code region laid out again (<see cref="CodeLayout.Region"/>), checks that it lays itself out again, and returns the file's length.</summary>
    private static int LinkRegion(byte[] bytes, List<ModScript> mods, ConcurrentBag<string> failures, string path) {
        LinkResult r = ScriptLinker.Link(bytes, mods, layout: CodeLayout.Region);
        if (!r.Changed) {
            failures.Add($"{path}: region layout: {string.Join("; ", r.Messages.Where(m => m.Severity == LinkSeverity.Error))}");
            return bytes.Length;
        }
        ScriptBlock b = ScriptFile.Parse(r.File).Script;
        if (!ScriptImage.FromBlock(b).Write().AsSpan().SequenceEqual(b.Bytes[..(int)b.U32(0x10)])) failures.Add($"{path}: region layout: does not lay itself out again");
        return r.File.Length;
    }

    /// <summary>
    ///     A method's statements (code from an empty stack to the next, as the decompiler cuts them), offsets in the worker;
    ///     null when its stack cannot be followed (a native without a known argument count).
    /// </summary>
    private static List<(uint Start, uint End)>? Statements(byte[] code, uint start, uint end, Bytecode.Analysis.NativeSignatures natives) {
        List<(uint, uint)> result = [];
        int depth = 0;
        uint first = start;
        for (uint pc = start; pc < end;) {
            byte b = code[pc];
            byte op = (byte)(b & 0x7f);
            ushort operand = (b & 0x80) != 0 && pc + 2 < code.Length ? (ushort)(code[pc + 1] | code[pc + 2] << 8) : (ushort)0;
            (int pops, int pushes) = op is Opcodes.Func or Opcodes.FuncFacc
                ? natives[operand] is { } n ? StackEffects.Native(op, n.Argc) : (-1, 0)
                : StackEffects.Of(op);
            if (pops < 0 || pops > depth) return null;
            depth = depth - pops + pushes;
            pc += (b & 0x80) != 0 ? 3u : 1u;
            if (depth == 0) {
                result.Add((first, pc));
                first = pc;
            }
        }
        return depth == 0 ? result : null;
    }

    /// <summary>
    ///     Whether the linked worker's method at entry <paramref name="entry"/> is the vanilla code <paramref name="start"/> to
    ///     <paramref name="end"/> with one <c>nop</c> before <paramref name="before"/> and two after the statement ending at
    ///     <paramref name="after"/>, each exit a jump, then the exit; a problem when not.
    /// </summary>
    private static string? Copied(WorkerImage vanilla, WorkerImage linked, int entry, uint start, uint end, uint before, uint after) {
        byte exit = vanilla.Type == 0 ? Opcodes.Ret : Opcodes.End;
        List<Instr> source = Decode(vanilla.Code, start, end);
        List<(byte Code, ushort? Operand, uint Length)> expected = [];
        (byte, ushort?, uint) nop = (Opcodes.Nop, null, 1), jmp = (Opcodes.Jmp, null, 3);
        foreach (Instr i in source) {
            if (i.Offset == after) expected.AddRange([nop, nop]);
            if (i.Offset == before) expected.Add(nop);
            expected.Add(i.Code == exit ? jmp : (i.Code, Opcodes.All[i.Code].Operand == OperandKind.Label ? null : i.Operand, i.Length));
        }
        if (after == end) expected.AddRange([nop, nop]);
        if (source.Count == 0 || after == end || !(source[^1].Code is Opcodes.Jmp or Opcodes.PopAJmp or Opcodes.End or Opcodes.EndP or Opcodes.End1 or Opcodes.EndP1 or Opcodes.Ret)) {
            expected.Add(jmp);
        }
        expected.Add((exit, null, 1));
        uint at = linked.Entries[entry];
        // The worker's first method keeps its entries and jumps to its new code (the trampoline).
        if (at == start && linked.Code[at] == (Opcodes.Jmp | 0x80) && !vanilla.Code.AsSpan((int)at, 3).SequenceEqual(linked.Code.AsSpan((int)at, 3))) {
            at = linked.Labels[linked.Code[at + 1] | linked.Code[at + 2] << 8];
        }
        int expectedBytes = (int)expected.Sum(e => e.Length);
        if (at + expectedBytes > linked.Code.Length) return "the copy runs past the code";
        List<Instr> copy = Decode(linked.Code, at, at + (uint)expectedBytes);
        if (copy.Count != expected.Count) return $"{copy.Count} instructions, {expected.Count} expected";
        for (int k = 0; k < copy.Count; k++) {
            if (copy[k].Code != expected[k].Code) return $"instruction {k}: {Opcodes.All[copy[k].Code].Name}, {Opcodes.All[expected[k].Code].Name} expected";
            if (expected[k].Operand is ushort o && copy[k].Operand != o) return $"instruction {k}: operand {copy[k].Operand}, {o} expected";
        }
        return null;
    }

    private static List<Instr> Decode(byte[] code, uint start, uint end) {
        List<Instr> result = [];
        for (uint pc = start; pc < end;) {
            byte b = code[pc];
            ushort? operand = (b & 0x80) != 0 ? (ushort)(code[pc + 1] | code[pc + 2] << 8) : null;
            Instr i = new(pc, (byte)(b & 0x7f), operand);
            result.Add(i);
            pc += i.Length;
        }
        return result;
    }
}
