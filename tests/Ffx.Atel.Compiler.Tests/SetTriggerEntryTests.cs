// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary><c>op_79</c>: sets the entry a kind of trigger requests of the worker.</summary>
public sealed class SetTriggerEntryTests {
    [Fact]
    public void BothNamesCompileToTheOpcodeAndTheDecompilerWritesTheNewOne() {
        const string source = """
            using Ffx;
            using Ffx.Atel;
            using static Monsters.M996.M996;

            namespace Monsters.M996;

            [Monster("m996")]
            [Workers(typeof(Lulu))]
            public static partial class M996
            {
                [Var(Storage.Private, 0x0000)] public static ushort[] V0 = new ushort[8];
            }

            public sealed class Lulu : Worker2
            {
                protected override void Init()
                {
                    SetTriggerEntry(Addr(ref V0[0]), 0, 6);
                    Op79(Addr(ref V0[0]), 3, 7);
                    return;
                }

                protected override void Main()
                {
                    return;
                }
            }
            """;
        SyntaxTree tree = ScriptCompiler.Parse(source, "m996.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        byte[] blockBytes = ScriptCompiler.Compile([tree]).Block();
        ScriptBlock block = ScriptBlock.Parse(blockBytes);
        string text = string.Join("\n", block.Decode(block.Workers[0]).Select(i => Bytecode.Script.Assembly.Render(block, 0, i)));
        Assert.Contains("pushimm 0\npushimm 6\nop_79", text, StringComparison.Ordinal);
        Assert.Contains("pushimm 3\npushimm 7\nop_79", text, StringComparison.Ordinal);

        byte[] file = new byte[0x40 + (blockBytes.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        blockBytes.CopyTo(file, 0x40);
        ScriptFile parsed = ScriptFile.Parse(file);
        DecompiledScript d = ScriptDecompiler.Decompile("ffx_ps2/ffx/master/jppc/battle/mon/_m996/m996.bin", parsed);
        Assert.Contains(", 0, 6);", d.Source, StringComparison.Ordinal);
        Assert.Equal(2, d.Source.Split("SetTriggerEntry(Addr(ref ").Length - 1);
        Assert.DoesNotContain("Op79(", d.Source, StringComparison.Ordinal);
        SyntaxTree back = ScriptCompiler.Parse(d.Source, "m996.cs");
        Assert.Empty(ScriptCompiler.Check([back]));
        Assert.Equal(file, ScriptCompiler.Compile([back]).ApplyTo(parsed).ToArray());
    }
}
