// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary><c>[Saved]</c> fields (docs/language.md, "Saved fields"): compiled into storage 0 variables the linker places from the run's table.</summary>
public sealed class SavedTests {
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
        }

        public sealed partial class Larva : Worker2
        {
            protected override void Init()
            {
                Hp = 5;
                return;
            }

            protected override void Main()
            {
                return;
            }
        }
        """;

    private const string Progress = """
        using Ffx.Atel;

        namespace LarvaCount;

        public static class Progress
        {
            [Saved] public static ushort LarvasMet;
            [Saved] public static byte Tier = 1;
            [Saved] public static byte[] Flags = new byte[8];
            [Saved(Key = "Met")] public static int Seen = -2;
        }
        """;

    private const string Mod = """
        using Ffx;
        using Ffx.Atel;
        using static Monsters.M997.M997;

        namespace Monsters.M997;

        public static partial class M997
        {
            [Saved] public static short Wins = 3;
            [Saved(Mod = "yuta.other", Key = "Other.Stats.Kills")] public static int OtherKills;
        }

        public sealed partial class Larva
        {
            [Postfix(nameof(Init))]
            void Count()
            {
                LarvaCount.Progress.LarvasMet += 1;
                if (LarvaCount.Progress.LarvasMet >= 10) LarvaCount.Progress.Flags[0] |= 1;
                Wins += 1;
                Hp = OtherKills;
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

    private static List<SyntaxTree> ModTrees(string mod = Mod) => [ScriptCompiler.Parse(Progress, "mod/Progress.cs"), ScriptCompiler.Parse(mod, "mod/Larva.cs")];

    private static CompiledMod CompileMod(string mod = Mod) {
        List<SyntaxTree> trees = ModTrees(mod);
        // As atel compile-mod does: the script's own files, and the saved fields of all the mod's files.
        return ScriptCompiler.CompileMod([ScriptCompiler.Parse(Vanilla, "Monsters/M997/M997.cs")], [trees[1]], saved: ScriptCompiler.ReadSaved(trees));
    }

    [Fact]
    public void TheModIsCSharp() {
        IReadOnlyList<Diagnostic> found = ScriptCompiler.Check([ScriptCompiler.Parse(Vanilla, "M997.cs"), .. ModTrees()]);
        Assert.Empty(found);
    }

    [Fact]
    public void SavedFieldsAreReadFromEveryFileWithTheirKeysAndInitialValues() {
        SavedDeclarations saved = ScriptCompiler.ReadSaved(ModTrees());
        // In the order of the mod's files (Larva.cs, then Progress.cs), each in declaration order.
        Assert.Equal(["Monsters.M997.M997.Wins", "yuta.other/Other.Stats.Kills", "LarvaCount.Progress.LarvasMet", "LarvaCount.Progress.Tier", "LarvaCount.Progress.Flags", "Met"],
            saved.Fields.Select(f => f.PackageKey));
        Assert.Equal(["yuta.other"], saved.ReadMods);
        SavedFile file = saved.ToFile()!;
        // Only the mod's own fields; another mod's is that mod's to declare.
        Assert.Equal(["Monsters.M997.M997.Wins", "LarvaCount.Progress.LarvasMet", "LarvaCount.Progress.Tier", "LarvaCount.Progress.Flags", "Met"], file.Fields.Select(f => f.Key));
        Assert.Equal([(3, 1, false), (2, 1, false), (0, 1, false), (0, 8, true), (5, 1, false)], file.Fields.Select(f => (f.Type, f.Length, f.Array)));
        Assert.Equal([3, 0], file.Fields[0].Initial);
        Assert.Null(file.Fields[1].Initial);
        Assert.Equal([1], file.Fields[2].Initial);
        Assert.Equal([0xfe, 0xff, 0xff, 0xff], file.Fields[4].Initial);
        SavedFile back = SavedFile.FromJson(file.ToJson());
        Assert.Equal(file.Fields.Select(f => f.Key), back.Fields.Select(f => f.Key));
    }

    [Fact]
    public void TheScriptsPackageListsTheSavedFieldsItUses() {
        ScriptPackage p = CompileMod().Package;
        Assert.Equal(ScriptPackage.SavedFormat, p.Format);
        List<NewVariable> saved = [.. p.Variables.Where(v => v.Saved is not null)];
        // Tier and Met are not used by this script, so its package does not list them.
        Assert.Equal(["Monsters.M997.M997.Wins", "yuta.other/Other.Stats.Kills", "LarvaCount.Progress.LarvasMet", "LarvaCount.Progress.Flags"], saved.Select(v => v.Saved));
        Assert.All(saved, v => {
            Assert.Equal(0, v.Storage);
            Assert.Null(v.Offset);
            Assert.Null(v.Initial);
        });
        Assert.True(saved[3].Array);
        Assert.Equal(8, saved[3].Length);
    }

    [Fact]
    public void AnotherModsFieldIsReadOnly() {
        string mod = Mod.Replace("Hp = OtherKills;", "OtherKills = 5;", StringComparison.Ordinal);
        CompileException ex = Assert.Throws<CompileException>(() => CompileMod(mod));
        Assert.Contains("yuta.other's saved field", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[Saved] public ushort Bad;", "static")]
    [InlineData("[Saved] public static string Bad;", "a variable cannot be")]
    [InlineData("[Saved] public static byte[] Bad;", "size")]
    [InlineData("[Saved, Var(Storage.Save, 0x10)] public static byte Bad;", "[Var]")]
    [InlineData("[Saved(Mod = \"x\")] public static byte Bad;", "Key")]
    [InlineData("[Saved(Mod = \"x\", Key = \"A.B\")] public static byte Bad = 4;", "initial value is that mod's")]
    [InlineData("[Saved(Key = \"LarvaCount.Progress.Tier\")] public static byte Bad;", "two [Saved] fields")]
    public void AFieldThatCannotBeSavedIsAnError(string field, string message) {
        string mod = Mod.Replace("[Saved] public static short Wins = 3;", $"[Saved] public static short Wins = 3;\n    {field}", StringComparison.Ordinal);
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.ReadSaved(ModTrees(mod)));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AClassOfSavedFieldsHoldsNothingElse() {
        string progress = Progress.Replace("[Saved] public static ushort LarvasMet;", "[Saved] public static ushort LarvasMet;\n    public const int Goal = 10;", StringComparison.Ordinal);
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.ReadSaved([ScriptCompiler.Parse(progress, "mod/Progress.cs")]));
        Assert.Contains("holds those only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASavedFieldInAWorkerIsAnError() {
        string mod = Mod.Replace("    [Postfix(nameof(Init))]", "    [Saved] public static byte Bad;\n    [Postfix(nameof(Init))]", StringComparison.Ordinal);
        CompileException ex = Assert.Throws<CompileException>(() => CompileMod(mod));
        Assert.Contains("not in a worker", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Linked with a table that puts the fields where a run of the loader would: each variable is storage 0 at its
    ///     field's offset, the same in every script; the linked file decompiles to <c>Save.ModArea</c> offsets and compiles back.
    /// </summary>
    [Fact]
    public void TheLinkerPlacesSavedFieldsFromTheTable() => LinkAndDecompile(Mod, otherType: 5);

    /// <summary>
    ///     Issue #74: a <c>uint</c> variable stored into an <c>int</c> one decompiles with its cast
    ///     (<c>Hp = (int)OtherKills;</c>), else the C# does not compile (CS0266). The shipped scripts declare no
    ///     <c>u32</c> variable, so only a mod's field meets it.
    /// </summary>
    [Fact]
    public void AUintFieldStoredIntoAnIntKeepsItsCast() {
        string mod = Mod.Replace("public static int OtherKills;", "public static uint OtherKills;", StringComparison.Ordinal)
            .Replace("Hp = OtherKills;", "Hp = (int)OtherKills;", StringComparison.Ordinal);
        string source = LinkAndDecompile(mod, otherType: 4);
        Assert.Contains("public static uint ", source, StringComparison.Ordinal);
        // Hp is the worker's private variable, V0 once decompiled; the other mod's field is placed at the area's start.
        Assert.Contains("V0 = (int)ModArea;", source, StringComparison.Ordinal);
    }

    private static string LinkAndDecompile(string mod, int otherType) {
        List<SyntaxTree> trees = ModTrees(mod);
        SavedDeclarations saved = ScriptCompiler.ReadSaved(trees);
        ScriptPackage p = ScriptPackage.FromJson(CompileMod(mod).Package.ToJson());
        p.File = Path;
        AtelMod other = new() { Id = "yuta.other" };
        other.Saved.Add(new SavedField { Key = "Other.Stats.Kills", Type = otherType });
        AtelMod mine = new() { Id = "yuta.larvacount", Manifest = new ModManifest { Requires = ["yuta.other"] } };
        mine.Saved.AddRange(saved.ToFile()!.Fields);
        mine.Packages[Path] = p;
        List<LinkMessage> messages = [];
        SavedLayout layout = SavedLayout.Build([other, mine], messages, out List<AtelMod> kept);
        Assert.Equal(["yuta.other", "yuta.larvacount"], kept.Select(m => m.Id));
        Assert.DoesNotContain(messages, m => m.Severity == LinkSeverity.Error);

        LinkResult r = ScriptLinker.Link(VanillaFile(), [new ModScript("yuta.larvacount", p)], saved: layout);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        ScriptFile linked = ScriptFile.Parse(r.File);
        ScriptImage image = ScriptImage.FromBlock(linked.Script);
        foreach ((string key, int index) in new[] { ("yuta.larvacount/Monsters.M997.M997.Wins", 1), ("yuta.other/Other.Stats.Kills", 2),
                     ("yuta.larvacount/LarvaCount.Progress.LarvasMet", 3), ("yuta.larvacount/LarvaCount.Progress.Flags", 4) }) {
            VarEntry v = image.Variables[index];
            Assert.Equal(0, v.Storage);
            Assert.Equal(layout.Find(key)!.Offset, v.Offset);
        }
        // yuta.other first: its i32 at the area's start; then Wins (i16), LarvasMet (u16), Tier (u8), Flags (8 × u8), Met (i32, aligned).
        Assert.Equal([0x62cc, 0x62d0, 0x62d2, 0x62d4, 0x62d5, 0x62e0], layout.Fields.Select(f => f.Offset));

        DecompiledScript d = ScriptDecompiler.Decompile(Path, linked);
        Assert.Contains("[Var(Storage.Save, Save.ModArea + 0x4)]", d.Source, StringComparison.Ordinal);
        Assert.Contains("[Var(Storage.Save, Save.ModArea)]", d.Source, StringComparison.Ordinal);
        List<SyntaxTree> back = [.. d.Files().Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath))];
        Assert.Empty(ScriptCompiler.Check(back));
        Assert.Equal(r.File, ScriptCompiler.Compile(back).ApplyTo(linked).ToArray());
        return string.Concat(d.Files().Select(f => f.Text));
    }

    [Fact]
    public void WithoutAPlaceTheLinkerLeavesThePackageOut() {
        ScriptPackage p = ScriptPackage.FromJson(CompileMod().Package.ToJson());
        p.File = Path;
        LinkResult r = ScriptLinker.Link(VanillaFile(), [new ModScript("yuta.larvacount", p)]);
        Assert.False(r.Changed);
        Assert.Contains(r.Messages, m => m.Severity == LinkSeverity.Error && m.Text.Contains("no place in the save area", StringComparison.Ordinal));
    }
}
