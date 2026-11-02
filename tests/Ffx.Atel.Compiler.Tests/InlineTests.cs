// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;
using static Ffx.Atel.Compiler.Tests.NewCodeTests;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>
///     Inline methods (docs/encoding.md, "Inline methods"): a call compiles to exactly the statements it
///     stands for, labels and pools included, and the decompiler writes the call where they are.
/// </summary>
public sealed class InlineTests {
    // The lines of Ffx.Dialogue written out, as the game's scripts have them.
    private const string Written = """
                while (Hp != 3)
                {
                    Common.WaitFrames(1);
                }
                Common.MsgSetPos(0, 256, 448, 4);
                Common.MsgSetStyle(0, 1);
                Common.MsgSetText(0, Text[12]);
                Common.MsgSetFlags(0, 12290);
                Common.MsgOpen(0, 0);
                while (true)
                {
                    if (Common.MsgIsReady(0) == 1)
                    {
                        break;
                    }
                    else
                    {
                        Common.WaitFrames(1);
                    }
                }
                Common.MsgWaitEnd(0);
                Common.MsgClose(0);
                Common.MsgWaitExEnd(0, 1);
                Common.MsgSetPos(1, Table[Hp + 1] + 20, 448, 4);
                Common.MsgSetStyle(1, 1);
                Common.MsgSetText(1, Text[3]);
                Common.MsgSetFlags(1, 8194);
                Common.MsgOpen(1, 0);
                while (Common.MsgGetState(1) != 2)
                {
                    Common.WaitFrames(1);
                }
                Common.VoiceStartEnd();
                while (Common.VoiceIsPlaying())
                {
                    if (Common.MsgIsReady(1) == 1 & AsBool(Common.PadPressed() & 32) & Common.AlwaysTrue019c())
                    {
                        Common.VoiceStop();
                        break;
                    }
                    Common.WaitFrames(1);
                }
                Common.SoundWaitPoll();
                Common.MsgClose(1);
                Common.MsgWaitExEnd(1, 1);
                Common.MsgSetPos(2, 256, (int)359f, 4);
                Common.MsgSetStyle(2, 1);
                Common.MsgSetText(2, Text[4]);
                Common.MsgSetFlags(2, 65536);
                Common.MsgOpen(2, 0);
                if (Hp == 2)
                {
                    Common.MsgSetPos(0, 256, 448, 3);
                    Common.MsgSetStyle(0, 1);
                    Common.MsgSetText(0, Text[5]);
                    Common.MsgSetFlags(0, 12290);
                    Common.MsgOpen(0, 0);
                    while (true)
                    {
                        if (Common.MsgIsReady(0) == 1)
                        {
                            break;
                        }
                        else
                        {
                            Common.WaitFrames(1);
                        }
                    }
                    Common.MsgWaitEnd(0);
                    Common.MsgClose(0);
                    Common.MsgWaitExEnd(0, 1);
                }
                Common.WaitFrames(Hp);
                Common.WaitFrames(Hp);
        """;

    private const string Calls = """
                while (Hp != 3)
                {
                    Common.WaitFrames(1);
                }
                Dialogue.Say(Text[12]);
                Dialogue.SayVoiced(Text[3], window: 1, x: Table[Hp + 1] + 20);
                Dialogue.Open(Text[4], 2, y: (int)359f, flags: 0x10000);
                if (Hp == 2)
                {
                    Dialogue.Say(Text[5], anchor: 3);
                }
                Twice(Hp);
        """;

    // An inline method of the script's own: a worker's method with [Inline].
    private const string Twice = """
            [Inline(Waits = true)]
            static void Twice(int frames)
            {
                Common.WaitFrames(frames);
                Common.WaitFrames(frames);
            }
        """;

    private static string Script(string body, string members = "") =>
        Vanilla.Replace("Hp = 1;", body, StringComparison.Ordinal).Replace("[Register(3)] public static int I3;", "[Register(3)] public static int I3;\n" + members, StringComparison.Ordinal);

    private static byte[] Block(string body, string members = "") {
        SyntaxTree tree = ScriptCompiler.Parse(Script(body, members), "M996.cs");
        Assert.DoesNotContain(ScriptCompiler.Check([tree]), d => d.Severity == DiagnosticSeverity.Error);
        return ScriptCompiler.Compile([tree]).Block();
    }

    private static byte[] File(byte[] block) {
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    [Fact]
    public void ACallCompilesToTheStatementsItStandsFor() {
        byte[] written = Block(Written);
        Assert.Equal(written, Block(Calls, Twice));
        // The labels of the loops inside the calls are numbered as the written-out loops: the block has
        // the same label table, and a script with a pool constant in a call has it in the same place.
        Assert.Equal(Block(Written.Replace("65536", "65537", StringComparison.Ordinal)), Block(Calls.Replace("0x10000", "65537", StringComparison.Ordinal), Twice));
    }

    [Fact]
    public void TheDecompilerWritesTheCallsAndTheyCompileBack() {
        byte[] block = Block(Written);
        DecompiledScript d = ScriptDecompiler.Decompile(NewCodeTests.Path, ScriptFile.Parse(File(block)));
        string source = d.Source;
        Assert.Contains("Dialogue.Say(Text[12]);", source, StringComparison.Ordinal);
        Assert.Contains("Dialogue.SayVoiced(Text[3], 1, V2[V0 + 1] + 20);", source, StringComparison.Ordinal);
        Assert.Contains("Dialogue.Open(Text[4], 2, y: (int)359f, flags: 0x10000);", source, StringComparison.Ordinal);
        Assert.Contains("Dialogue.Say(Text[5], anchor: 3);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MsgSetPos", source, StringComparison.Ordinal);
        Assert.Equal(4, d.Stats.Inline["dialogue-lines"]);
        Assert.Equal(4, d.Stats.Inline["dialogue-one-call"]);
        Assert.Equal(block, ScriptCompiler.Compile([.. d.Files().Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath))]).Block());

        // Without inline methods, the code as it is.
        string plain = ScriptDecompiler.Decompile(NewCodeTests.Path, ScriptFile.Parse(File(block)), inline: []).Source;
        Assert.DoesNotContain("Dialogue.", plain, StringComparison.Ordinal);
        Assert.True(plain.Length > source.Length);
    }

    [Fact]
    public void DeadCodeKeepsItsLines() {
        string dead = "if (false)\n{\n" + Written[(Written.IndexOf("Common.MsgSetPos(0", StringComparison.Ordinal))..Written.IndexOf("Common.MsgSetPos(1", StringComparison.Ordinal)] + "}\n";
        byte[] block = Block(dead);
        DecompiledScript d = ScriptDecompiler.Decompile(NewCodeTests.Path, ScriptFile.Parse(File(block)));
        Assert.DoesNotContain("Dialogue.", d.Source, StringComparison.Ordinal);
        Assert.Equal(1, d.Stats.Inline["dialogue-dead"]);
    }

    [Fact]
    public void AModSaysANewLine() {
        SyntaxTree vanilla = ScriptCompiler.Parse(Vanilla, "Monsters/M996/M996.cs");
        SyntaxTree mod = ScriptCompiler.Parse(Replaced("""
                    Dialogue.Open(Text.New("Nice throw!"));
            """), "mod/Mod.cs");
        Assert.Empty(ScriptCompiler.Check([vanilla, mod]));
        ScriptPackage p = ScriptCompiler.CompileMod([vanilla], [mod]).Package;
        p.File = NewCodeTests.Path;
        LinkResult r = ScriptLinker.Link(VanillaFile(), [new ModScript("test", ScriptPackage.FromJson(p.ToJson()), "en")], firstMessage: 40);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        MiniVm vm = new(ScriptImage.FromBlock(ScriptFile.Parse(r.File).Script));
        vm.Run(0, 1);
        Assert.Equal([(0, 40)], vm.Calls.Where(c => c.Id == 0x0064).Select(c => (c.Args[0].AsInt, c.Args[1].AsInt)));
    }

    [Theory]
    [InlineData("Dialogue.Say(Text[1], window: Common.Random16());", "", "is used where its parameter is")]
    [InlineData("Dialogue.Say(Text[1], window: Hp++);", "", "is used where its parameter is")]
    [InlineData("Dialogue.Say(Text[1], nope: 1);", "", "has no parameter nope")]
    [InlineData("Hp = 1;", "[Inline] static void A() { B(); }\n[Inline] static void B() { A(); }\nstatic void C() { A(); }", "calls itself")]
    [InlineData("Hp = 1;", "[Inline] static void W() { Common.WaitFrames(1); }\nstatic void C() { W(); }", "mark it [Inline(Waits = true)]")]
    [InlineData("Hp = 1;", "[Inline] static void S() { Hp = 2; }\nstatic void C() { S(); }", "is not a variable")]
    public void WhatAnInlineMethodCannotDo(string body, string members, string message) {
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.Compile([ScriptCompiler.Parse(Script(body, members), "M996.cs")]));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }
}
