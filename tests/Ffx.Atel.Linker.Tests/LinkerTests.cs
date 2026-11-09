// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Linker.Tests;

/// <summary>Linking hand-made packages onto a made-up script, without the game.</summary>
public sealed class LinkerTests {
    // ----- The vanilla script -----------------------------------------------------------------

    /// <summary>Shared variable v0 (i32 at shared 0); the "other" area points at the shared data, as in the shipped scripts.</summary>
    private const uint V0 = 5u << 28 | 4u << 25;

    private static readonly string[] VanillaCode = [
        // Worker 0 (type 2): e0 ends; e1 sets v0 and calls the function worker.
        """
        e0:
            end
        e1:
            pushimm 5
            popv v0
            call w2
            end
        """,
        // Worker 1 (type 2): e0 is the "Init" the patches target; e1 waits.
        """
        e0:
            pushv v0
            popa_jz l0
            pushimm 1
            popv v0
            end
        l0:
            pushimm 2
            popv v0
            end
        e1:
            end
        """,
        // Worker 2 (type 0): a function worker.
        """
        e0:
            ret
        """,
    ];

    private static readonly ushort[] VanillaTypes = [2, 2, 0];

    internal static byte[] VanillaFile() {
        ScriptImage image = new() {
            Author = "test"u8.ToArray(), Name = "998"u8.ToArray(),
            Shared = new byte[0x40], Other = new byte[0x40], Event = new byte[0x10],
        };
        image.Variables.Add(new VarEntry(V0, 1, 0));
        image.IntConstants.Add(0x12345);
        image.FloatConstants.Add(BitConverter.SingleToUInt32Bits(1.5f));
        for (int w = 0; w < VanillaCode.Length; w++) {
            WorkerAssembler a = new();
            a.Add(VanillaCode[w]);
            AssembledWorker done = a.Finish();
            image.Workers.Add(new WorkerImage { Type = VanillaTypes[w], Code = done.Code, Entries = done.Entries, Labels = done.Labels });
        }
        // The "other" area holds a block offset of the shared data; the linker must not move it.
        byte[] probe = image.Write();
        int shared = (int)BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(BinaryPrimitives.ReadInt32LittleEndian(probe.AsSpan(0x38)) + 0x30));
        BinaryPrimitives.WriteUInt32LittleEndian(image.Other.AsSpan(0x0c), (uint)shared);
        byte[] block = image.Write();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40 + 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)(file.Length - 0x40));
        block.CopyTo(file, 0x40);
        file[^1] = 0xab;    // the next section
        return file;
    }

    private static ScriptImage Image(byte[] file) => ScriptImage.FromBlock(ScriptFile.Parse(file).Script);

    // ----- Packages ---------------------------------------------------------------------------

    /// <summary>
    ///     Assembles a fragment: the syntax of <c>atel disasm</c>, plus <c>@kind:value</c> for an operand the
    ///     linker fills in (<c>pushc @int:70000</c>, <c>pushv @newvar:0</c>); jumps to <c>lN</c> are the fragment's labels.
    /// </summary>
    internal static Fragment Frag(string asm) {
        List<byte> code = [];
        Dictionary<int, uint> labels = [];
        List<Reloc> relocs = [];
        foreach (string raw in asm.Split('\n')) {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.EndsWith(':')) {
                labels[int.Parse(line[1..^1], CultureInfo.InvariantCulture)] = (uint)code.Count;
                continue;
            }
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Opcode op = Opcodes.ByName(parts[0]) ?? throw new ArgumentException(line);
            if (parts.Length > 1 && parts[1].StartsWith('@')) {
                string[] kv = parts[1][1..].Split(':');
                RelocKind kind = Enum.Parse<RelocKind>(kv[0], ignoreCase: true);
                relocs.Add(new Reloc(code.Count, kind, kind == RelocKind.Float
                    ? BitConverter.SingleToUInt32Bits(float.Parse(kv[1], CultureInfo.InvariantCulture))
                    : (uint)long.Parse(kv[1], CultureInfo.InvariantCulture)));
                new Instr(0, op.Code, 0).Encode(code);
            }
            else if (op.Operand == OperandKind.Label) {
                relocs.Add(new Reloc(code.Count, RelocKind.Label, uint.Parse(parts[1][1..], CultureInfo.InvariantCulture)));
                new Instr(0, op.Code, 0).Encode(code);
            }
            else {
                new Instr(0, op.Code, parts.Length > 1 ? Assembly.ParseOperand(parts[1]) : null).Encode(code);
            }
        }
        uint[] table = new uint[labels.Count == 0 ? 0 : labels.Keys.Max() + 1];
        foreach ((int n, uint at) in labels) table[n] = at;
        return new Fragment { Bytes = [.. code], Labels = table, Relocs = relocs };
    }

    /// <summary>A package against <see cref="VanillaFile"/>, attached to worker 1 and its entry 0 ("Init").</summary>
    internal static ScriptPackage Package(byte[] vanillaFile, params EntryPatch[] patches) {
        ScriptImage v = Image(vanillaFile);
        WorkerImage w1 = v.Workers[1];
        ScriptPackage p = new() { File = "ffx_ps2/test/m998.bin", Script = "m998" };
        p.Vanilla.Workers.Add(new WorkerRef(1, "W01", VanillaHash.Worker(w1)));
        p.Vanilla.Workers.Add(new WorkerRef(2, "W02", VanillaHash.Worker(v.Workers[2])));
        p.Vanilla.Entries.Add(new EntryRef(1, "Init", [0], w1.Entries[0], w1.Entries[1], VanillaHash.Range(w1, w1.Entries[0], w1.Entries[1])));
        p.Vanilla.Variables.Add(new VariableRef(0, "V0", VanillaHash.Variable(v.Variables[0])));
        p.Patches.AddRange(patches);
        return p;
    }

    private static EntryPatch Patch(PatchKind kind, string name, string asm) =>
        new() { Kind = kind, Worker = 1, Name = name, Target = kind == PatchKind.Entry ? null : "Init", Code = Frag(asm) };

    private static string Worker(byte[] file, int w) {
        StringBuilder sb = new();
        Assembly.DisassembleWorker(sb, ScriptFile.Parse(file).Script, w);
        return sb.ToString().Replace("\r", "");
    }

    private static string Code(string text) => string.Join('\n', text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0)) + "\n";

    private static LinkResult Link(byte[] vanilla, params ScriptPackage[] packages) =>
        ScriptLinker.Link(vanilla, [.. packages.Select((p, i) => new ModScript($"mod{(char)('a' + i)}", p))]);

    /// <summary>Links, and fails with the report when a package did not apply.</summary>
    private static LinkResult LinkAll(byte[] vanilla, params ScriptPackage[] packages) {
        LinkResult r = Link(vanilla, packages);
        Assert.True(r.Applied.Count == packages.Length, string.Join("\n", r.Messages));
        return r;
    }

    /// <summary>What every linked file must keep: the vanilla block's bytes outside its header fields and tables, the data areas at their offsets, the other sections.</summary>
    private static void AssertVanillaKept(byte[] vanilla, byte[] linked, params int[] unchangedWorkers) {
        ScriptImage v = Image(vanilla), l = Image(linked);
        Assert.Equal(v.Shared, l.Shared);
        Assert.Equal(v.Other, l.Other);
        Assert.Equal(v.Event, l.Event);
        ScriptBlock vb = ScriptFile.Parse(vanilla).Script, lb = ScriptFile.Parse(linked).Script;
        int vh0 = vb.Workers[0].Header, lh0 = lb.Workers[0].Header;
        Assert.Equal(vb.U32(vh0 + 0x30), lb.U32(lh0 + 0x30));
        Assert.Equal(vb.U32(0x2c), lb.U32(0x2c));
        Assert.Equal(vb.U32(0x20), lb.U32(0x20));
        foreach (int w in unchangedWorkers) {
            Assert.Equal(v.Workers[w].Code, l.Workers[w].Code);
            Assert.Equal(v.Workers[w].Entries, l.Workers[w].Entries);
            Assert.Equal(v.Workers[w].Labels, l.Workers[w].Labels);
        }
        // The section after the script is the vanilla one.
        Assert.Equal(0xab, linked[^1]);
        // The linked block takes itself apart and lays itself out again byte for byte.
        ScriptImage again = ScriptImage.FromBlock(lb);
        Assert.Equal(lb.Bytes[..(int)lb.U32(0x10)].ToArray(), again.Write());
    }

    // ----- Tests ------------------------------------------------------------------------------

    [Fact]
    public void WithoutPackagesTheFileIsVanilla() {
        byte[] vanilla = VanillaFile();
        LinkResult r = ScriptLinker.Link(vanilla, []);
        Assert.False(r.Changed);
        Assert.Same(vanilla, r.File);
    }

    [Fact]
    public void ANewEntryGetsTheNextNumberAndItsOperands() {
        byte[] vanilla = VanillaFile();
        ScriptPackage p = Package(vanilla,
            Patch(PatchKind.Entry, "Boost", """
                pushc @int:70000
                pushfc @float:2.5
                popv @var:0
                pushc @int:74565
                pushimm @newentry:0
                pushimm @worker:2
                req0
                popfacc
                end
                """));
        LinkResult r = LinkAll(vanilla, p);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        Assert.Equal(Code("""
            worker 1 type 2
            e0:
                pushv v0
                popa_jz l0
                pushimm 1
                popv v0
                end
            l0:
                pushimm 2
                popv v0
                end
            e1:
                end
            e2:
                pushc c1        ; 70000
                pushfc f1        ; 2.5
                popv v0
                pushc c0        ; 74565
                pushimm 2
                pushimm 2
                req0
                popfacc
                end
            """), Code(Worker(r.File, 1)));
        AssertVanillaKept(vanilla, r.File, 0, 2);
    }

    [Fact]
    public void APrefixRunsFirstThenTheOriginal() {
        byte[] vanilla = VanillaFile();
        LinkResult r = LinkAll(vanilla, Package(vanilla, Patch(PatchKind.Prefix, "Before", """
            pushimm 7
            popv @var:0
            """)));
        Assert.Equal(Code("""
            worker 1 type 2
            e0:
                jmp l3
                popa_jz l0
                pushimm 1
                popv v0
                end
            l0:
                pushimm 2
                popv v0
                end
            e1:
                end
            l3:
                pushimm 7
                popv v0
                pushv v0
                popa_jz l2
                pushimm 1
                popv v0
                jmp l1
            l2:
                pushimm 2
                popv v0
                jmp l1
            l1:
                end
            """), Code(Worker(r.File, 1)));
        AssertVanillaKept(vanilla, r.File, 0, 2);
    }

    [Fact]
    public void APostfixRunsWhereverTheOriginalEnds() {
        byte[] vanilla = VanillaFile();
        LinkResult r = LinkAll(vanilla, Package(vanilla, Patch(PatchKind.Postfix, "After", """
            pushimm 9
            popv @var:0
            """)));
        // The original's code is copied; each `end` jumps to the postfix, its own jump goes to the copy.
        // Init starts the worker's code, so it keeps its entry, and its first instruction jumps to the copy (l3).
        Assert.Equal(Code("""
            worker 1 type 2
            e0:
                jmp l3
                popa_jz l0
                pushimm 1
                popv v0
                end
            l0:
                pushimm 2
                popv v0
                end
            e1:
                end
            l3:
                pushv v0
                popa_jz l2
                pushimm 1
                popv v0
                jmp l1
            l2:
                pushimm 2
                popv v0
                jmp l1
            l1:
                pushimm 9
                popv v0
                end
            """), Code(Worker(r.File, 1)));
        AssertVanillaKept(vanilla, r.File, 0, 2);
    }

    [Fact]
    public void PrefixesAndPostfixesOfSeveralModsChainInLoadOrder() {
        byte[] vanilla = VanillaFile();
        LinkResult r = LinkAll(vanilla,
            Package(vanilla, Patch(PatchKind.Prefix, "A1", "pushimm 1\npopi0"), Patch(PatchKind.Postfix, "A2", "pushimm 2\npopi0")),
            Package(vanilla, Patch(PatchKind.Prefix, "B1", "pushimm 3\npopi0"), Patch(PatchKind.Postfix, "B2", "pushimm 4\npopi0")));
        Assert.Equal(["moda", "modb"], r.Applied);
        string text = Code(Worker(r.File, 1));
        // e0 jumps to the chain (Init starts the worker's code, so it keeps its entry: the trampoline).
        Assert.StartsWith("worker 1 type 2\ne0:\n    jmp l3\n", text, StringComparison.Ordinal);
        int from = text.IndexOf("l3:", StringComparison.Ordinal);
        string chain = text[from..(text.IndexOf("    end\n", from, StringComparison.Ordinal) + "    end\n".Length)];
        Assert.Equal(Code("""
            l3:
                pushimm 1
                popi0
                pushimm 3
                popi0
                pushv v0
                popa_jz l2
                pushimm 1
                popv v0
                jmp l1
            l2:
                pushimm 2
                popv v0
                jmp l1
            l1:
                pushimm 2
                popi0
                pushimm 4
                popi0
                end
            """), chain);
    }

    [Fact]
    public void UnchangedCodeStaysInPlaceAndNewCodeIsATail() {
        byte[] vanilla = VanillaFile();
        ScriptPackage p = Package(vanilla, Patch(PatchKind.Postfix, "After", "pushimm 9\npopv @var:0"));
        p.Workers.Add(new NewWorker { Name = "Helper", Type = 0, Code = Frag("nop\nret"), Entries = [1] });
        LinkResult r = LinkAll(vanilla, p);
        ScriptBlock vb = ScriptFile.Parse(vanilla).Script, lb = ScriptFile.Parse(r.File).Script;
        // The code region is the vanilla one, at the same offset and length, byte for byte but for worker 1's first
        // instruction (pushv, 3 bytes): Init starts the worker's code, so it keeps its entry and jumps to its new code.
        Assert.Equal(vb.CodeStart, lb.CodeStart);
        Assert.Equal(vb.CodeLength, lb.CodeLength);
        byte[] was = vb.Bytes.Slice(vb.CodeStart, vb.CodeLength).ToArray(), now = lb.Bytes.Slice(lb.CodeStart, lb.CodeLength).ToArray();
        int head = (int)vb.Workers[1].Start;
        Assert.Equal(Opcodes.Jmp | 0x80, now[head]);
        Array.Copy(was, head, now, head, 3);
        Assert.Equal(was, now);
        // Worker 1 keeps its range and gets a tail past the code length; the new function worker (3, after the vanilla
        // one) has only a tail, which starts at a label (its entry is at 1).
        Bytecode.Script.Worker w1 = lb.Workers[1], helper = lb.Workers[3];
        Assert.Equal((vb.Workers[1].Start, vb.Workers[1].End), (w1.Start, w1.End));
        Assert.True(w1.HasTail && w1.TailStart >= lb.CodeLength);
        Assert.True(helper.HasTail && helper.Start == helper.End && helper.TailStart == w1.TailEnd);
        Assert.Contains(helper.TailStart, helper.Labels);
        Assert.False(lb.Workers[0].HasTail || lb.Workers[2].HasTail);
        AssertVanillaKept(vanilla, r.File, 0, 2);

        // The earlier layout lays the whole code region out again, and the file grows by more.
        LinkResult region = ScriptLinker.Link(vanilla, [new ModScript("moda", p)], layout: CodeLayout.Region);
        Assert.True(region.Changed, string.Join("\n", region.Messages));
        ScriptBlock rb = ScriptFile.Parse(region.File).Script;
        Assert.True(rb.CodeStart >= vb.U32(0x10));
        Assert.DoesNotContain(rb.Workers, w => w.HasTail);
        Assert.Equal(Worker(r.File, 0), Worker(region.File, 0));
        Assert.True(ScriptBlockSize(region.File) > ScriptBlockSize(r.File));
        AssertVanillaKept(vanilla, region.File, 0, 2);
    }

    private static int ScriptBlockSize(byte[] file) => (int)ScriptFile.Parse(file).Script.U32(0x10);

    [Fact]
    public void TwoReplacementsConflictAndTheLastWins() {
        byte[] vanilla = VanillaFile();
        LinkResult r = LinkAll(vanilla,
            Package(vanilla, Patch(PatchKind.Replace, "Mine", "pushimm 1\npopi0\nend")),
            Package(vanilla, Patch(PatchKind.Replace, "Theirs", "pushimm 2\npopi0\nend")));
        LinkMessage conflict = Assert.Single(r.Messages, m => m.Severity == LinkSeverity.Conflict);
        Assert.Equal("modb", conflict.Mod);
        Assert.Contains("moda, modb", conflict.Text, StringComparison.Ordinal);
        Assert.StartsWith(Code("""
            worker 1 type 2
            e0:
                jmp l1
                popa_jz l0
                pushimm 1
                popv v0
                end
            l0:
                pushimm 2
                popv v0
                end
            e1:
                end
            l1:
                pushimm 2
                popi0
                end
            """), Code(Worker(r.File, 1)), StringComparison.Ordinal);
    }

    [Fact]
    public void AReplacementWithAPostfixEndsInThePostfix() {
        byte[] vanilla = VanillaFile();
        LinkResult r = LinkAll(vanilla,
            Package(vanilla, Patch(PatchKind.Replace, "Mine", "pushimm 1\npopi0\nend")),
            Package(vanilla, Patch(PatchKind.Postfix, "After", "pushimm 2\npopi0")));
        // The replacement is placed, then copied with its `end` jumping to the postfix.
        Assert.StartsWith(Code("""
            worker 1 type 2
            e0:
                jmp l2
                popa_jz l0
                pushimm 1
                popv v0
                end
            l0:
                pushimm 2
                popv v0
                end
            e1:
                end
            l2:
                pushimm 1
                popi0
                end
                pushimm 1
                popi0
                jmp l1
            l1:
                pushimm 2
                popi0
                end
            """), Code(Worker(r.File, 1)), StringComparison.Ordinal);
    }

    [Fact]
    public void AHashMismatchKeepsThePackageOut() {
        byte[] vanilla = VanillaFile();
        ScriptPackage p = Package(vanilla, Patch(PatchKind.Prefix, "Before", "nop"));
        p.Vanilla.Entries[0] = p.Vanilla.Entries[0] with { Hash = "0000000000000000" };
        LinkResult r = Link(vanilla, p);
        Assert.False(r.Changed);
        Assert.Same(vanilla, r.File);
        LinkMessage error = Assert.Single(r.Messages);
        Assert.Equal(LinkSeverity.Error, error.Severity);
        Assert.Contains("hash mismatch", error.Text, StringComparison.Ordinal);

        // Another mod's package for the same script still applies.
        LinkResult both = Link(vanilla, p, Package(vanilla, Patch(PatchKind.Prefix, "Other", "nop")));
        Assert.Equal(["modb"], both.Applied);
    }

    [Fact]
    public void ANewWorkerGoesBeforeTheFunctionWorkersAndCallsFollow() {
        byte[] vanilla = VanillaFile();
        ScriptPackage p = Package(vanilla, Patch(PatchKind.Entry, "Ask", """
            pushimm 1
            pushimm @newworker:0
            pushimm 0
            req0
            popfacc
            call @worker:2
            end
            """));
        p.Workers.Add(new NewWorker {
            Name = "Helper", Type = 2,
            Code = Frag("""
                pushimm 3
                popv @newvar:0
                end
                l0:
                pushv @newvar:0
                popa_jnz l0
                end
                """),
            Entries = [0, 7],
        });
        p.Variables.Add(new NewVariable { Name = "Count", Type = 5 });
        LinkResult r = LinkAll(vanilla, p);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        ScriptBlock b = ScriptFile.Parse(r.File).Script;
        Assert.Equal([2, 2, 2, 0], b.Workers.Select(w => (int)w.Type));
        Assert.Equal(3, b.U16(0x36));
        // Worker 0's call to the function worker (now worker 3) follows it.
        Assert.Contains("call w3", Worker(r.File, 0), StringComparison.Ordinal);
        Assert.Equal(Code("""
            worker 2 type 2
            e0:
                pushimm 3
                popv v1
                end
            e1:
            l0:
                pushv v1
                popa_jnz l0
                end
            """), Code(Worker(r.File, 2)));
        Assert.EndsWith(Code("""
            e2:
                pushimm 1
                pushimm 2
                pushimm 0
                req0
                popfacc
                call w3
                end
            """), Code(Worker(r.File, 1)), StringComparison.Ordinal);
        // The new variable lives past the vanilla block, in reach of the shared data's base.
        ScriptImage l = Image(r.File);
        VarEntry count = l.Variables[1];
        Assert.Equal(4, count.Storage);
        Assert.Equal(5, count.Type);
        int shared = (int)b.U32(b.Workers[0].Header + 0x30);
        Assert.True(shared + count.Offset >= ScriptFile.Parse(vanilla).Script.U32(0x10));
        Assert.True(shared + count.Offset + 4 <= b.U32(0x10));
        AssertVanillaKept(vanilla, r.File);
    }

    [Fact]
    public void ManyNewWorkersMoveTheFirstHeaders() {
        byte[] vanilla = VanillaFile();
        ScriptPackage p = Package(vanilla);
        for (int i = 0; i < 20; i++) p.Workers.Add(new NewWorker { Name = $"N{i}", Type = 1, Code = Frag($"pushimm {i}\npopi0\nend"), Entries = [0] });
        LinkResult r = LinkAll(vanilla, p);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        ScriptBlock b = ScriptFile.Parse(r.File).Script;
        Assert.Equal(23, b.Workers.Count);
        Assert.Equal(0, b.Workers[^1].Type);
        Assert.Contains("pushimm 19", Worker(r.File, 21), StringComparison.Ordinal);
        AssertVanillaKept(vanilla, r.File, 1);
    }

    [Fact]
    public void ABrokenPackageIsRefused() {
        byte[] vanilla = VanillaFile();
        ScriptPackage p = Package(vanilla, Patch(PatchKind.Entry, "Bad", "pushv @var:5\nend"));
        LinkResult r = Link(vanilla, p);
        Assert.False(r.Changed);
        Assert.Contains(r.Messages, m => m.Severity == LinkSeverity.Error && m.Text.Contains("refers to nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void APackageSurvivesJson() {
        byte[] vanilla = VanillaFile();
        ScriptPackage p = Package(vanilla, Patch(PatchKind.Postfix, "After", "pushc @int:70000\npopv @var:0"));
        p.Variables.Add(new NewVariable { Name = "Count", Type = 5 });
        string json = p.ToJson();
        Assert.Contains("\"kind\": \"postfix\"", json, StringComparison.Ordinal);
        ScriptPackage back = ScriptPackage.FromJson(json);
        Assert.Equal(json, back.ToJson());
        Assert.Equal(Link(vanilla, p).File, Link(vanilla, back).File);
    }
}
