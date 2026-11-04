// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Linker.Tests;

/// <summary>
///     Switching named dead blocks on at link time (<see cref="ScriptPackage.Cuts"/>; docs/research/dead-code.md,
///     "Switching a block on"), on a made-up script.
/// </summary>
public sealed class CutTests {
    private const uint V0 = 5u << 28 | 4u << 25;

    /// <summary>
    ///     Worker 0: a dead block (<c>if (false) { V0 = 8; }</c>), a dead block with an else
    ///     (<c>if (false) { V0 = 5; } else { V0 = 2; }</c>), and a live <c>jmp</c> over the same code as the first
    ///     block, which a <c>popa_jz</c> reaches: not a dead block.
    /// </summary>
    private const string Code = """
        e0:
            pushimm 1
            popv v0
            jmp l0
            pushimm 8
            popv v0
        l0:
            jmp l1
            pushimm 5
            popv v0
            jmp l2
        l1:
            pushimm 2
            popv v0
        l2:
            pushv v0
            popa_jz l3
            pushimm 3
            popv v0
            jmp l4
        l3:
            pushimm 8
            popv v0
        l4:
            end
        e1:
            end
        """;

    private static byte[] VanillaFile() {
        ScriptImage image = new() {
            Author = "test"u8.ToArray(), Name = "997"u8.ToArray(),
            Shared = new byte[0x40], Other = new byte[0x40], Event = new byte[0x10],
        };
        image.Variables.Add(new VarEntry(V0, 1, 0));
        WorkerAssembler a = new();
        a.Add(Code);
        AssembledWorker done = a.Finish();
        image.Workers.Add(new WorkerImage { Type = 2, Code = done.Code, Entries = done.Entries, Labels = done.Labels });
        byte[] block = image.Write();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40 + 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)(file.Length - 0x40));
        block.CopyTo(file, 0x40);
        file[^1] = 0xab;
        return file;
    }

    /// <summary>The signature of instructions <paramref name="from"/> to <paramref name="to"/> of worker 0.</summary>
    private static string Signature(byte[] file, int from, int to) {
        ScriptBlock block = ScriptFile.Parse(file).Script;
        return DeadSignature.Of(block, 0, block.Decode(block.Workers[0]), from, to);
    }

    // Instructions 3-4 are the first block's code; 6-7 the code of the block with an else (without its jmp over the else).
    private static string Plain(byte[] file) => Signature(file, 3, 5);
    private static string WithElse(byte[] file) => Signature(file, 6, 8);

    /// <summary>The file offset of instruction <paramref name="i"/> of worker 0.</summary>
    private static int At(byte[] file, int i) {
        ScriptFile f = ScriptFile.Parse(file);
        return f.SectionStart + f.Script.CodeStart + (int)f.Script.Decode(f.Script.Workers[0])[i].Offset;
    }

    private static ScriptPackage Package(params CutSwitch[] cuts) =>
        new() { File = "ffx_ps2/test/m997.bin", Script = "m997", Format = ScriptPackage.CutsFormat, Cuts = [.. cuts] };

    private static LinkResult Link(byte[] vanilla, params ScriptPackage[] packages) =>
        ScriptLinker.Link(vanilla, [.. packages.Select((p, i) => new ModScript($"mod{(char)('a' + i)}", p))]);

    /// <summary>The offsets at which two files of the same length differ.</summary>
    private static List<int> Differences(byte[] a, byte[] b) {
        Assert.Equal(a.Length, b.Length);
        return [.. Enumerable.Range(0, a.Length).Where(i => a[i] != b[i])];
    }

    [Fact]
    public void TheFinderFindsOnlyDeadBlocks() {
        byte[] vanilla = VanillaFile();
        ScriptBlock block = ScriptFile.Parse(vanilla).Script;
        // The live jmp over `V0 = 8` (reached by popa_jz) has the same code as the first block, and is not found.
        DeadJump plain = Assert.Single(DeadJumps.Find(block, new HashSet<string> { Plain(vanilla) }));
        Assert.Equal(At(vanilla, 2) - 0x40 - block.CodeStart, (int)plain.Offset);
        Assert.Equal(0, plain.Label);
        DeadJump withElse = Assert.Single(DeadJumps.Find(block, new HashSet<string> { WithElse(vanilla) }));
        Assert.Equal(1, withElse.Label);
        Assert.Empty(DeadJumps.Find(block, new HashSet<string> { "0123456789abcdef" }));
    }

    [Fact]
    public void ASwitchedOnBlockChangesOnlyItsJmp() {
        byte[] vanilla = VanillaFile();
        LinkResult r = Link(vanilla, Package(new CutSwitch("Plain", [Plain(vanilla)], 1)));
        Assert.Equal(["moda"], r.Applied);
        int at = At(vanilla, 2);
        Assert.Equal([at], Differences(vanilla, r.File));
        Assert.Equal([DeadJumps.JmpByte, 0, 0], vanilla[at..(at + 3)]);
        Assert.Equal([DeadJumps.NopByte, 0, 0], r.File[at..(at + 3)]);
        Assert.Contains(r.Messages, m => m.Severity == LinkSeverity.Info && m.Text.Contains("Cut.Plain, 1 block(s)", StringComparison.Ordinal));
        // The block now runs: worker 0 decodes with nop_76 l0 where the jmp was.
        ScriptBlock linked = ScriptFile.Parse(r.File).Script;
        Assert.Equal(Opcodes.Nop76, linked.Decode(linked.Workers[0])[2].Code);
    }

    /// <summary>
    ///     The finder with <c>switchedOn</c> (the in-game switch, which reads files a mod may have linked): a block the
    ///     linker switched on is found at the same jmp, as on; without it, not at all. The live jmp stays unfound.
    /// </summary>
    [Fact]
    public void TheFinderFindsSwitchedOnBlocksWhenAsked() {
        byte[] vanilla = VanillaFile();
        LinkResult r = Link(vanilla, Package(new CutSwitch("Plain", [Plain(vanilla)], 1), new CutSwitch("WithElse", [WithElse(vanilla)], 1)));
        ScriptBlock linked = ScriptFile.Parse(r.File).Script;
        HashSet<string> both = [Plain(vanilla), WithElse(vanilla)];
        Assert.Empty(DeadJumps.Find(linked, both));
        List<DeadJump> found = DeadJumps.Find(linked, both, switchedOn: true);
        List<DeadJump> before = DeadJumps.Find(ScriptFile.Parse(vanilla).Script, both, switchedOn: true);
        Assert.Equal(before.Select(j => j with { On = true }), found);
        Assert.All(before, j => Assert.False(j.On));
        Assert.Equal([0, 1], found.Select(j => (int)j.Label));
    }

    [Fact]
    public void ABlockWithAnElseRunsAndSkipsTheElse() {
        byte[] vanilla = VanillaFile();
        LinkResult r = Link(vanilla, Package(new CutSwitch("WithElse", [WithElse(vanilla)], 1)));
        Assert.Equal([At(vanilla, 5)], Differences(vanilla, r.File));
        // Its own jmp over the else stays.
        Assert.Equal(DeadJumps.JmpByte, r.File[At(vanilla, 8)]);
    }

    [Fact]
    public void TwoModsSwitchingTheSameBlockOnAreFine() {
        byte[] vanilla = VanillaFile();
        CutSwitch cut = new("Plain", [Plain(vanilla)], 1);
        LinkResult once = Link(vanilla, Package(cut));
        LinkResult twice = Link(vanilla, Package(cut), Package(cut, new CutSwitch("WithElse", [WithElse(vanilla)], 1)));
        Assert.Equal(["moda", "modb"], twice.Applied);
        Assert.Equal([At(vanilla, 2), At(vanilla, 5)], Differences(vanilla, twice.File));
        Assert.Equal(once.File[At(vanilla, 2)], twice.File[At(vanilla, 2)]);
        Assert.Contains(twice.Messages, m => m.Mod == "modb" && m.Text.Contains("already on", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0123456789abcdef", 1, "found 0 of its 1 dead block(s)")]
    [InlineData(null, 2, "found 1 of its 2 dead block(s)")]
    [InlineData(null, 0, "no signatures or no blocks")]
    public void ACutThatIsNotThereIsAnError(string? signature, int blocks, string message) {
        byte[] vanilla = VanillaFile();
        LinkResult r = Link(vanilla, Package(new CutSwitch("Plain", [signature ?? Plain(vanilla)], blocks)));
        Assert.False(r.Changed);
        Assert.Same(vanilla, r.File);
        LinkMessage error = Assert.Single(r.Messages, m => m.Severity == LinkSeverity.Error);
        Assert.Contains(message, error.Text, StringComparison.Ordinal);
        Assert.Contains("not applied", error.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACutGoesWithAPatchOnTheSameEntry() {
        byte[] vanilla = VanillaFile();
        ScriptImage image = ScriptImage.FromBlock(ScriptFile.Parse(vanilla).Script);
        WorkerImage w0 = image.Workers[0];
        ScriptPackage p = Package(new CutSwitch("Plain", [Plain(vanilla)], 1));
        // The hashes are the vanilla code's, as the mod was built against it.
        p.Vanilla.Workers.Add(new WorkerRef(0, "W00", VanillaHash.Worker(w0)));
        p.Vanilla.Entries.Add(new EntryRef(0, "Init", [0], w0.Entries[0], w0.Entries[1], VanillaHash.Range(w0, w0.Entries[0], w0.Entries[1])));
        p.Patches.Add(new EntryPatch { Kind = PatchKind.Postfix, Worker = 0, Name = "After", Target = "Init", Code = LinkerTests.Frag("pushimm 9") });
        LinkResult r = Link(vanilla, p);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        // The code was laid out again; the copy the postfix runs after has the block on, and no jmp over it.
        ScriptBlock linked = ScriptFile.Parse(r.File).Script;
        List<Instr> code = linked.Decode(linked.Workers[0]);
        Assert.Contains(code, i => i.Code == Opcodes.Nop76);
    }

    [Fact]
    public void APackageWithCutsRoundTripsAsJson() {
        ScriptPackage p = Package(new CutSwitch("Plain", ["0123456789abcdef"], 3));
        ScriptPackage back = ScriptPackage.FromJson(p.ToJson());
        Assert.Equal(ScriptPackage.CutsFormat, back.Format);
        CutSwitch cut = Assert.Single(back.Cuts!);
        Assert.Equal(("Plain", 3), (cut.Name, cut.Blocks));
        Assert.Equal(["0123456789abcdef"], cut.Signatures);
        Assert.Contains("\"cuts\"", p.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"cuts\"", new ScriptPackage().ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void ANewerFormatIsRefused() {
        byte[] vanilla = VanillaFile();
        ScriptPackage p = Package(new CutSwitch("Plain", [Plain(vanilla)], 1));
        p.Format = ScriptPackage.CurrentFormat + 1;
        Assert.False(Link(vanilla, p).Changed);
    }
}
