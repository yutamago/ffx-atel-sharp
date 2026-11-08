// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary><c>[Inject]</c>: code at a statement inside a vanilla method, compiled, linked and run, without the game.</summary>
public sealed class InjectTests {
    private const string Path = "ffx_ps2/ffx/master/jppc/battle/mon/_m997/m997.bin";

    private const string Vanilla = """
        using Ffx;
        using Ffx.Atel;
        using static Monsters.M997.M997;

        namespace Monsters.M997;

        [Monster("m997")]
        [Workers(typeof(Larva))]
        [Header(Author = "test", Name = "997")]
        [SharedData(0x40, "")]
        [OtherData(0x40, "")]
        [EventData(0x10, "")]
        public static partial class M997
        {
            [Var(Storage.Shared, 0x0000)] public static int Hp;
            [Var(Storage.Shared, 0x0004)] public static int Mp;
        }

        public sealed partial class Larva : Worker2
        {
            protected override void Init()
            {
                Hp = 5;
                while (Hp < Common.Random16())
                {
                    Hp = Hp + 1;
                }
                Btl.SetActorProperty(ActorProperty.Hp, Hp);
                return;
            }

            protected override void Main()
            {
                if (Hp == 3)
                {
                    Btl.SetActorProperty(ActorProperty.Hp, 1);
                }
                Btl.SetActorProperty(ActorProperty.Mp, 2);
                Mp = Mp + 100;
                return;
            }
        }
        """;

    private static string Mod(string larva, string members = "public static int Before; public static int After;") => $$"""
        using Ffx;
        using Ffx.Atel;
        using static Monsters.M997.M997;

        namespace Monsters.M997;

        public static partial class M997
        {
            {{members}}
        }

        public sealed partial class Larva
        {
            {{larva}}
        }
        """;

    private static byte[] VanillaFile() {
        byte[] block = ScriptCompiler.Compile(Vanilla, "M997.cs").Block();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    private static ScriptPackage Compile(string mod) {
        CompiledMod m = ScriptCompiler.CompileMod([ScriptCompiler.Parse(Vanilla, "Monsters/M997/M997.cs")], [ScriptCompiler.Parse(mod, "mod/Larva.cs")]);
        m.Package.File = Path;
        return ScriptPackage.FromJson(m.Package.ToJson());
    }

    private static (LinkResult Result, ScriptImage Image) Link(params ScriptPackage[] packages) {
        LinkResult r = ScriptLinker.Link(VanillaFile(), [.. packages.Select((p, i) => new ModScript($"mod{i + 1}", p))]);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        return (r, ScriptImage.FromBlock(ScriptFile.Parse(r.File).Script));
    }

    /// <summary>Random16 gives 8: the loop runs from 5 to 8.</summary>
    private static MiniVm Vm(ScriptImage image) => new(image, (id, args) => MiniVm.Value.Int(8));

    [Fact]
    public void TheModIsCSharp() {
        string mod = Mod("""
            [Inject(nameof(Init), Before = nameof(Common.Random16))]
            void B() { Before = Before + 1; }
            """);
        Assert.Empty(ScriptCompiler.Check([ScriptCompiler.Parse(Vanilla, "M997.cs"), ScriptCompiler.Parse(mod, "Larva.cs")]));
    }

    [Fact]
    public void TheAnchorIsTheStatementWithTheCall() {
        ScriptPackage p = Compile(Mod("""
            [Inject(nameof(Main), Before = "Btl.SetActorProperty", Occurrence = 2)]
            void B() { Before = 1; }
            """));
        EntryPatch inject = Assert.Single(p.Patches);
        Assert.Equal(PatchKind.Inject, inject.Kind);
        Assert.Equal("Main", inject.Target);
        Assert.Equal(InjectPosition.Before, inject.At!.Position);
        Assert.Equal("before the statement with the 2nd call to Btl.SetActorProperty in Main", inject.At.Anchor);
        Assert.Equal(ScriptPackage.DataFormat, p.Format);
        // The statement `Btl.SetActorProperty(ActorProperty.Mp, 2);`: its code is the pushes and the call.
        Assert.True(inject.At.End > inject.At.Start);
    }

    /// <summary>
    ///     Before a <c>while</c>: once, not on each turn of the loop (its back edge stays inside the statement). After it:
    ///     once, when the loop's exit jump leaves it.
    /// </summary>
    [Fact]
    public void AroundALoopTheCodeRunsOnce() {
        ScriptPackage p = Compile(Mod("""
            [Inject(nameof(Init), Before = nameof(Common.Random16))]
            void B() { Before = Before + 1; }

            [Inject(nameof(Init), After = nameof(Common.Random16))]
            void A() { After = After + 10 + Hp; }
            """));
        (_, ScriptImage image) = Link(p);
        int before = image.Variables.Count - 2, after = image.Variables.Count - 1;
        MiniVm vm = Vm(image);
        vm.Run(0, 0);
        Assert.Equal(8, vm.Var(0).AsInt);
        Assert.Equal(1, vm.Var(before).AsInt);
        Assert.Equal(18, vm.Var(after).AsInt);
        Assert.Equal(5, vm.Calls.Count);    // Random16 four times, SetActorProperty once
    }

    /// <summary>Before a statement an <c>if</c> jumps to from outside: the code runs on both paths, once.</summary>
    [Fact]
    public void BeforeAStatementThatIsAJumpTarget() {
        ScriptPackage p = Compile(Mod("""
            [Inject(nameof(Main), Before = nameof(Btl.SetActorProperty), Occurrence = 2)]
            void B() { Before = Before + 1; }

            [Inject(nameof(Main), After = nameof(Btl.SetActorProperty), Occurrence = 2)]
            void A() { After = Mp; }
            """));
        (_, ScriptImage image) = Link(p);
        int before = image.Variables.Count - 2, after = image.Variables.Count - 1;
        foreach (int hp in new[] { 3, 4 }) {
            MiniVm vm = Vm(image);
            vm.SetVar(0, MiniVm.Value.Int(hp));
            vm.Run(0, 1);
            Assert.Equal(1, vm.Var(before).AsInt);
            Assert.Equal(0, vm.Var(after).AsInt);    // after the call's statement, before Mp += 100
            Assert.Equal(100, vm.Var(1).AsInt);
            Assert.Equal(hp == 3 ? 2 : 1, vm.Calls.Count);
        }
    }

    /// <summary>Two mods' injects at one point run in load order; prefixes and postfixes of the same method still chain around them.</summary>
    [Fact]
    public void InjectsCombineWithEachOtherAndWithPrefixes() {
        ScriptPackage a = Compile(Mod("""
            [Inject(nameof(Main), Before = "Btl.SetActorProperty", Occurrence = 2)]
            void B() { Before = 1; }

            [Prefix(nameof(Main))]
            void P() { After = 5; }
            """));
        ScriptPackage b = Compile(Mod("""
            [Inject(nameof(Main), Before = "Btl.SetActorProperty", Occurrence = 2)]
            void B2() { Before = Before * 10; After = After + 1; }

            [Postfix(nameof(Main))]
            void Q() { After = After * 2; }
            """));
        (LinkResult r, ScriptImage image) = Link(a, b);
        // Each package's new variables: a's Before and After, then b's.
        int count = image.Variables.Count;
        (int aBefore, int aAfter, int bBefore, int bAfter) = (count - 4, count - 3, count - 2, count - 1);
        MiniVm vm = Vm(image);
        vm.Run(0, 1);
        Assert.Equal(1, vm.Var(aBefore).AsInt);
        Assert.Equal(5, vm.Var(aAfter).AsInt);
        Assert.Equal(0, vm.Var(bBefore).AsInt);    // b's own Before: 0 * 10
        Assert.Equal(2, vm.Var(bAfter).AsInt);     // b's inject, then b's postfix
        Assert.Contains(r.Messages, m => m.Severity == LinkSeverity.Info && m.Text.Contains("injects at one point run in load order: mod1's B, mod2's B2", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Messages, m => m.Severity is LinkSeverity.Conflict or LinkSeverity.Error or LinkSeverity.Warning);
    }

    /// <summary>A replacement takes the vanilla code an inject points into: the inject is a reported conflict and not applied.</summary>
    [Fact]
    public void AReplacementWinsOverAnInject() {
        ScriptPackage a = Compile(Mod("""
            [Inject(nameof(Main), Before = "Btl.SetActorProperty", Occurrence = 2)]
            void B() { Before = 1; }
            """));
        ScriptPackage b = Compile(Mod("""
            [Replace(nameof(Main))]
            void R() { Mp = 7; }
            """, ""));
        (LinkResult r, ScriptImage image) = Link(a, b);
        LinkMessage conflict = Assert.Single(r.Messages, m => m.Severity == LinkSeverity.Conflict);
        Assert.Equal("mod1", conflict.Mod);
        Assert.Contains("is not applied: mod2 replaces the method", conflict.Text, StringComparison.Ordinal);
        MiniVm vm = Vm(image);
        vm.Run(0, 1);
        Assert.Equal(7, vm.Var(1).AsInt);
        Assert.Equal(0, vm.Var(image.Variables.Count - 2).AsInt);
    }

    /// <summary>The linked script decompiles, the injected statements where they were put, and compiles back byte for byte.</summary>
    [Fact]
    public void TheLinkedScriptDecompilesAndRoundTrips() {
        ScriptPackage p = Compile(Mod("""
            [Inject(nameof(Main), Before = "Btl.SetActorProperty", Occurrence = 2)]
            void B() { Before = 4321; }
            """));
        (LinkResult r, _) = Link(p);
        ScriptFile linked = ScriptFile.Parse(r.File);
        DecompiledScript d = ScriptDecompiler.Decompile(Path, linked);
        List<SyntaxTree> trees = [.. d.Files().Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath))];
        Assert.Empty(ScriptCompiler.Check(trees));
        Assert.Equal(r.File, ScriptCompiler.Compile(trees).ApplyTo(linked).ToArray());
        // In Main, now the linked copy (the vanilla code stays behind Init as bytes no entry starts).
        int main = d.Source.IndexOf("void Main()", StringComparison.Ordinal);
        int inject = d.Source.IndexOf("= 4321;", StringComparison.Ordinal);
        int call = d.Source.IndexOf("ActorProperty.Mp, 2)", main, StringComparison.Ordinal);
        Assert.True(main >= 0 && inject > main && call > inject, d.Source);
    }

    [Theory]
    [InlineData("[Inject(nameof(Main), Before = nameof(Common.Random16))]", "Main has no call to Common.Random16")]
    [InlineData("[Inject(nameof(Main), Before = \"Btl.SetActorProperty\", Occurrence = 3)]", "Main has 2 call(s) to Btl.SetActorProperty, not 3")]
    [InlineData("[Inject(nameof(Main))]", "Before = … or After = …")]
    [InlineData("[Inject(nameof(Main), Before = \"Btl.SetActorProperty\", After = \"Btl.SetActorProperty\")]", "Before = … or After = …")]
    [InlineData("[Inject(\"Nope\", Before = \"Btl.SetActorProperty\")]", "no entry method Nope")]
    [InlineData("[Inject(nameof(Main), Before = \"Btl.SetActorProperty\", Occurrence = 0)]", "Occurrence counts from 1")]
    public void AnAnchorNamesACallInTheMethod(string attribute, string message) {
        CompileException ex = Assert.Throws<CompileException>(() => Compile(Mod($"{attribute} void B() {{ Before = 1; }}")));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The Larva (<c>m209</c>), as the in-game check does it (examples/LarvaInject): after the statement with the 3rd
    ///     call to <c>Btl.UseCommand</c> in <c>OnTurn</c> (its Thundara at more than half HP), its HP drops to 1. The
    ///     linked script decompiles with the new statement there and compiles back byte for byte. Needs <c>FFX_GAME_DIR</c>.
    /// </summary>
    [Fact]
    public void AnInjectIntoTheLarvaLinksOntoTheGamesFile() {
        string? dir = Bytecode.GameData.GameDir();
        if (dir is null) Assert.Skip($"{Bytecode.GameData.GameDirVariable} is not set");
        const string larva = "ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin";
        byte[] original;
        using (Bytecode.Vbf.VbfArchive archive = Bytecode.GameData.OpenArchive(dir!)) original = archive.Read(larva);
        ScriptFile vanilla = ScriptFile.Parse(original);
        List<SyntaxTree> source = [.. ScriptDecompiler.Decompile(larva, vanilla).Files().Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath))];
        SyntaxTree mod = ScriptCompiler.Parse("""
            using Ffx;
            using Ffx.Atel;

            namespace Monsters.M209;

            public sealed partial class Ai
            {
                [Inject(nameof(OnTurn), After = nameof(Btl.UseCommand), Occurrence = 3)]
                void Exhausted()
                {
                    Btl.SetActorProperty(ActorProperty.Hp, 1);
                }
            }
            """, "mod/Ai.cs");
        Assert.Empty(ScriptCompiler.Check([.. source, mod]));
        CompiledMod compiled = ScriptCompiler.CompileMod(source, [mod]);
        compiled.Package.File = larva;
        LinkResult r = ScriptLinker.Link(original, [new ModScript("larva-inject", compiled.Package)]);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        Assert.Contains(r.Messages, m => m.Text.Contains("inject Exhausted, after the statement with the 3rd call to Btl.UseCommand in OnTurn", StringComparison.Ordinal));

        ScriptFile linked = ScriptFile.Parse(r.File);
        ScriptImage before = ScriptImage.FromBlock(vanilla.Script), after = ScriptImage.FromBlock(linked.Script);
        foreach (int w in new[] { 0, 2 }) Assert.Equal(before.Workers[w].Code, after.Workers[w].Code);
        DecompiledScript d = ScriptDecompiler.Decompile(larva, linked);
        List<SyntaxTree> trees = [.. d.Files().Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath))];
        Assert.Empty(ScriptCompiler.Check(trees));
        Assert.Equal(r.File, ScriptCompiler.Compile(trees).ApplyTo(linked).ToArray());
        // In the new OnTurn: the last Thundara, then the injected statement.
        string ai = d.Files().Single(f => f.RelativePath.EndsWith("Ai.cs", StringComparison.Ordinal)).Text;
        int onTurn = ai.IndexOf("void OnTurn()", StringComparison.Ordinal);
        int body = ai.IndexOf("Btl.SetActorProperty(ActorProperty.Hp, 1);", onTurn, StringComparison.Ordinal);
        Assert.True(onTurn >= 0 && body > onTurn, ai);
        string between = ai[onTurn..body];
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(between, @"Btl\.UseCommand\("));
    }
}
