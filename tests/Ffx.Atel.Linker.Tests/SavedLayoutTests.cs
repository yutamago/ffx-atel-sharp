// SPDX-License-Identifier: MIT

namespace Ffx.Atel.Linker.Tests;

/// <summary>The run's table of saved fields (<see cref="SavedLayout"/>): offsets in the save's mod area, and which mods it leaves out.</summary>
public sealed class SavedLayoutTests : IDisposable {
    private readonly string _dir = Directory.CreateTempSubdirectory("atel-saved-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static SavedField Field(string key, int type, int length = 1) => new() { Key = key, Type = type, Length = length, Array = length > 1 };

    private static AtelMod Mod(string id, string[]? requires = null, params SavedField[] fields) {
        AtelMod m = new() { Id = id, Manifest = new ModManifest { Requires = [.. requires ?? []] } };
        m.Saved.AddRange(fields);
        return m;
    }

    /// <summary>A package of <paramref name="mod"/> that uses saved field <paramref name="key"/> (its own, or <c>other/key</c>).</summary>
    private static void Uses(AtelMod mod, string key, int type, int length = 1) {
        string path = $"ffx_ps2/test/{mod.Id}.bin";
        if (!mod.Packages.TryGetValue(path, out ScriptPackage? p)) mod.Packages[path] = p = new ScriptPackage { File = path, Script = mod.Id };
        p.Variables.Add(new NewVariable { Name = key, Type = type, Storage = 0, Length = length, Array = length > 1, Saved = key });
    }

    [Fact]
    public void FieldsArePlacedInLoadOrderEachAlignedToItsElement() {
        List<LinkMessage> messages = [];
        SavedLayout layout = SavedLayout.Build([
            Mod("a", null, Field("A.P.Flag", 0), Field("A.P.Count", 2), Field("A.P.Total", 5)),
            Mod("none"),
            Mod("b", null, Field("B.Q.Bytes", 0, 3), Field("B.Q.Speed", 6)),
        ], messages, out List<AtelMod> kept);
        Assert.Equal(["a", "none", "b"], kept.Select(m => m.Id));
        Assert.Equal([("a/A.P.Flag", 0x62cc), ("a/A.P.Count", 0x62ce), ("a/A.P.Total", 0x62d0), ("b/B.Q.Bytes", 0x62d4), ("b/B.Q.Speed", 0x62d8)],
            layout.Fields.Select(f => (f.FullKey, f.Offset)));
        Assert.Equal(0x62d8 + 4 - SaveArea.Start, layout.Used);
        Assert.Equal(0x62d0, layout.Find("a", "A.P.Total")!.Offset);
        Assert.Equal(0x62d8, layout.Find("a", "b/B.Q.Speed")!.Offset);
        Assert.Null(layout.Find("a", "B.Q.Speed"));
        Assert.Equal(["a", "b"], layout.Mods.Order());
        Assert.DoesNotContain(messages, m => m.Severity == LinkSeverity.Error);
    }

    [Fact]
    public void TheSameModsInAnotherOrderGetOtherOffsets() {
        AtelMod a = Mod("a", null, Field("A.X", 5)), b = Mod("b", null, Field("B.Y", 5));
        SavedLayout ab = SavedLayout.Build([a, b], [], out _), ba = SavedLayout.Build([b, a], [], out _);
        Assert.Equal(SaveArea.Start, ab.Find("a/A.X")!.Offset);
        Assert.Equal(SaveArea.Start, ba.Find("b/B.Y")!.Offset);
    }

    /// <summary>Over 1,032 bytes: the mod that does not fit after the earlier ones is left out, a later one that fits is not.</summary>
    [Fact]
    public void AModWhoseFieldsDoNotFitIsLeftOut() {
        List<LinkMessage> messages = [];
        SavedLayout layout = SavedLayout.Build([
            Mod("big", null, Field("Big.Table", 0, 1000)),
            Mod("late", null, Field("Late.Table", 0, 20), Field("Late.More", 0, 20)),
            Mod("small", null, Field("Small.Count", 5)),
        ], messages, out List<AtelMod> kept);
        Assert.Equal(["big", "small"], kept.Select(m => m.Id));
        Assert.Equal(["big/Big.Table", "small/Small.Count"], layout.Fields.Select(f => f.FullKey));
        Assert.Equal(SaveArea.Start + 1000, layout.Find("small/Small.Count")!.Offset);
        LinkMessage error = Assert.Single(messages, m => m.Severity == LinkSeverity.Error);
        Assert.Equal("late", error.Mod);
        Assert.Contains("do not fit", error.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactlyTheWholeAreaFits() {
        SavedLayout layout = SavedLayout.Build([Mod("a", null, Field("A.All", 4, SaveArea.Length / 4))], [], out List<AtelMod> kept);
        Assert.Single(kept);
        Assert.Equal(SaveArea.Length, layout.Used);
    }

    [Fact]
    public void AModReadsAnotherModsFieldByItsFullKey() {
        AtelMod owner = Mod("yuta.larvacount", null, Field("LarvaCount.Progress.LarvasMet", 2));
        AtelMod reader = Mod("yuta.reader", ["yuta.larvacount"]);
        Uses(reader, "yuta.larvacount/LarvaCount.Progress.LarvasMet", 2);
        List<LinkMessage> messages = [];
        SavedLayout layout = SavedLayout.Build([reader, owner], messages, out List<AtelMod> kept);
        Assert.Equal(["yuta.reader", "yuta.larvacount"], kept.Select(m => m.Id));
        Assert.Equal(SaveArea.Start, layout.Find("yuta.reader", "yuta.larvacount/LarvaCount.Progress.LarvasMet")!.Offset);
        Assert.DoesNotContain(messages, m => m.Severity == LinkSeverity.Error);
    }

    [Theory]
    [InlineData("no-requires", "does not require")]
    [InlineData("missing-owner", "no linked mod declares")]
    [InlineData("other-type", "declares type 2")]
    [InlineData("own-undeclared", "not in its saved.json")]
    public void AReadThatDoesNotResolveLeavesTheReaderOut(string kind, string message) {
        AtelMod owner = Mod("owner", null, Field("O.Count", 2));
        AtelMod reader = Mod("reader", kind == "no-requires" ? null : ["owner"]);
        switch (kind) {
            case "no-requires": Uses(reader, "owner/O.Count", 2); break;
            case "missing-owner": Uses(reader, "owner/O.Gone", 2); break;
            case "other-type": Uses(reader, "owner/O.Count", 5); break;
            case "own-undeclared": Uses(reader, "R.Mine", 5); break;
        }
        List<LinkMessage> messages = [];
        SavedLayout.Build([owner, reader], messages, out List<AtelMod> kept);
        Assert.Equal(["owner"], kept.Select(m => m.Id));
        LinkMessage error = Assert.Single(messages, m => m.Severity == LinkSeverity.Error);
        Assert.Equal("reader", error.Mod);
        Assert.Contains(message, error.Text, StringComparison.Ordinal);
    }

    /// <summary>An owner left out (its fields do not fit) takes its readers with it; their space goes to later mods.</summary>
    [Fact]
    public void TheReadersOfAModLeftOutAreLeftOutToo() {
        AtelMod big = Mod("big", null, Field("Big.Table", 0, 1000));
        AtelMod owner = Mod("owner", null, Field("O.Table", 0, 100));
        AtelMod reader = Mod("reader", ["owner"], Field("R.Own", 0, 30));
        Uses(reader, "owner/O.Table", 0, 100);
        AtelMod last = Mod("last", null, Field("L.Small", 0, 32));
        List<LinkMessage> messages = [];
        SavedLayout layout = SavedLayout.Build([big, owner, reader, last], messages, out List<AtelMod> kept);
        Assert.Equal(["big", "last"], kept.Select(m => m.Id));
        Assert.Equal(SaveArea.Start + 1000, layout.Find("last/L.Small")!.Offset);
        Assert.Equal(["owner", "reader"], messages.Where(m => m.Severity == LinkSeverity.Error).Select(m => m.Mod));
    }

    [Fact]
    public void TwoFieldsWithOneKeyLeaveTheModOut() {
        List<LinkMessage> messages = [];
        SavedLayout.Build([Mod("a", null, Field("A.X", 0), Field("A.X", 2))], messages, out List<AtelMod> kept);
        Assert.Empty(kept);
        Assert.Contains(messages, m => m.Severity == LinkSeverity.Error && m.Text.Contains("two saved fields", StringComparison.Ordinal));
    }

    [Fact]
    public void AModsSavedJsonIsReadAndIsNoPackage() {
        string dir = Path.Combine(_dir, "yuta.larvacount");
        Directory.CreateDirectory(Path.Combine(dir, AtelMod.PackageDir));
        SavedFile file = new() { Fields = [Field("LarvaCount.Progress.LarvasMet", 2), new SavedField { Key = "LarvaCount.Progress.Tier", Type = 0, Initial = [1] }] };
        File.WriteAllText(Path.Combine(dir, AtelMod.PackageDir, SavedFile.FileName), file.ToJson());
        AtelMod mod = AtelMod.Load("yuta.larvacount", dir);
        Assert.Empty(mod.Errors);
        Assert.Empty(mod.Packages);
        Assert.Equal(["LarvaCount.Progress.LarvasMet", "LarvaCount.Progress.Tier"], mod.Saved.Select(f => f.Key));
        Assert.Equal([1], mod.Saved[1].InitialBytes());
    }

    [Fact]
    public void APackageWithASavedFieldNeedsFormat4() {
        ScriptPackage p = new() { Variables = [new NewVariable { Name = "X", Storage = 0, Saved = "A.X" }] };
        Assert.Equal(ScriptPackage.SavedFormat, p.NeededFormat());
        Assert.Equal(ScriptPackage.CurrentFormat, ScriptPackage.SavedFormat);
    }
}
