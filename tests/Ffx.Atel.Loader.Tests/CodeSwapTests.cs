// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Linker;

namespace Ffx.Atel.Loader.Tests;

/// <summary>Code swaps in memory: when a running block may be replaced by a newly linked one (docs/formats/debug.md).</summary>
public sealed class CodeSwapTests {
    private const string Script = "ffx_ps2/test/m996.bin";

    // Entries 0 (Init), 1 (a loop, Main) and 2; Init sets i0 from a constant, entry 2 from a variable.
    private const string Code = """
        e0:
            pushimm 1
            popi0
            end
        e1:
        l0:
            pushimm 2
            popi0
            jmp l0
        e2:
            pushv v0
            popi0
            end
        """;

    private static byte[] Vanilla(uint privateLength = 0) {
        ScriptImage image = new() { Author = "t"u8.ToArray(), Name = "996"u8.ToArray(), Shared = new byte[0x20], Other = new byte[0x40] };
        image.Variables.Add(new VarEntry(0x4a00_0000, 1, 0));    // an i32 in the shared data
        WorkerAssembler a = new();
        a.Add(Code);
        AssembledWorker w = a.Finish();
        image.Workers.Add(new WorkerImage {
            Type = 1, Code = w.Code, Entries = w.Entries, Labels = w.Labels,
            PrivateLength = privateLength, Private = privateLength > 0 ? new byte[privateLength] : null,
        });
        byte[] block = image.Write();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    private static ScriptBlock Block(byte[] file) => ScriptFile.Parse(file).Script;

    /// <summary>The vanilla file linked with a postfix on <paramref name="entry"/> that sets i0 to 7.</summary>
    private static byte[] Postfix(byte[] vanilla, int entry) {
        WorkerImage w = ScriptImage.FromBlock(Block(vanilla)).Workers[0];
        uint start = w.Entries[entry], end = entry + 1 < w.Entries.Length ? w.Entries[entry + 1] : (uint)w.Code.Length;
        ScriptPackage p = new() { File = Script, Script = "m996" };
        p.Vanilla.Workers.Add(new WorkerRef(0, "W00", VanillaHash.Worker(w)));
        p.Vanilla.Entries.Add(new EntryRef(0, $"E{entry}", [entry], start, end, VanillaHash.Range(w, start, end)));
        p.Patches.Add(new EntryPatch { Kind = PatchKind.Postfix, Worker = 0, Name = "After", Target = $"E{entry}", Code = new Fragment { Bytes = [0xae, 7, 0, 0x59] } });
        AtelMod mod = new() { Id = "test" };
        mod.Packages[Script] = p;
        LinkedFile linked = Assert.Single(ModSet.Link([mod], path => path == Script ? vanilla : null));
        Assert.Null(linked.Error);
        return linked.Result!.File;
    }

    private static LivePosition Pc(uint offset) => new(offset, 0, false, "worker 0 level 0");

    [Fact]
    public void MainKeepsRunningWhileInitIsPatched() {
        byte[] vanilla = Vanilla();
        ScriptBlock loaded = Block(vanilla), next = Block(Postfix(vanilla, 0));
        uint main = loaded.Workers[0].Entries[1];
        // Main's loop, mid-method, and a level idle at Init's start (where a reset leaves it).
        SwapCheck check = CodeSwap.Check(loaded, next, [Pc(main + 3), new LivePosition(0, 0, false, "worker 0 level 1")], privateInWorks: false);
        Assert.True(check.Safe, string.Join("; ", check.Reasons));
        Assert.Empty(check.Notes);
    }

    [Fact]
    public void ALevelInsideAChangedMethodWaits() {
        byte[] vanilla = Vanilla();
        ScriptBlock loaded = Block(vanilla), next = Block(Postfix(vanilla, 0));
        // Init's first instruction became the trampoline's jmp; a level past it would run the rest of Init on top of it.
        Assert.NotEqual(loaded.Bytes[loaded.CodeStart], next.Bytes[next.CodeStart]);
        SwapCheck check = CodeSwap.Check(loaded, next, [Pc(3)], privateInWorks: false);
        Assert.False(check.Safe);
        Assert.Contains("worker 0 level 0: it is inside worker 0's code 0x0-", Assert.Single(check.Reasons), StringComparison.Ordinal);
        // A return into it (after a call at 0) is the same.
        Assert.False(CodeSwap.Check(loaded, next, [new LivePosition(3, 0, true, "r")], privateInWorks: false).Safe);
        // One into Main is fine.
        Assert.True(CodeSwap.Check(loaded, next, [new LivePosition(loaded.Workers[0].Entries[1] + 3, 0, true, "r")], privateInWorks: false).Safe);
    }

    [Fact]
    public void ALevelInsideAMovedMethodFinishesItsOldCode() {
        byte[] vanilla = Vanilla();
        ScriptBlock loaded = Block(vanilla), next = Block(Postfix(vanilla, 2));
        uint e2 = loaded.Workers[0].Entries[2];
        Assert.NotEqual(e2, next.Workers[0].Entries[2]);    // moved to the tail
        SwapCheck check = CodeSwap.Check(loaded, next, [Pc(e2 + 3)], privateInWorks: false);
        Assert.True(check.Safe, string.Join("; ", check.Reasons));
        Assert.Contains("finishes the old code of worker 0's entry 2", Assert.Single(check.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedTablesUnderTheCodeAreNotSafe() {
        byte[] vanilla = Vanilla();
        ScriptBlock loaded = Block(vanilla);
        uint main = loaded.Workers[0].Entries[1], e2 = loaded.Workers[0].Entries[2];
        // Main's loop jumps through label 0; entry 2 reads variable 0.
        byte[] relabelled = loaded.ToArray();
        int labels = (int)loaded.U32(loaded.Workers[0].Header + 0x24);
        BinaryPrimitives.WriteUInt32LittleEndian(relabelled.AsSpan(labels), e2);
        SwapCheck check = CodeSwap.Check(loaded, ScriptBlock.Parse(relabelled), [Pc(main + 3)], privateInWorks: false);
        Assert.Contains("names label 0 of worker 0, which changed", Assert.Single(check.Reasons), StringComparison.Ordinal);

        byte[] retyped = loaded.ToArray();
        int vars = (int)loaded.U32(loaded.Workers[0].Header + 0x14);
        BinaryPrimitives.WriteUInt32LittleEndian(retyped.AsSpan(vars), 0x5a00_0000);
        check = CodeSwap.Check(loaded, ScriptBlock.Parse(retyped), [Pc(e2 + 3)], privateInWorks: false);
        Assert.Contains("names variable 0, which changed", Assert.Single(check.Reasons), StringComparison.Ordinal);
        // Not running there: nothing to keep consistent.
        Assert.True(CodeSwap.Check(loaded, ScriptBlock.Parse(retyped), [Pc(main + 3)], privateInWorks: false).Safe);
    }

    [Fact]
    public void ThePositionMustBeInAWorkersCode() {
        ScriptBlock loaded = Block(Vanilla());
        SwapCheck check = CodeSwap.Check(loaded, loaded, [Pc(0x4000)], privateInWorks: false);
        Assert.Contains("is in no worker's code", Assert.Single(check.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateDataMayGrowInEventsButNotInBattle() {
        ScriptBlock loaded = Block(Vanilla(privateLength: 8));
        byte[] grown = loaded.ToArray();
        int header = loaded.Workers[0].Header;
        BinaryPrimitives.WriteUInt32LittleEndian(grown.AsSpan(header + 0x10), 16);
        ScriptBlock next = ScriptBlock.Parse(grown);
        Assert.True(CodeSwap.Check(loaded, next, [], privateInWorks: false).Safe);
        SwapCheck battle = CodeSwap.Check(loaded, next, [], privateInWorks: true);
        Assert.False(battle.Safe);
        Assert.Contains("worker 0's private data grew (8 -> 16 bytes)", Assert.Single(battle.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public void ANewWorkerOrAMovedAreaIsNotSafe() {
        byte[] vanilla = Vanilla();
        ScriptBlock loaded = Block(vanilla);
        ScriptImage image = ScriptImage.FromBlock(loaded);
        image.Workers.Add(image.Workers[0]);
        SwapCheck check = CodeSwap.Check(loaded, ScriptBlock.Parse(image.Write()), [], privateInWorks: false);
        Assert.False(check.Safe);
        Assert.Contains(check.Reasons, r => r.StartsWith("the worker count changed (1 -> 2)", StringComparison.Ordinal));

        byte[] moved = loaded.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(moved.AsSpan(loaded.Workers[0].Header + 0x30), 0x10);
        Assert.Contains("worker 0's storage 4 moved", CodeSwap.Check(loaded, ScriptBlock.Parse(moved), [], privateInWorks: false).Reasons);
    }

    [Fact]
    public void TheMergeKeepsWhatTheGameWrote() {
        byte[] vanilla = Vanilla();
        ScriptBlock loaded = Block(vanilla), next = Block(Postfix(vanilla, 0));
        int shared = (int)loaded.U32(loaded.Workers[0].Header + 0x30);
        byte[] running = loaded.ToArray();
        running[shared] = 0x42;                                   // V0, set by the game
        byte[] merged = CodeSwap.Merge(loaded, next, running, out int kept, out int overridden);
        Assert.Equal(0x42, merged[shared]);
        Assert.Equal(1, kept);
        Assert.Equal(0, overridden);
        // Everything else is the new block.
        merged[shared] = next.Bytes[shared];
        Assert.Equal(next.ToArray(), merged);

        // A data edit of the same byte: the game's value stays.
        byte[] edited = next.ToArray();
        edited[shared] = 0x10;
        CodeSwap.Merge(loaded, ScriptBlock.Parse(edited), running, out _, out overridden);
        Assert.Equal(1, overridden);
    }

    [Fact]
    public void AMovedPrivateAreaTakesItsRunningValues() {
        ScriptBlock loaded = Block(Vanilla(privateLength: 8));
        int header = loaded.Workers[0].Header;
        int from = (int)loaded.U32(header + 0x2c);
        // The link moved the private data to the block's end and grew it.
        byte[] grown = [.. loaded.Bytes, .. new byte[0x20]];
        BinaryPrimitives.WriteUInt32LittleEndian(grown.AsSpan(header + 0x2c), (uint)loaded.Bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(grown.AsSpan(header + 0x10), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(grown.AsSpan(0x10), (uint)grown.Length);
        ScriptBlock next = ScriptBlock.Parse(grown);
        byte[] running = loaded.ToArray();
        running[from + 2] = 0x99;
        byte[] merged = CodeSwap.Merge(loaded, next, running, out _, out _);
        Assert.Equal(0x99, merged[loaded.Bytes.Length + 2]);
        Assert.Equal(loaded.Bytes.Length + 2, CodeSwap.Rebase(loaded, next, from + 2));
        Assert.Equal(0x20, CodeSwap.Rebase(loaded, next, 0x20));
    }

    [Fact]
    public void TheRunningBlockIsKnownByItsCode() {
        byte[] vanilla = Vanilla();
        ScriptBlock loaded = Block(vanilla), next = Block(Postfix(vanilla, 0));
        byte[] memory = [.. loaded.Bytes, 0xcc, 0xcc];
        memory[(int)loaded.U32(loaded.Workers[0].Header + 0x30)] = 0x42;    // data the game wrote
        Assert.True(CodeSwap.IsRunning(loaded, memory));
        Assert.False(CodeSwap.IsRunning(next, memory));
        Assert.False(CodeSwap.IsRunning(loaded, memory.AsSpan(0, 0x20)));
    }

    [Fact]
    public void WorksLieAsAtelGetBasicWorkFindsThem() {
        // 5 actors (2 big, one each of 0x558, 0x5b8, 0x2e8) and 2 more workers of 0x30.
        List<(int Offset, int Size)> works = CodeSwap.Works(actors: 5, all: 7, n5b8: 1, n558: 1, n2e8: 1);
        Assert.Equal([(0, 0xb58), (0xb58, 0xb58), (0x16b0, 0x558), (0x1c08, 0x5b8), (0x21c0, 0x2e8), (0x24a8, 0x30), (0x24d8, 0x30)], works);
        Assert.Equal(9, CodeSwap.Levels(0xb58, 1));
        Assert.Equal(2, CodeSwap.Levels(0x2e8, 5));
        Assert.Equal(0, CodeSwap.Levels(0x30, 4));
    }

    [Fact]
    public void AMethodRunsFromItsEntryToTheNext() {
        ScriptBlock loaded = Block(Vanilla());
        Worker w = loaded.Workers[0];
        Assert.Equal((0u, w.Entries[1]), CodeSwap.Method(w, 3));
        Assert.Equal((w.Entries[2], w.End), CodeSwap.Method(w, w.Entries[2] + 1));
    }
}
