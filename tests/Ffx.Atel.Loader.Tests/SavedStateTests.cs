// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

using Ffx.Atel.Linker;

namespace Ffx.Atel.Loader.Tests;

/// <summary>
///     The saved fields at run time (<see cref="SavedState"/>): the mod area rebuilt by key on a load, taken apart on a save,
///     for the cases of docs/research/mod-saves.md ("Cases").
/// </summary>
public sealed class SavedStateTests {
    private static readonly SaveFingerprint Save1 = new(0x61e2, 1234567);

    private static AtelMod Counter() {
        AtelMod m = new() { Id = "yuta.larvacount" };
        m.Saved.Add(new SavedField { Key = "LarvaCount.Progress.LarvasMet", Type = 2 });
        m.Saved.Add(new SavedField { Key = "LarvaCount.Progress.Tier", Type = 0, Initial = [1] });
        m.Saved.Add(new SavedField { Key = "LarvaCount.Progress.Flags", Type = 0, Length = 4, Array = true });
        return m;
    }

    private static AtelMod Other() {
        AtelMod m = new() { Id = "yuta.other" };
        m.Saved.Add(new SavedField { Key = "Other.Stats.Kills", Type = 5, Initial = [7, 0, 0, 0] });
        return m;
    }

    private static SavedState State(params AtelMod[] mods) => new(SavedLayout.Build(mods, [], out _));

    private static byte[] Area() => new byte[SaveArea.Length];

    private static int Read(SavedState s, byte[] area, string key) {
        PlacedField f = s.Layout.Find(key)!;
        return f.Field.ElementSize switch {
            1 => area[f.AreaOffset],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(area.AsSpan(f.AreaOffset)),
            _ => BinaryPrimitives.ReadInt32LittleEndian(area.AsSpan(f.AreaOffset)),
        };
    }

    private static void Write(SavedState s, byte[] area, string key, int value) {
        PlacedField f = s.Layout.Find(key)!;
        switch (f.Field.ElementSize) {
            case 1: area[f.AreaOffset] = (byte)value; break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(f.AreaOffset), (ushort)value); break;
            default: BinaryPrimitives.WriteInt32LittleEndian(area.AsSpan(f.AreaOffset), value); break;
        }
    }

    /// <summary>A state file as Fahrenheit hands it over: written to a file and read back.</summary>
    private static StateFile RoundTrip(StateFile s) {
        using MemoryStream stream = new();
        stream.Write(new byte[5000]);    // an older, longer state in the file: Write truncates
        SavedState.Write(stream, s);
        StateFile? back = SavedState.Read(stream, out string? problem);
        Assert.Null(problem);
        return back!;
    }

    [Fact]
    public void ANewGameGivesEveryFieldItsInitialValue() {
        SavedState s = State(Counter(), Other());
        byte[] area = Area();
        area.AsSpan().Fill(0xee);
        s.NewGame(area);
        Assert.Equal(0, Read(s, area, "yuta.larvacount/LarvaCount.Progress.LarvasMet"));
        Assert.Equal(1, Read(s, area, "yuta.larvacount/LarvaCount.Progress.Tier"));
        Assert.Equal(7, Read(s, area, "yuta.other/Other.Stats.Kills"));
        Assert.All(area.Skip(s.Layout.Used), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ValuesComeBackByNameWhenTheOffsetsMove() {
        SavedState first = State(Counter(), Other());
        byte[] area = Area();
        first.NewGame(area);
        Write(first, area, "yuta.larvacount/LarvaCount.Progress.LarvasMet", 12);
        Write(first, area, "yuta.other/Other.Stats.Kills", 99);
        area[first.Layout.Find("yuta.larvacount/LarvaCount.Progress.Flags")!.AreaOffset + 2] = 5;
        StateFile saved = RoundTrip(first.Capture(area, Save1));

        // Another run, the mods in the other order: every offset differs.
        SavedState second = State(Other(), Counter());
        Assert.NotEqual(first.Layout.Find("yuta.other/Other.Stats.Kills")!.Offset, second.Layout.Find("yuta.other/Other.Stats.Kills")!.Offset);
        byte[] loaded = Area();
        List<(bool Warning, string Text)> report = second.Load(loaded, saved, Save1);
        Assert.DoesNotContain(report, r => r.Warning);
        Assert.Equal(12, Read(second, loaded, "yuta.larvacount/LarvaCount.Progress.LarvasMet"));
        Assert.Equal(99, Read(second, loaded, "yuta.other/Other.Stats.Kills"));
        Assert.Equal(1, Read(second, loaded, "yuta.larvacount/LarvaCount.Progress.Tier"));
        Assert.Equal(5, loaded[second.Layout.Find("yuta.larvacount/LarvaCount.Progress.Flags")!.AreaOffset + 2]);
    }

    /// <summary>A vanilla save (no state file, its tail zero or anything): the fields start from their initial values.</summary>
    [Fact]
    public void AVanillaSaveStartsFromTheInitialValues() {
        SavedState s = State(Counter());
        byte[] area = Area();
        area.AsSpan().Fill(0x5a);
        s.Load(area, null, null);
        Assert.Equal(0, Read(s, area, "yuta.larvacount/LarvaCount.Progress.LarvasMet"));
        Assert.Equal(1, Read(s, area, "yuta.larvacount/LarvaCount.Progress.Tier"));
        Assert.All(area.Skip(s.Layout.Used), b => Assert.Equal(0, b));
    }

    /// <summary>A save made without one of the mods: its fields start from their initial values, the other mod's load.</summary>
    [Fact]
    public void ASaveMadeWithoutTheModGivesItsFieldsTheirInitialValues() {
        SavedState without = State(Other());
        byte[] area = Area();
        without.NewGame(area);
        Write(without, area, "yuta.other/Other.Stats.Kills", 3);
        StateFile saved = RoundTrip(without.Capture(area, Save1));

        SavedState with = State(Counter(), Other());
        byte[] loaded = Area();
        with.Load(loaded, saved, Save1);
        Assert.Equal(3, Read(with, loaded, "yuta.other/Other.Stats.Kills"));
        Assert.Equal(1, Read(with, loaded, "yuta.larvacount/LarvaCount.Progress.Tier"));
        Assert.Equal(0, Read(with, loaded, "yuta.larvacount/LarvaCount.Progress.LarvasMet"));
    }

    /// <summary>A mod disabled for a while: its values stay in the state through the saves made without it, and come back.</summary>
    [Fact]
    public void ADisabledModsValuesAreKeptAndComeBack() {
        SavedState both = State(Counter(), Other());
        byte[] area = Area();
        both.NewGame(area);
        Write(both, area, "yuta.larvacount/LarvaCount.Progress.LarvasMet", 41);
        StateFile first = RoundTrip(both.Capture(area, Save1));

        // Without the counter: its bytes are not in the area, which the other mod has to itself.
        SavedState alone = State(Other());
        byte[] middle = Area();
        List<(bool Warning, string Text)> report = alone.Load(middle, first, Save1);
        Assert.Contains("yuta.larvacount", alone.KeptMods);
        Assert.Contains(report, r => r.Text.Contains("kept for mods not linked now: yuta.larvacount", StringComparison.Ordinal));
        Assert.All(middle.Skip(4), b => Assert.Equal(0, b));
        SaveFingerprint save2 = new(0x1111, 2000000);
        StateFile second = RoundTrip(alone.Capture(middle, save2));
        Assert.True(second.Vars.ContainsKey("yuta.larvacount"));

        SavedState again = State(Counter(), Other());
        byte[] last = Area();
        again.Load(last, second, save2);
        Assert.Equal(41, Read(again, last, "yuta.larvacount/LarvaCount.Progress.LarvasMet"));
        Assert.Empty(again.KeptMods);
    }

    /// <summary>The state of a save made later without the loader (the fingerprint differs): ignored, with a warning.</summary>
    [Fact]
    public void AStateOfAnotherSaveIsIgnoredWithAWarning() {
        SavedState s = State(Counter());
        byte[] area = Area();
        s.NewGame(area);
        Write(s, area, "yuta.larvacount/LarvaCount.Progress.LarvasMet", 9);
        StateFile saved = RoundTrip(s.Capture(area, Save1));
        byte[] loaded = Area();
        List<(bool Warning, string Text)> report = s.Load(loaded, saved, Save1 with { PlayTime = Save1.PlayTime + 30 });
        Assert.Contains(report, r => r.Warning && r.Text.Contains("another save", StringComparison.Ordinal));
        Assert.Equal(0, Read(s, loaded, "yuta.larvacount/LarvaCount.Progress.LarvasMet"));
        Assert.Equal(1, Read(s, loaded, "yuta.larvacount/LarvaCount.Progress.Tier"));
    }

    /// <summary>A mod update: a longer array keeps its first elements, a field of another type starts again with a warning.</summary>
    [Fact]
    public void AnUpdatedFieldKeepsWhatStillFits() {
        SavedState old = State(Counter());
        byte[] area = Area();
        old.NewGame(area);
        Write(old, area, "yuta.larvacount/LarvaCount.Progress.LarvasMet", 300);
        int flags = old.Layout.Find("yuta.larvacount/LarvaCount.Progress.Flags")!.AreaOffset;
        area[flags] = 1; area[flags + 3] = 4;
        StateFile saved = RoundTrip(old.Capture(area, Save1));

        AtelMod updated = new() { Id = "yuta.larvacount" };
        updated.Saved.Add(new SavedField { Key = "LarvaCount.Progress.LarvasMet", Type = 5 });
        updated.Saved.Add(new SavedField { Key = "LarvaCount.Progress.Flags", Type = 0, Length = 6, Array = true, Initial = [9, 9, 9, 9, 9, 9] });
        SavedState now = State(updated);
        byte[] loaded = Area();
        List<(bool Warning, string Text)> report = now.Load(loaded, saved, Save1);
        Assert.Contains(report, r => r.Warning && r.Text.Contains("LarvasMet was saved as type 2", StringComparison.Ordinal));
        Assert.Equal(0, Read(now, loaded, "yuta.larvacount/LarvaCount.Progress.LarvasMet"));
        int at = now.Layout.Find("yuta.larvacount/LarvaCount.Progress.Flags")!.AreaOffset;
        Assert.Equal([1, 0, 0, 4, 9, 9], loaded.AsSpan(at, 6).ToArray());
    }

    [Fact]
    public void ANewGameForgetsTheKeptValues() {
        SavedState both = State(Counter(), Other());
        byte[] area = Area();
        both.NewGame(area);
        StateFile saved = both.Capture(area, Save1);
        SavedState alone = State(Other());
        alone.Load(Area(), saved, Save1);
        Assert.NotEmpty(alone.KeptMods);
        alone.NewGame(Area());
        Assert.Empty(alone.KeptMods);
        Assert.False(alone.Capture(Area(), Save1).Vars.ContainsKey("yuta.larvacount"));
    }

    [Fact]
    public void TheFingerprintIsReadFromTheSaveFile() {
        byte[] file = new byte[SaveArea.FileLength];
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x1a), 0x61e2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x40 + 0xbc), 1234567);
        Assert.Equal(Save1, SaveFingerprint.OfFile(file));
    }

    [Fact]
    public void ABrokenStateFileIsReportedNotThrown() {
        using MemoryStream stream = new("{ not json"u8.ToArray());
        Assert.Null(SavedState.Read(stream, out string? problem));
        Assert.NotNull(problem);
        using MemoryStream empty = new();
        Assert.Null(SavedState.Read(empty, out problem));
        Assert.Null(problem);
    }
}
