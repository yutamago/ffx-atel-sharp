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
        Assert.Contains("public static byte[] V1 = new byte[4];", d.Source, StringComparison.Ordinal);

        SyntaxTree tree = ScriptCompiler.Parse(d.Source, "m999.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        CompiledScript compiled = ScriptCompiler.Compile([tree]);
        Assert.Equal((Bytecode.ScriptCategory.Monster, "m999"), (compiled.Category, compiled.Name));
        Assert.Equal(original, compiled.ApplyTo(file).ToArray());
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
