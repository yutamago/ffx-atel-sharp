// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>Decompiling and compiling without the game: a made-up script and hand-written source.</summary>
public sealed class SourceTests {
    private const string MonsterPath = "ffx_ps2/ffx/master/jppc/battle/mon/_m999/m999.bin";

    /// <summary>A small monster file with one of everything the source has to carry.</summary>
    private static byte[] MadeUpFile() {
        ScriptImage image = new() {
            Header14 = 3, Header18 = 0xffff, Header24 = 0x8000_0001,
            Author = "test"u8.ToArray(), Name = "999"u8.ToArray(),
            Shared = new byte[0x40], Other = new byte[0x40], Event = new byte[0x10],
        };
        image.Shared[5] = 7;
        image.Other[0x3f] = 0xff;
        image.Variables.Add(new VarEntry(0x5600_0010, 1, 0));    // i32, private, 0x10
        image.Variables.Add(new VarEntry(0x0900_0004, 4, 2));    // u8[4], shared, 0x04, bit 24, extra 2
        image.IntConstants.AddRange([0x8000_0000, 12345]);
        image.FloatConstants.AddRange([BitConverter.SingleToUInt32Bits(35f), 0x7fc0_0001]);    // a NaN payload

        WorkerAssembler w0 = new();
        w0.PlaceEntry(0);
        w0.Add("end");
        AssembledWorker a0 = w0.Finish();
        image.Workers.Add(new WorkerImage { Type = 0, Code = a0.Code, Entries = a0.Entries, Labels = a0.Labels });

        WorkerAssembler w1 = new();
        w1.PlaceEntry(0);
        w1.PlaceEntry(2);    // entries 0 and 2 share code; entry 1 is inside what entry 0 runs into
        w1.Add("""
            nop
            pushc c0
        l0:
        e1:
            popa_jz l0
            pushfc f1
            end
        """);
        AssembledWorker a1 = w1.Finish();
        byte[] priv = new byte[16];
        priv[3] = 9;
        image.Workers.Add(new WorkerImage { Type = 2, PrivateLength = 0x10, Private = priv, Code = a1.Code, Entries = a1.Entries, Labels = a1.Labels });

        byte[] block = image.Write();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    [Fact]
    public void AMadeUpScriptRoundTrips() {
        byte[] original = MadeUpFile();
        ScriptFile file = ScriptFile.Parse(original);
        DecompiledScript d = ScriptDecompiler.Decompile(MonsterPath, file);
        Assert.Equal("Monsters.M999", d.Names.Namespace);
        Assert.Contains("[Entry(0)][Entry(2)]", d.Source, StringComparison.Ordinal);
        Assert.Contains("[FloatBits(", d.Source, StringComparison.Ordinal);
        Assert.Contains("public static byte[,] V1 = new byte[2, 2];", d.Source, StringComparison.Ordinal);

        SyntaxTree tree = ScriptCompiler.Parse(d.Source, "m999.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        CompiledScript compiled = ScriptCompiler.Compile([tree]);
        Assert.Equal((Bytecode.ScriptCategory.Monster, "m999"), (compiled.Category, compiled.Name));
        Assert.Equal(original, compiled.ApplyTo(file).ToArray());
    }

    /// <summary>A monster whose code is one of each statement Phase 3 writes as C#.</summary>
    private static byte[] StatementsFile() {
        ScriptImage image = new() {
            Author = "test"u8.ToArray(), Name = "998"u8.ToArray(), Shared = new byte[0x20], Other = new byte[0x40], Event = new byte[0x10],
        };
        image.Variables.Add(new VarEntry(0x5600_0010, 1, 0));                          // V0: int, private 0x10
        image.Variables.Add(new VarEntry(0x0800_0004 | VarEntry.ArrayBit, 4, 2));      // V1: byte[2, 2], shared 0x04
        image.Variables.Add(new VarEntry(0x1600_0014, 1, 0));                          // V2: sbyte, private 0x14
        image.IntConstants.Add(70000);
        image.FloatConstants.Add(BitConverter.SingleToUInt32Bits(2.5f));

        WorkerAssembler w0 = new();
        w0.PlaceEntry(0);
        w0.Add("ret");
        AssembledWorker a0 = w0.Finish();
        image.Workers.Add(new WorkerImage { Type = 0, Code = a0.Code, Entries = a0.Entries, Labels = a0.Labels });

        WorkerAssembler w1 = new();
        w1.PlaceEntry(0);
        w1.Add("""
                pushimm 5
                popv v0
                pushv v0
                pushimm 3
                add
                popv v0
                pushimm 3
                op_2b
                pushva v1
                pushimm -65
                and
                popva v1
                pushimm 300
                popv v2
                pushc c0
                popv v0
                pushfc f0
                func_facc 0x0016
                pushv v0
                pushimm 2
                gt
                pushv v2
                land
                popa_jz l0
                pushimm 1
                pushimm 0
                pushimm 0
                req0
                popfacc
                pushi1
                popf0
                call w0
            l0:
                end
            """);
        AssembledWorker a1 = w1.Finish();
        image.Workers.Add(new WorkerImage { Type = 2, Code = a1.Code, Entries = a1.Entries, Labels = a1.Labels });

        byte[] block = image.Write();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    [Fact]
    public void StatementsRoundTripAsCSharp() {
        byte[] original = StatementsFile();
        ScriptFile file = ScriptFile.Parse(original);
        DecompiledScript d = ScriptDecompiler.Decompile("ffx_ps2/ffx/master/jppc/battle/mon/_m998/m998.bin", file);
        string[] expected = [
            "V0 = 5;",
            "V0 += 3;",
            "V1[1, 1] &= unchecked((byte)-65);",
            "V2 = unchecked((sbyte)300);",
            "V0 = 0x11170;",
            "Common.MoveSetSpeed(2.5f);",
            "if (!(V0 > 2 & AsBool(V2))) goto l0;",
            "Request(1, 0, 0);",
            "F0 = I1;",
            "Call<W00>();",
            "l0:",
            "return;",
            "[Register(1)] public int I1;",
        ];
        foreach (string line in expected) Assert.Contains(line, d.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("Asm(", d.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("[Ints(", d.Source, StringComparison.Ordinal);    // the pools follow the rules
        Assert.Equal((12, 0), (d.Stats.Statements, d.Stats.AsmInstructions));

        SyntaxTree tree = ScriptCompiler.Parse(d.Source, "m998.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        Assert.Equal(original, ScriptCompiler.Compile([tree]).ApplyTo(file).ToArray());
    }

    [Fact]
    public void HandWrittenStatementsCompile() {
        string source = """
            using Ffx;
            using Ffx.Atel;
            using static Monsters.M999.M999;

            namespace Monsters.M999;

            [Monster("m999")]
            [Workers(typeof(W00))]
            public static partial class M999
            {
                [Var(Storage.Private, 0x0000)] public static int Count;
            }

            public sealed class W00 : Worker2
            {
                [Entry(0)]
                void Init()
                {
                    Count = (Count + 1) * 2;
                    if (Count == 4 | Count < 0) goto done;
                    Common.WaitFrames(Count & 3);
                done:
                    return;
                }
            }
            """;
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.Compile(source, "m999.cs"));
        Assert.Contains("labels are lN", ex.Message, StringComparison.Ordinal);
        CompiledScript compiled = ScriptCompiler.Compile(source.Replace("done", "l0", StringComparison.Ordinal), "m999.cs");
        List<Instr> code = ScriptBlock.Parse(compiled.Block()).Decode(ScriptBlock.Parse(compiled.Block()).Workers[0]);
        Assert.Equal(
            "pushv pushimm add pushimm mul popv pushv pushimm eq pushv pushimm lt lor popa_jnz pushv pushimm and func_facc end",
            string.Join(" ", code.Select(i => i.Opcode.Name)));
    }

    private const string BadSource = """"
        using Ffx.Atel;

        namespace Monsters.M999;

        [Monster("m999")]
        [Workers(typeof(W00))]
        public static partial class M999 { }

        sealed class W00 : Worker2
        {
            [Entry(0)]
            void E0()
            {
                Asm("""
                    nop
                    bogus 1
                    """);
            }
        }
        """";

    [Fact]
    public void AssemblyErrorsGiveTheSourceLine() {
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.Compile(BadSource, "m999.cs"));
        Assert.Equal("m999.cs(16): unknown instruction bogus", ex.Message);
    }

    [Fact]
    public void AnUnlistedWorkerIsAnError() {
        string source = BadSource.Replace("bogus 1", "end", StringComparison.Ordinal) + "\nsealed class W01 : Worker1 { }\n";
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.Compile(source, "m999.cs"));
        Assert.Contains("W01 is not in [Workers]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSourceIsCheckedAsCSharp() {
        string source = BadSource.Replace("void E0()", "void E0(Undefined x)", StringComparison.Ordinal);
        Assert.Contains(ScriptCompiler.Check([ScriptCompiler.Parse(source)]), d => d.Id == "CS0246");
    }

    [Theory]
    [InlineData(MonsterPath, "Monsters.M999", "M999")]
    [InlineData("ffx_ps2/ffx/master/jppc/event/obj/bj/bjyt0400/bjyt0400.ebp", "Events.Bj.Bjyt0400", "Bjyt0400")]
    [InlineData("ffx_ps2/ffx/master/jppc/event/obj/ka/kami0400/200thunder_kami0400.ebp", "Events.Ka._200thunder_kami0400", "_200thunder_kami0400")]
    [InlineData("ffx_ps2/ffx/master/jppc/battle/btl/cdsp00_00/cdsp00_00.bin", "Battles.Cdsp00_00", "Cdsp00_00")]
    public void ScriptsAreNamedAfterTheirPath(string path, string ns, string cls) {
        SourceNames names = SourceNames.ForPath(path);
        Assert.Equal((ns, cls), (names.Namespace, names.ClassName));
    }
}
