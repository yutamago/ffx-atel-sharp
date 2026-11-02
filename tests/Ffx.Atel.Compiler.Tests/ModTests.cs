// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>Compiling a mod against vanilla source and linking it, without the game.</summary>
public sealed class ModTests {
    private const string Path = "ffx_ps2/ffx/master/jppc/battle/mon/_m997/m997.bin";

    private const string Vanilla = """
        using Ffx;
        using Ffx.Atel;
        using static Monsters.M997.M997;

        namespace Monsters.M997;

        [Monster("m997")]
        [Workers(typeof(Larva), typeof(Fn))]
        [Header(Author = "test", Name = "997")]
        [SharedData(0x40, "")]
        [OtherData(0x40, "")]
        [EventData(0x10, "")]
        public static partial class M997
        {
            [Var(Storage.Shared, 0x0000)] public static int Hp;
        }

        public sealed partial class Larva : Worker2
        {
            protected override void Init()
            {
                Hp = 5;
                if (Hp == 3)
                {
                    Hp = 4;
                    return;
                }
                Hp = 6;
                return;
            }

            protected override void Main()
            {
                return;
            }
        }

        public sealed partial class Fn : Worker0
        {
            [Entry(0)]
            void E0()
            {
                return;
            }
        }
        """;

    private const string Mod = """
        using Ffx;
        using Ffx.Atel;
        using static Monsters.M997.M997;

        namespace Monsters.M997;

        public static partial class M997
        {
            public static int Turns;
        }

        public sealed partial class Larva
        {
            [Postfix(nameof(Init))]
            void MoreHp()
            {
                Hp = Hp + 1000;
                Turns = 70000;
            }

            public static async Task BoostAsync()
            {
                Hp = 1234;
            }

            [Prefix(nameof(Main))]
            void First()
            {
                Request(1, Link.Worker<Larva>(), Link.Entry<Larva>(nameof(BoostAsync)));
                Request(1, Link.Worker<Spark>(), 0);
            }
        }

        public sealed class Spark : Worker2
        {
            protected override void Init()
            {
                Turns = 1;
                return;
            }

            protected override void Main()
            {
                Call<Fn>();
                return;
            }
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

    private static CompiledMod CompileMod() =>
        ScriptCompiler.CompileMod([ScriptCompiler.Parse(Vanilla, "Monsters/M997/M997.cs")], [ScriptCompiler.Parse(Mod, "mod/Larva.cs")]);

    [Fact]
    public void TheModIsCSharp() {
        IReadOnlyList<Diagnostic> found = ScriptCompiler.Check([ScriptCompiler.Parse(Vanilla, "M997.cs"), ScriptCompiler.Parse(Mod, "Larva.cs")]);
        Assert.Empty(found);
    }

    /// <summary>A function worker as the decompiler writes it, <c>public static void Run()</c>, without <c>[Entry]</c>.</summary>
    [Fact]
    public void AFunctionWorkersRunIsItsEntry() {
        string vanilla = System.Text.RegularExpressions.Regex.Replace(Vanilla, @"\[Entry\(0\)\]\s*void E0\(\)", "public static void Run()");
        Assert.NotEqual(Vanilla, vanilla);
        CompiledMod mod = ScriptCompiler.CompileMod([ScriptCompiler.Parse(vanilla, "Monsters/M997/M997.cs")], [ScriptCompiler.Parse(Mod, "mod/Larva.cs")]);
        Assert.Equal(3, mod.Package.Patches.Count);
    }

    [Fact]
    public void ThePackageHoldsOnlyTheMod() {
        CompiledMod mod = CompileMod();
        Assert.Equal("m997", mod.Name);
        ScriptPackage p = mod.Package;
        Assert.Equal(["Turns"], p.Variables.Select(x => x.Name));
        Assert.Null(p.Variables[0].Offset);
        Assert.Equal(4, p.Variables[0].Storage);
        Assert.Equal(["Spark"], p.Workers.Select(w => w.Name));
        Assert.Equal([(PatchKind.Postfix, "MoreHp", "Init"), (PatchKind.Entry, "BoostAsync", null), (PatchKind.Prefix, "First", "Main")],
            p.Patches.Select(x => (x.Kind, x.Name, x.Target)));
        // Attached to Larva (0), with Init and Main, using Fn (1) and Hp (0).
        Assert.Equal([0, 1], p.Vanilla.Workers.Select(w => w.Index));
        Assert.Equal(["Init", "Main"], p.Vanilla.Entries.Select(e => e.Name).Order());
        Assert.Equal(["Hp"], p.Vanilla.Variables.Select(x => x.Name));
        // No vanilla code: the postfix is its own three statements.
        Fragment post = p.Patches[0].Code;
        Assert.Contains(post.Relocs, r => r.Kind == RelocKind.Int && r.Value == 70000);
        Assert.Contains(post.Relocs, r => r.Kind == RelocKind.NewVar && r.Value == 0);
        Assert.DoesNotContain(post.Bytes, b => b == Opcodes.End);
        Assert.Contains(p.Patches[2].Code.Relocs, r => r.Kind == RelocKind.NewEntry && r.Value == 1);
        Assert.Contains(p.Patches[2].Code.Relocs, r => r.Kind == RelocKind.NewWorker && r.Value == 0);
        Assert.Contains(p.Workers[0].Code.Relocs, r => r.Kind == RelocKind.Worker && r.Value == 1);
    }

    [Fact]
    public void TheLinkedScriptDecompilesAndRoundTrips() {
        byte[] vanilla = VanillaFile();
        CompiledMod mod = CompileMod();
        mod.Package.File = Path;
        LinkResult r = ScriptLinker.Link(vanilla, [new ModScript("test", ScriptPackage.FromJson(mod.Package.ToJson()))]);
        Assert.True(r.Changed, string.Join("\n", r.Messages));

        ScriptFile linked = ScriptFile.Parse(r.File);
        Assert.Equal([2, 2, 0], linked.Script.Workers.Select(w => (int)w.Type));
        Assert.Equal(3, linked.Script.Workers[0].Entries.Length);    // Init, Main, Boost

        DecompiledScript d = ScriptDecompiler.Decompile(Path, linked);
        List<SyntaxTree> trees = [.. d.Files().Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath))];
        Assert.Empty(ScriptCompiler.Check(trees));
        Assert.Equal(r.File, ScriptCompiler.Compile(trees).ApplyTo(linked).ToArray());
        Assert.Contains("1234", d.Source, StringComparison.Ordinal);
        Assert.Contains("0x11170", d.Source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The Larva (<c>m209</c>, the loader's in-game test): a postfix on worker 1's <c>Init</c> that requests a
    ///     new entry, which sets the HP. Needs <c>FFX_GAME_DIR</c>.
    /// </summary>
    [Fact]
    public void AModOfTheLarvaLinksOntoTheGamesFile() {
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
            using static Monsters.M209.M209;

            namespace Monsters.M209;

            public sealed partial class W01
            {
                [Postfix(nameof(Init))]
                void AskForHp()
                {
                    _ = W01.SetHpAsync();
                }

                public static async Task SetHpAsync()
                {
                    Btl.SetActorProperty(0, 1234);
                }
            }
            """, "mod/Larva.cs");
        Assert.Empty(ScriptCompiler.Check([.. source, mod]));
        CompiledMod compiled = ScriptCompiler.CompileMod(source, [mod]);
        compiled.Package.File = larva;
        LinkResult r = ScriptLinker.Link(original, [new ModScript("larva", compiled.Package)]);
        Assert.True(r.Changed, string.Join("\n", r.Messages));

        ScriptFile linked = ScriptFile.Parse(r.File);
        ScriptImage before = ScriptImage.FromBlock(vanilla.Script), after = ScriptImage.FromBlock(linked.Script);
        foreach (int w in new[] { 0, 2 }) Assert.Equal(before.Workers[w].Code, after.Workers[w].Code);
        Assert.Equal(before.Workers[1].Entries.Length + 1, after.Workers[1].Entries.Length);
        DecompiledScript d = ScriptDecompiler.Decompile(larva, linked);
        List<SyntaxTree> trees = [.. d.Files().Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath))];
        Assert.Empty(ScriptCompiler.Check(trees));
        Assert.Equal(r.File, ScriptCompiler.Compile(trees).ApplyTo(linked).ToArray());
        Assert.Contains("SetActorProperty(0, 1234)", d.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void AModCannotGiveAnEntryNumber() {
        string bad = Mod.Replace("public static async Task BoostAsync()", "[Entry(5)] public static async Task BoostAsync()", StringComparison.Ordinal);
        CompileException ex = Assert.Throws<CompileException>(() =>
            ScriptCompiler.CompileMod([ScriptCompiler.Parse(Vanilla, "M997.cs")], [ScriptCompiler.Parse(bad, "Larva.cs")]));
        Assert.Contains("[Replace(nameof", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APatchNamesAVanillaMethod() {
        string bad = Mod.Replace("[Postfix(nameof(Init))]", "[Postfix(\"Nope\")]", StringComparison.Ordinal);
        CompileException ex = Assert.Throws<CompileException>(() =>
            ScriptCompiler.CompileMod([ScriptCompiler.Parse(Vanilla, "M997.cs")], [ScriptCompiler.Parse(bad, "Larva.cs")]));
        Assert.Contains("no entry method Nope", ex.Message, StringComparison.Ordinal);
    }
}
