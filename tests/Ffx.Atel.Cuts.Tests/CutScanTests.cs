// SPDX-License-Identifier: MIT

using System.Text.Json;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler.Layout;

namespace Ffx.Atel.Cuts.Tests;

/// <summary>Finding and switching named blocks in a script image, the catalog and the saved switches.</summary>
public sealed class CutScanTests {
    private static List<int> Differences(byte[] a, byte[] b) {
        Assert.Equal(a.Length, b.Length);
        return [.. Enumerable.Range(0, a.Length).Where(i => a[i] != b[i])];
    }

    [Fact]
    public void TheEmbeddedCatalogIsTheNamesFileAndTheCutConstants() {
        CutCatalog catalog = CutCatalog.Embedded();
        CutNames names = CutNames.Default;
        Assert.Equal(names.Names.Values.Distinct().Order(StringComparer.Ordinal), catalog.Cuts.Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.Equal(typeof(Ffx.Cut).GetFields().Select(f => f.Name).Order(StringComparer.Ordinal), catalog.Cuts.Select(c => c.Name).Order(StringComparer.Ordinal));
        foreach (CutInfo c in catalog.Cuts) {
            Assert.Equal(names.SignaturesOf(c.Name), c.Signatures);
            Assert.Equal(names.Scripts[c.Name], c.Scripts);
            Assert.False(string.IsNullOrWhiteSpace(c.Summary), $"{c.Name} has no summary");
            Assert.DoesNotContain("<", c.Summary, StringComparison.Ordinal);
        }
        Assert.Equal(["system_01", "znkd1300"], catalog.Scripts.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SummariesArePlainText() {
        CutCatalog catalog = Fixture.Catalog();
        Assert.Equal("Sets V0 to 8, as WithElse does not.", catalog.Cuts.Single(c => c.Name == "Plain").Summary);
        Assert.Equal("Sets V0 to 5 instead of 2.", catalog.Cuts.Single(c => c.Name == "WithElse").Summary);
        Assert.Equal(["Plain", "WithElse"], catalog.Cuts.Select(c => c.Name));
    }

    [Fact]
    public void TheScanFindsEachNamesJumps() {
        byte[] block = Fixture.Block();
        ScriptCuts cuts = ScriptCuts.Scan(block, "997", Fixture.Catalog());
        Assert.Equal("997", ScriptCuts.NameOf(block));
        Assert.Equal([Fixture.PlainAt0(block), Fixture.PlainAt1(block)], cuts.Sites["Plain"].Select(s => s.At));
        Assert.Equal([0, 1], cuts.Sites["Plain"].Select(s => s.Worker));
        CutSite withElse = Assert.Single(cuts.Sites["WithElse"]);
        Assert.Equal(Fixture.ElseAt(block), withElse.At);
        Assert.Equal(1, withElse.Label);
        Assert.All(cuts.Sites.Values.SelectMany(s => s), s => Assert.False(s.LoadedOn));
        Assert.Empty(ScriptCuts.Scan(block, "998", Fixture.Catalog()).Sites);    // not a listed script: not even parsed
    }

    [Fact]
    public void OnWritesNop76OverTheJmpAndOffWritesItBack() {
        byte[] vanilla = Fixture.Block();
        ScriptCuts cuts = ScriptCuts.Scan(vanilla, "997", Fixture.Catalog());
        byte[] image = (byte[])vanilla.Clone();

        Assert.Equal(new ToggleResult(2, null), cuts.Toggle(image, "Plain", on: true));
        Assert.Equal([Fixture.PlainAt0(vanilla), Fixture.PlainAt1(vanilla)], Differences(vanilla, image));
        Assert.All(Differences(vanilla, image), i => Assert.Equal(DeadJumps.NopByte, image[i]));
        Assert.Equal(2, cuts.CountOn(image, "Plain"));
        Assert.Equal(0, cuts.Toggle(image, "Plain", on: true).Changed);    // already on

        Assert.Equal(1, cuts.Toggle(image, "WithElse", on: true).Changed);
        Assert.Equal(DeadJumps.JmpByte, image[Fixture.LiveAt(vanilla)]);    // the live jmp over the same code stays

        Assert.Equal(2, cuts.Toggle(image, "Plain", on: false).Changed);
        Assert.Equal(1, cuts.Toggle(image, "WithElse", on: null).Changed);    // as loaded: off
        Assert.Equal(vanilla, image);
        Assert.Equal(new ToggleResult(0, null), cuts.Toggle(image, "NotHere", on: true));
    }

    [Fact]
    public void ABlockALinkedModSwitchedOnIsFoundAndAsLoadedKeepsItOn() {
        byte[] vanilla = Fixture.Block();
        byte[] linked = (byte[])vanilla.Clone();
        linked[Fixture.ElseAt(vanilla)] = DeadJumps.NopByte;    // what the linker writes for EnableCut(WithElse)
        ScriptCuts cuts = ScriptCuts.Scan(linked, "997", Fixture.Catalog());
        Assert.True(Assert.Single(cuts.Sites["WithElse"]).LoadedOn);
        Assert.False(cuts.Sites["Plain"][0].LoadedOn);

        byte[] image = (byte[])linked.Clone();
        Assert.Equal(1, cuts.Toggle(image, "WithElse", on: false).Changed);
        Assert.Equal(vanilla, image);
        Assert.Equal(1, cuts.Toggle(image, "WithElse", on: null).Changed);
        Assert.Equal(linked, image);
    }

    [Fact]
    public void UnexpectedBytesAreLeftAloneAndReported() {
        byte[] vanilla = Fixture.Block();
        ScriptCuts cuts = ScriptCuts.Scan(vanilla, "997", Fixture.Catalog());
        byte[] image = (byte[])vanilla.Clone();
        image[Fixture.PlainAt1(vanilla) + 1] ^= 0x40;    // the second block's operand is not its label any more
        byte[] before = (byte[])image.Clone();
        ToggleResult r = cuts.Toggle(image, "Plain", on: true);
        Assert.Equal(0, r.Changed);
        Assert.Contains("worker 1", r.Problem, StringComparison.Ordinal);
        Assert.Equal(before, image);    // not even the first block: a name is all on or untouched

        image = (byte[])vanilla.Clone();
        image[Fixture.ElseAt(vanilla)] = 0x00;
        Assert.Contains("expected b0 or f6", cuts.Toggle(image, "WithElse", on: true).Problem, StringComparison.Ordinal);
        Assert.NotNull(cuts.Toggle(new byte[8], "WithElse", on: true).Problem);    // shorter than the site
    }

    [Fact]
    public void AHeaderThatDoesNotFitIsNoBlock() {
        byte[] block = Fixture.Block();
        Assert.NotNull(BlockHeader.Read(block));
        Assert.Null(BlockHeader.Read(block.AsSpan(0, 0x20)));
        byte[] bad = (byte[])block.Clone();
        bad[0x10] = 0; bad[0x11] = 0; bad[0x12] = 0; bad[0x13] = 0;    // length 0
        Assert.Null(BlockHeader.Read(bad));
        Assert.Null(ScriptCuts.NameOf(bad));
        Assert.Null(ScriptCuts.NameOf(new byte[0x100]));
    }

    [Fact]
    public void TheSwitchesRoundTripThroughTheStateFile() {
        CutStates states = new();
        states.Set("Plain", true);
        states.Set("WithElse", false);
        states.Set("WithElse", null);
        states.Set("Other", false);
        Assert.Equal(4, states.Version);
        List<string> dropped = [];
        CutStates back = CutStates.FromJson(states.ToJson(), new HashSet<string> { "Plain", "WithElse" }, dropped);
        Assert.True(back.Get("Plain"));
        Assert.Null(back.Get("WithElse"));
        Assert.Equal(["Other"], dropped);
        Assert.Empty(CutStates.FromJson("", new HashSet<string>(), dropped).All);
        Assert.Throws<JsonException>(() => CutStates.FromJson("{\"cuts\": {\"Plain\": 1}}", new HashSet<string> { "Plain" }, dropped));
        Assert.Throws<JsonException>(() => CutStates.FromJson("[]", new HashSet<string>(), dropped));
        Assert.ThrowsAny<JsonException>(() => CutStates.FromJson("{", new HashSet<string>(), dropped));
    }
}
