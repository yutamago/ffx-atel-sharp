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
        long growth = 0;
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
                Interlocked.Add(ref growth, r.File.Length - f.Bytes.Length);
            }
            catch (Exception ex) when (ex is ScriptFormatException or ArgumentException) {
                failures.Add($"{f.Path}: {ex.Message}");
            }
        });
        output.WriteLine($"{linked} scripts linked, {skipped} without workers or shared data; files grew by {growth / Math.Max(linked, 1)} bytes on average");
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
}
