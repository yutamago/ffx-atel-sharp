// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;
using static Ffx.Atel.Compiler.Tests.NewCodeTests;

namespace Ffx.Atel.Compiler.Tests;

/// <summary><c>Text.New("…")</c> in a mod (docs/plan.md, "Text"): a message the package carries, whose index the linker gives.</summary>
public sealed class TextNewTests {
    private static CompiledMod Compile(string body) {
        SyntaxTree vanilla = ScriptCompiler.Parse(Vanilla, "Monsters/M996/M996.cs");
        SyntaxTree mod = ScriptCompiler.Parse(Replaced(body), "mod/Mod.cs");
        Assert.Empty(ScriptCompiler.Check([vanilla, mod]));
        return ScriptCompiler.CompileMod([vanilla], [mod]);
    }

    [Fact]
    public void ANewMessageIsInThePackageAndTheLinkerGivesItsIndex() {
        CompiledMod mod = Compile("""
                    Common.MsgSetText(1, Text.New("Nice throw!"));
                    Common.MsgSetText(2, Text.New("Again{{!}}"));
                    Common.MsgSetText(3, Text.New("Nice throw!"));
            """);
        ScriptPackage p = mod.Package;
        // Each text once, without an index; the code refers to them by their place in the list.
        Assert.Equal([new PackageMessage(null, "Nice throw!"), new PackageMessage(null, "Again{{!}}")], p.Messages);
        Assert.Equal([0u, 1u, 0u], p.Patches[0].Code.Relocs.Where(r => r.Kind == RelocKind.NewMessage).Select(r => r.Value));

        // Linked with the tables ending at 40: messages 40 and 41.
        p.File = NewCodeTests.Path;
        LinkResult r = ScriptLinker.Link(VanillaFile(), [new ModScript("test", ScriptPackage.FromJson(p.ToJson()), "en")], firstMessage: 40);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        Assert.Equal([(40, "Nice throw!"), (41, "Again{{!}}")], r.NewMessages.Select(m => (m.Index, m.Text)));
        MiniVm vm = new(ScriptImage.FromBlock(ScriptFile.Parse(r.File).Script));
        vm.Run(0, 1);
        Assert.Equal([(1, 40), (2, 41), (3, 40)], vm.Calls.Where(c => c.Id == 0x0064).Select(c => (c.Args[0].AsInt, c.Args[1].AsInt)));
    }

    [Theory]
    [InlineData("Common.MsgSetText(1, Text.New(\"\"));", "has text")]
    [InlineData("Common.MsgSetText(1, Text.New(\"a\" + \"b\"));", "one string literal")]
    public void TextNewTakesOneLiteral(string statement, string message) {
        SyntaxTree vanilla = ScriptCompiler.Parse(Vanilla, "Monsters/M996/M996.cs");
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.CompileMod([vanilla], [ScriptCompiler.Parse(Replaced(statement), "mod/Mod.cs")]));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScriptCompiledWholeHasNoTextNew() {
        string source = Vanilla.Replace("Hp = 1;", "Common.MsgSetText(1, Text.New(\"Hi\"));", StringComparison.Ordinal);
        Assert.NotEqual(Vanilla, source);
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.Compile(source, "M996.cs"));
        Assert.Contains("Text.New is for mods", ex.Message, StringComparison.Ordinal);
    }
}
