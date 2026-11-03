// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Reflection;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Layout;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>Dead <c>if (false)</c> blocks: the catalogue's facts, and names for them (<c>if (Cut.…)</c>; docs/research/dead-code.md).</summary>
public sealed class DeadCodeTests {
    private const string Path = "ffx_ps2/ffx/master/jppc/battle/mon/_m996/m996.bin";

    private static string Source(string body) => $$"""
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
            void E0()
            {
                {{body}}
            }
        }
        """;

    private static byte[] Block(string body) {
        SyntaxTree tree = ScriptCompiler.Parse(Source(body), "m999.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        return ScriptCompiler.Compile([tree]).Block();
    }

    [Fact]
    public void ACutNameCompilesAsFalse() {
        Assert.Equal(Block("if (false) { Count = 8; } else { Count = 2; } return;"),
            Block("if (Cut.HighroadBattleLine335Tidus) { Count = 8; } else { Count = 2; } return;"));
        Assert.Equal(Block("if (false) { Count = 8; } return;"), Block("if (Ffx.Cut.HighroadBattleLine335Tidus) { Count = 8; } return;"));
    }

    [Theory]
    [InlineData("if (Cut.NoSuchBlock) { Count = 8; }", "not a constant of Ffx.Cut")]
    [InlineData("while (Cut.HighroadBattleLine335Tidus) { Count = 8; }", "only the whole condition of an `if`")]
    [InlineData("if (Cut.HighroadBattleLine335Tidus & Count == 1) { Count = 8; }", "only the whole condition of an `if`")]
    public void ACutNameIsOnlyAKnownIfCondition(string body, string message) {
        SyntaxTree tree = ScriptCompiler.Parse(Source(body + " return;"), "m999.cs");
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.Compile([tree]));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A monster file of one worker: <c>Count = 8;</c> live, the same dead, and other dead code.</summary>
    private static byte[] File() {
        ScriptImage image = new() {
            Header14 = 3, Header18 = 0xffff, Header24 = 0x8000_0001,
            Author = "test"u8.ToArray(), Name = "996"u8.ToArray(), Shared = new byte[0x20], Other = new byte[0x40], Event = new byte[0x10],
        };
        image.Variables.Add(new VarEntry(0x5600_0000, 1, 0));    // i32, private, 0x00
        WorkerAssembler w0 = new();
        w0.PlaceEntry(0);
        w0.Add("""
                pushimm 8
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
                end
            """);
        AssembledWorker a0 = w0.Finish();
        image.Workers.Add(new WorkerImage { Type = 2, Code = a0.Code, Entries = a0.Entries, Labels = a0.Labels });
        byte[] block = image.Write();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    [Fact]
    public void DeadBlocksAreListedWithTheirLiveTwins() {
        ScriptFile file = ScriptFile.Parse(File());
        DecompiledScript d = ScriptDecompiler.Decompile(Path, file, deadBlocks: true, cuts: CutNames.Empty);
        Assert.Equal(2, d.DeadBlocks.Count);
        DeadBlock copy = d.DeadBlocks[0], variant = d.DeadBlocks[1];
        // `Count = 8;` stands live just before: a twin. `Count = 5;` runs nowhere.
        Assert.False(copy.HasElse);
        Assert.Equal(1, copy.LiveTwins);
        Assert.Equal(2, copy.Instructions);
        Assert.Contains("= 8;", copy.Code, StringComparison.Ordinal);
        Assert.True(variant.HasElse);
        Assert.Equal(0, variant.LiveTwins);
        Assert.Contains("= 5;", variant.Code, StringComparison.Ordinal);
        Assert.Contains("= 2;", variant.ElseCode, StringComparison.Ordinal);
        Assert.Equal([0], copy.Entries);
        Assert.NotEqual(copy.Signature, variant.Signature);
        Assert.Equal(DeadSignature.Length, copy.Signature.Length);
        // Without the list, a decompile carries none.
        Assert.Empty(ScriptDecompiler.Decompile(Path, file).DeadBlocks);
    }

    [Fact]
    public void ANamedDeadBlockIsWrittenWithItsNameAndCompilesBack() {
        byte[] original = File();
        ScriptFile file = ScriptFile.Parse(original);
        string signature = ScriptDecompiler.Decompile(Path, file, deadBlocks: true, cuts: CutNames.Empty).DeadBlocks[1].Signature;
        CutNames cuts = CutNames.Parse($"[cuts]\n\"{signature}\" = \"HighroadBattleLine335Tidus\"\n", "test.toml");
        DecompiledScript d = ScriptDecompiler.Decompile(Path, file, cuts: cuts, deadBlocks: true);
        Assert.Contains("if (Cut.HighroadBattleLine335Tidus)", d.Source, StringComparison.Ordinal);
        Assert.Contains("if (false)", d.Source, StringComparison.Ordinal);    // the other block keeps false
        Assert.Equal("HighroadBattleLine335Tidus", d.DeadBlocks[1].Name);
        Assert.Equal(1, d.Stats.Cuts["HighroadBattleLine335Tidus"]);
        Assert.Equal([signature], d.Stats.CutSignatures);

        SyntaxTree tree = ScriptCompiler.Parse(d.Source, "m996.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        Assert.Equal(original, ScriptCompiler.Compile([tree]).ApplyTo(file).ToArray());
    }

    [Theory]
    [InlineData("[cuts]\n\"0123\" = \"A\"\n", "16 lower-case hex digits")]
    [InlineData("[cuts]\n\"0123456789abcdef\" = \"not a name\"\n", "not a C# name")]
    [InlineData("[cuts]\n\"0123456789abcdef\" = \"A\"\n\"0123456789abcdef\" = \"B\"\n", "named twice")]
    [InlineData("\"0123456789abcdef\" = \"A\"\n", "before any section")]
    [InlineData("[classes]\n", "unknown section")]
    public void ABadCutNamesFileStops(string text, string message) {
        FormatException ex = Assert.Throws<FormatException>(() => CutNames.Parse(text, "test.toml"));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>data/cut-names.toml and Ffx.Cut name the same blocks: every name is a constant false, every constant has a block.</summary>
    [Fact]
    public void TheEmbeddedCutNamesAreTheConstantsOfCut() {
        HashSet<string> constants = [.. typeof(Cut).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.GetRawConstantValue() is false).Select(f => f.Name)];
        Assert.NotEmpty(CutNames.Default.Names);
        Assert.Equal(constants.Order(StringComparer.Ordinal), CutNames.Default.Names.Values.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(constants.Count, typeof(Cut).GetFields(BindingFlags.Public | BindingFlags.Static).Length);
    }
}
