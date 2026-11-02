// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary><c>op_77</c>/<c>op_78</c>: wait until a worker has no request for an entry queued or running.</summary>
public sealed class WaitForEntryTests {
    [Fact]
    public void TheFormsCompileToTheOpcodesAndTheDecompilerWritesThemBack() {
        const string source = """
            using Ffx;
            using Ffx.Atel;

            namespace Monsters.M996;

            [Monster("m996")]
            [Workers(typeof(Lulu), typeof(Wakka))]
            public static partial class M996
            {
            }

            public sealed class Lulu : Worker2
            {
                protected override void Init()
                {
                    return;
                }

                protected override void Main()
                {
                    return;
                }

                [Entry(2)]
                public static async Task E2Async()
                {
                    return;
                }
            }

            public sealed class Wakka : Worker2
            {
                protected override void Init()
                {
                    _ = Lulu.E2Async();
                    WaitForEntry<Lulu>(2);
                    WaitForEntry(0, 2);
                    Op77(0, 2);
                    WaitForPartyEntry(1, 2);
                    return;
                }
            }
            """;
        SyntaxTree tree = ScriptCompiler.Parse(source, "m996.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        CompiledScript compiled = ScriptCompiler.Compile([tree]);
        byte[] blockBytes = compiled.Block();
        ScriptBlock block = ScriptBlock.Parse(blockBytes);
        string text = string.Join("\n", block.Decode(block.Workers[1]).Select(i => Bytecode.Script.Assembly.Render(block, 1, i)));
        Assert.Equal(3, text.Split("pushimm 0\npushimm 2\nop_77").Length - 1);
        Assert.Contains("pushimm 1\npushimm 2\nop_78", text, StringComparison.Ordinal);

        byte[] file = new byte[0x40 + (blockBytes.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        blockBytes.CopyTo(file, 0x40);
        ScriptFile parsed = ScriptFile.Parse(file);
        DecompiledScript d = ScriptDecompiler.Decompile("ffx_ps2/ffx/master/jppc/battle/mon/_m996/m996.bin", parsed);
        Assert.Equal(3, d.Source.Split("WaitForEntry<W00>(2);").Length - 1);
        Assert.Contains("WaitForPartyEntry(1, 2);", d.Source, StringComparison.Ordinal);
        SyntaxTree back = ScriptCompiler.Parse(d.Source, "m996.cs");
        Assert.Empty(ScriptCompiler.Check([back]));
        Assert.Equal(file, ScriptCompiler.Compile([back]).ApplyTo(parsed).ToArray());
    }
}
