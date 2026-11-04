// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Cuts.Tests;

/// <summary>The tracker over made-up game memory: scripts loaded, read again, moved and unloaded.</summary>
public sealed class CutTrackerTests {
    /// <summary>Ctrl works holding blocks by made-up address.</summary>
    private sealed class FakeMemory : IScriptMemory {
        public Dictionary<nint, byte[]> Memory { get; } = [];
        public List<nint>[] Ctrl { get; } = [.. Enumerable.Range(0, 7).Select(_ => new List<nint>())];
        public int CtrlWorks => Ctrl.Length;
        public bool InUse(int ctrl) => Ctrl[ctrl].Count > 0;
        public void Blocks(int ctrl, List<nint> into) => into.AddRange(Ctrl[ctrl]);
        public Span<byte> Bytes(nint block, int length) => Memory[block].AsSpan(0, length);

        /// <summary>The game reads a script: a fresh copy at <paramref name="at"/>, run by two workers of <paramref name="ctrl"/>.</summary>
        public void Load(int ctrl, nint at, byte[] block) {
            Memory[at] = (byte[])block.Clone();
            Ctrl[ctrl].AddRange([at, at]);
        }
    }

    private readonly List<string> _info = [], _errors = [];
    private readonly CutStates _states = new();
    private CutTracker Tracker() => new(Fixture.Catalog(), _states, _info.Add, _errors.Add);

    [Fact]
    public void ALoadedScriptGetsTheSwitchesAndReadingItAgainGetsThemAgain() {
        byte[] vanilla = Fixture.Block();
        FakeMemory mem = new();
        CutTracker t = Tracker();
        _states.Set("Plain", true);
        mem.Load(2, 0x1000, vanilla);
        t.Tick(mem);
        Assert.Equal(DeadJumps.NopByte, mem.Memory[0x1000][Fixture.PlainAt0(vanilla)]);
        Assert.Equal(DeadJumps.NopByte, mem.Memory[0x1000][Fixture.PlainAt1(vanilla)]);
        Assert.Equal(DeadJumps.JmpByte, mem.Memory[0x1000][Fixture.ElseAt(vanilla)]);
        LoadedCut plain = Assert.Single(t.Status.Loaded["Plain"]);
        Assert.Equal(new LoadedCut("997", 2, 2, 2, 0, null), plain);
        Assert.Equal(new LoadedCut("997", 2, 1, 0, 0, null), Assert.Single(t.Status.Loaded["WithElse"]));
        Assert.Empty(_errors);

        // The game reads the file again into the same place: the next frame switches it again.
        mem.Memory[0x1000] = (byte[])vanilla.Clone();
        t.Tick(mem);
        Assert.Equal(DeadJumps.NopByte, mem.Memory[0x1000][Fixture.PlainAt0(vanilla)]);
        Assert.Equal(1, t.Scans);

        // Off, then as loaded.
        _states.Set("Plain", false);
        t.Tick(mem);
        Assert.Equal(vanilla, mem.Memory[0x1000]);
        _states.Set("Plain", null);
        _states.Set("WithElse", true);
        t.Tick(mem);
        Assert.Equal([Fixture.ElseAt(vanilla)], Enumerable.Range(0, vanilla.Length).Where(i => vanilla[i] != mem.Memory[0x1000][i]));
    }

    [Fact]
    public void TheSameScriptElsewhereIsNotScannedAgainAndOtherScriptsNotAtAll() {
        byte[] vanilla = Fixture.Block();
        FakeMemory mem = new();
        CutTracker t = Tracker();
        _states.Set("WithElse", true);
        mem.Load(0, 0x1000, vanilla);
        mem.Load(3, 0x8000, vanilla);
        mem.Load(3, 0x9000, Fixture.Block("998"));
        t.Tick(mem);
        Assert.Equal(1, t.Scans);
        Assert.Equal(2, t.Status.Loaded["WithElse"].Count);
        Assert.Equal(DeadJumps.NopByte, mem.Memory[0x8000][Fixture.ElseAt(vanilla)]);
        Assert.Equal(Fixture.Block("998"), mem.Memory[0x9000]);

        // Unloaded: its ctrl work stops, or no worker runs it any more.
        mem.Ctrl[0].Clear();
        t.Tick(mem);
        Assert.Equal(3, Assert.Single(t.Status.Loaded["WithElse"]).Ctrl);
        mem.Ctrl[3].RemoveAll(a => a == 0x8000);
        t.Tick(mem);
        Assert.False(t.Status.Loaded.ContainsKey("WithElse"));
        Assert.Contains(_info, m => m.Contains("997 unloaded", StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherBlockAtTheSameAddressIsSeenAnew() {
        byte[] vanilla = Fixture.Block();
        FakeMemory mem = new();
        CutTracker t = Tracker();
        mem.Load(0, 0x1000, Fixture.Block("998"));
        t.Tick(mem);
        Assert.Empty(t.Status.Loaded);
        _states.Set("Plain", true);
        mem.Memory[0x1000] = (byte[])vanilla.Clone();    // the next map's script, same address
        t.Tick(mem);
        Assert.Equal(2, Assert.Single(t.Status.Loaded["Plain"]).On);
    }

    [Fact]
    public void BytesThatAreNotTheJumpAreReportedOnceAndLeftAlone() {
        byte[] vanilla = Fixture.Block();
        FakeMemory mem = new();
        CutTracker t = Tracker();
        mem.Load(0, 0x1000, vanilla);
        t.Tick(mem);
        // After the scan, someone else's patch changes the second block's operand.
        mem.Memory[0x1000][Fixture.PlainAt1(vanilla) + 2] = 0x7f;
        byte[] before = (byte[])mem.Memory[0x1000].Clone();
        _states.Set("Plain", true);
        _states.Set("WithElse", true);
        t.Tick(mem);
        t.Tick(mem);
        Assert.Single(_errors);
        Assert.NotNull(Assert.Single(t.Status.Loaded["Plain"]).Problem);
        Assert.Equal(before[Fixture.PlainAt0(vanilla)], mem.Memory[0x1000][Fixture.PlainAt0(vanilla)]);
        Assert.Equal(DeadJumps.NopByte, mem.Memory[0x1000][Fixture.ElseAt(vanilla)]);    // the other name still switches
    }

    [Fact]
    public void ABlockSeenAgainWithTheSwitchesBytesKeepsTheFilesAsLoaded() {
        byte[] vanilla = Fixture.Block();
        FakeMemory mem = new();
        CutTracker t = Tracker();
        _states.Set("Plain", true);
        mem.Load(1, 0x1000, vanilla);
        t.Tick(mem);
        // The ctrl work lets go of the block and registers the same memory again, without reading the file.
        List<nint> held = [.. mem.Ctrl[1]];
        mem.Ctrl[1].Clear();
        t.Tick(mem);
        mem.Ctrl[1].AddRange(held);
        t.Tick(mem);
        Assert.Equal(new LoadedCut("997", 1, 2, 2, 0, null), Assert.Single(t.Status.Loaded["Plain"]));
        _states.Set("Plain", null);
        t.Tick(mem);
        Assert.Equal(vanilla, mem.Memory[0x1000]);
    }

    [Fact]
    public void AScriptThatDoesNotParseIsReported() {
        byte[] broken = Fixture.Block();
        broken[0x34] = 0xff;    // more workers than the block has headers
        FakeMemory mem = new();
        CutTracker t = Tracker();
        mem.Load(0, 0x1000, broken);
        t.Tick(mem);
        t.Tick(mem);
        Assert.Contains("does not parse", Assert.Single(_errors), StringComparison.Ordinal);
        Assert.Empty(t.Status.Loaded);
    }
}
