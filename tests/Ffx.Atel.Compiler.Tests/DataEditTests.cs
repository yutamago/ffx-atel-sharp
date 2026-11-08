// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>Per-field data edits: a mod's static constructors and new variables' initializers, compiled and linked, without the game.</summary>
public sealed class DataEditTests {
    private const string MonsterPath = "ffx_ps2/ffx/master/jppc/battle/mon/_m997/m997.bin";
    private const string EventPath = "ffx_ps2/ffx/master/jppc/event/obj/zz/zztest00/zztest00.ebp";

    private const string Monster = """
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
            [Var(Storage.Shared, 0x0004)] public static byte[] Table = new byte[4];
            [Var(Storage.Shared, 0x0008)] public static float Speed;
            [Var(Storage.Private, 0x0000)] public static int Mine;
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

    private const string Event = """
        using Ffx;
        using Ffx.Atel;
        using static Events.Zztest00.Zztest00;

        namespace Events.Zztest00;

        [Event("zztest00")]
        [Workers(typeof(Boy))]
        [Header(Author = "test", Name = "001")]
        [SharedData(0x20, "")]
        [OtherData(0x40, "")]
        [EventData(0x10, "")]
        public static partial class Zztest00
        {
            [Var(Storage.Private, 0x0004)] public static short Steps;
            [Var(Storage.Event, 0x0000)] public static int Flag;
        }

        [Worker(PrivateLength = 0x10)]
        [PrivateData(0x10, "0000: 01000000 0200")]
        public sealed partial class Boy : Worker1
        {
            protected override void Init()
            {
                Steps = 1;
                return;
            }

            protected override void Main()
            {
                return;
            }
        }
        """;

    /// <summary>A monster file: the script, a stat record (section 2) and its copy (6) with different string references, the rewards (4).</summary>
    internal static byte[] MonsterFile(string source = Monster) {
        byte[] block = ScriptCompiler.Compile(source, "M997.cs").Block();
        int script = 0x40, length = (block.Length + 0x3f) / 0x40 * 0x40;
        int[] at = new int[8];
        at[0] = script;
        at[1] = script + length;
        at[2] = at[1] + 0x40;
        at[4] = at[2] + 0xc0;
        at[5] = at[4] + 0x140;
        at[6] = at[5] + 0x40;
        at[7] = at[6] + 0xc0;
        byte[] file = new byte[at[7]];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 8);
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4 + 4 * i), (uint)at[i]);
        block.CopyTo(file, script);
        foreach (int s in new[] { at[2], at[6] }) {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(s + 0x14), 1498);    // HP
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(s + 0x18), 1000);    // MP
            file[s + 0x20] = 10;
            file[s + 0x22] = 19;
        }
        file[at[2] + 2] = 0x36;    // the string references differ
        file[at[6] + 2] = 0x2b;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at[4]), 330);    // gil
        return file;
    }

    internal static byte[] EventFile() {
        byte[] block = ScriptCompiler.Compile(Event, "Zztest00.cs").Block();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        "EV01"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), 0xffff_ffff);
        block.CopyTo(file, 0x40);
        return file;
    }

    private static ScriptPackage Compile(string vanilla, string mod, string path, byte[] file) {
        CompiledMod m = ScriptCompiler.CompileMod([ScriptCompiler.Parse(vanilla, "V.cs")], [ScriptCompiler.Parse(mod, "mod/Mod.cs")]);
        m.Package.File = path;
        DataAreas.Attach(m.Package, file);
        return ScriptPackage.FromJson(m.Package.ToJson());
    }

    private static string MonsterMod(string body, string members = "") => $$"""
        using Ffx;
        using Ffx.Atel;
        using static Monsters.M997.M997;

        namespace Monsters.M997;

        public static partial class M997
        {
            {{members}}
            static M997()
            {
                {{body}}
            }
        }
        """;

    private static int Section(byte[] file, int i) => (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4 + 4 * i));

    private static int SharedAt(byte[] file) {
        ScriptFile f = ScriptFile.Parse(file);
        return f.SectionStart + (int)f.Script.U32(f.Script.Workers[0].Header + 0x30);
    }

    [Fact]
    public void TheModIsCSharp() {
        string mod = MonsterMod("MonsterStats.Hp = 4321; MonsterRewards.Gil = 999; Hp = 77; Table[2] = 9; Speed = 2.5f; Data.At<ushort>(DataArea.Other, 0x20) = 7;",
            "public static int Turns = 5; public static float[] Speeds = { 1.5f, 2f };");
        Assert.Empty(ScriptCompiler.Check([ScriptCompiler.Parse(Monster, "V.cs"), ScriptCompiler.Parse(mod, "Mod.cs")]));
    }

    [Fact]
    public void AStaticConstructorBecomesDataEdits() {
        byte[] file = MonsterFile();
        ScriptPackage p = Compile(Monster, MonsterMod("MonsterStats.Hp = 4321; MonsterStats.Strength = 30; MonsterRewards.Gil = 999; Hp = 77; Table[2] = 9; Speed = 2.5f; Data.At<ushort>(DataArea.Other, 0x20) = 7;"), MonsterPath, file);
        Assert.Equal(ScriptPackage.DataFormat, p.Format);
        Assert.Equal(
            [(DataEditArea.MonsterStats, 0x14, "e1100000", "MonsterStats.Hp"), (DataEditArea.MonsterStats, 0x20, "1e", "MonsterStats.Strength"),
             (DataEditArea.MonsterRewards, 0, "e703", "MonsterRewards.Gil"), (DataEditArea.Shared, 0, "4d000000", "Hp"), (DataEditArea.Shared, 6, "09", "Table[2]"),
             (DataEditArea.Shared, 8, "00002040", "Speed"), (DataEditArea.Other, 0x20, "0700", "Data.At<ushort>(Other, 0x20)")],
            p.Data!.Select(e => (e.Area, e.Offset, Convert.ToHexStringLower(e.Bytes), e.Field)));
        Assert.Equal([DataEditArea.MonsterStats, DataEditArea.MonsterRewards, DataEditArea.Shared, DataEditArea.Other], p.Vanilla.Data!.Select(d => d.Area));
        Assert.Empty(p.Patches);
        Assert.Empty(p.Workers);
    }

    /// <summary>A package with only data edits changes those bytes and nothing else: no code moves, the file keeps its size.</summary>
    [Fact]
    public void DataEditsChangeOnlyTheirBytes() {
        byte[] file = MonsterFile();
        ScriptPackage p = Compile(Monster, MonsterMod("MonsterStats.Hp = 4321; MonsterRewards.Gil = 999; Table[2] = 9;"), MonsterPath, file);
        LinkResult r = ScriptLinker.Link(file, [new ModScript("tough", p)]);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        Assert.Equal(file.Length, r.File.Length);
        int s2 = Section(file, 2), s4 = Section(file, 4), s6 = Section(file, 6), shared = SharedAt(file);
        HashSet<int> expected = [.. Enumerable.Range(s2 + 0x14, 2), .. Enumerable.Range(s6 + 0x14, 2), .. Enumerable.Range(s4, 2), shared + 6];
        List<int> changed = [.. Enumerable.Range(0, file.Length).Where(i => file[i] != r.File[i])];
        Assert.Equal(expected.Order(), changed);
        Assert.Equal(4321u, BinaryPrimitives.ReadUInt32LittleEndian(r.File.AsSpan(s2 + 0x14)));
        Assert.Equal(4321u, BinaryPrimitives.ReadUInt32LittleEndian(r.File.AsSpan(s6 + 0x14)));
        Assert.Equal(999, BinaryPrimitives.ReadUInt16LittleEndian(r.File.AsSpan(s4)));
        Assert.Contains(r.Messages, m => m.Severity == LinkSeverity.Info && m.Text.Contains("sets MonsterStats.Hp (the monster's stats +0x14, 4 byte(s))", StringComparison.Ordinal));
    }

    /// <summary>Two mods that set the same field conflict and the later wins; edits of different fields combine.</summary>
    [Fact]
    public void SameFieldConflictsOtherFieldsCombine() {
        byte[] file = MonsterFile();
        ScriptPackage a = Compile(Monster, MonsterMod("MonsterStats.Hp = 2000; MonsterStats.Strength = 30;"), MonsterPath, file);
        ScriptPackage b = Compile(Monster, MonsterMod("MonsterStats.Hp = 3000; MonsterStats.Magic = 40;"), MonsterPath, file);
        LinkResult r = ScriptLinker.Link(file, [new ModScript("a", a), new ModScript("b", b)]);
        Assert.Equal(["a", "b"], r.Applied);
        int s2 = Section(file, 2);
        Assert.Equal(3000u, BinaryPrimitives.ReadUInt32LittleEndian(r.File.AsSpan(s2 + 0x14)));
        Assert.Equal(30, r.File[s2 + 0x20]);
        Assert.Equal(40, r.File[s2 + 0x22]);
        LinkMessage conflict = Assert.Single(r.Messages, m => m.Severity == LinkSeverity.Conflict);
        Assert.Equal("b", conflict.Mod);
        Assert.Contains("a sets MonsterStats.Hp and b sets MonsterStats.Hp", conflict.Text, StringComparison.Ordinal);
        Assert.Contains("b wins", conflict.Text, StringComparison.Ordinal);
    }

    /// <summary>A mod built against other vanilla bytes (another game version) is left out for that script.</summary>
    [Fact]
    public void AnotherGamesDataIsAHashMismatch() {
        byte[] file = MonsterFile();
        ScriptPackage p = Compile(Monster, MonsterMod("MonsterStats.Hp = 4321;"), MonsterPath, file);
        byte[] other = (byte[])file.Clone();
        other[Section(file, 2) + 0x18] = 99;    // another MP
        other[Section(file, 6) + 0x18] = 99;
        LinkResult r = ScriptLinker.Link(other, [new ModScript("tough", p)]);
        Assert.False(r.Changed);
        Assert.Contains(r.Messages, m => m.Severity == LinkSeverity.Error && m.Text.Contains("hash mismatch", StringComparison.Ordinal));
    }

    /// <summary>New variables take their initial values from their initializers; data edits and code link together.</summary>
    [Fact]
    public void NewVariablesHaveInitialValues() {
        byte[] file = MonsterFile();
        string mod = MonsterMod("Hp = 77;", "public static int Turns = 5; public static float[] Speeds = { 1.5f, 2f }; public static short Plain;") + """

            public sealed partial class Larva
            {
                [Postfix(nameof(Init))]
                void Count() { Turns = Turns + 1; Speeds[1] = 3f; }
            }
            """;
        ScriptPackage p = Compile(Monster, mod, MonsterPath, file);
        Assert.Equal(["05000000", "0000c03f00000040", null], p.Variables.Select(v => v.Initial is null ? null : Convert.ToHexStringLower(v.Initial)));
        Assert.Equal(2, p.Variables[1].Length);
        LinkResult r = ScriptLinker.Link(file, [new ModScript("m", p)]);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        ScriptFile linked = ScriptFile.Parse(r.File);
        ScriptImage image = ScriptImage.FromBlock(linked.Script);
        VarEntry turns = image.Variables[^3], speeds = image.Variables[^2];
        int shared = linked.SectionStart + (int)linked.Script.U32(linked.Script.Workers[0].Header + 0x30);
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(r.File.AsSpan(shared + turns.Offset)));
        Assert.Equal(2f, BinaryPrimitives.ReadSingleLittleEndian(r.File.AsSpan(shared + speeds.Offset + 4)));
        Assert.Equal(77, BinaryPrimitives.ReadInt32LittleEndian(r.File.AsSpan(shared)));
        Assert.Equal(image.Write(), linked.Script.Bytes[..(int)linked.Script.U32(0x10)].ToArray());
    }

    /// <summary>An event worker's private data, from the worker's static constructor.</summary>
    [Fact]
    public void AWorkersStaticConstructorSetsItsPrivateData() {
        byte[] file = EventFile();
        string mod = """
            using Ffx;
            using Ffx.Atel;
            using static Events.Zztest00.Zztest00;

            namespace Events.Zztest00;

            public static partial class Zztest00
            {
                static Zztest00() { Flag = -2; }
            }

            public sealed partial class Boy
            {
                static Boy() { Steps = 12; }
            }
            """;
        ScriptPackage p = Compile(Event, mod, EventPath, file);
        Assert.Equal([(DataEditArea.Event, null, 0, "feffffff"), (DataEditArea.Private, 0, 4, "0c00")],
            p.Data!.Select(e => (e.Area, e.Worker, e.Offset, Convert.ToHexStringLower(e.Bytes))));
        LinkResult r = ScriptLinker.Link(file, [new ModScript("m", p)]);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        ScriptFile linked = ScriptFile.Parse(r.File);
        int priv = linked.SectionStart + (int)linked.Script.U32(linked.Script.Workers[0].Header + 0x2c);
        Assert.Equal("010000000c00", Convert.ToHexStringLower(r.File.AsSpan(priv, 6)));
        Assert.Equal(file.Length, r.File.Length);
    }

    /// <summary>
    ///     A mod's new worker with private data of its own (<c>[Worker(PrivateLength = …)]</c>), its initial values from its
    ///     static constructor: the mod's own private variable and a vanilla one.
    /// </summary>
    [Fact]
    public void ANewWorkerHasPrivateData() {
        byte[] file = EventFile();
        string mod = """
            using Ffx;
            using Ffx.Atel;
            using static Events.Zztest00.Zztest00;

            namespace Events.Zztest00;

            public static partial class Zztest00
            {
                [Var(Storage.Private, 0x0008)] public static int Count;
            }

            [Worker(PrivateLength = 0x0c)]
            public sealed partial class Helper : LogicWorker
            {
                static Helper() { Count = 3; Steps = 9; }
                protected override void Init() { Count = Count + 1; return; }
                protected override void Main() { return; }
            }
            """;
        Assert.Empty(ScriptCompiler.Check([ScriptCompiler.Parse(Event, "V.cs"), ScriptCompiler.Parse(mod, "Mod.cs")]));
        ScriptPackage p = Compile(Event, mod, EventPath, file);
        NewWorker helper = Assert.Single(p.Workers);
        Assert.Equal(0x0cu, helper.PrivateLength);
        Assert.Equal("000000000900000003000000", Convert.ToHexStringLower(helper.Private!));    // Steps at 4, Count at 8
        Assert.Null(p.Data);
        Assert.Equal(ScriptPackage.DataFormat, p.Format);
        LinkResult r = ScriptLinker.Link(file, [new ModScript("m", p)]);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        ScriptFile linked = ScriptFile.Parse(r.File);
        ScriptImage image = ScriptImage.FromBlock(linked.Script);
        Assert.Equal(image.Write(), linked.Script.Bytes[..(int)linked.Script.U32(0x10)].ToArray());
        WorkerImage w = image.Workers[1];
        Assert.Equal(0x0cu, w.PrivateLength);
        Assert.Equal("000000000900000003000000", Convert.ToHexStringLower(w.Private!.AsSpan(0, 12)));
        Assert.Equal("01000000", Convert.ToHexStringLower(image.Workers[0].Private!.AsSpan(0, 4)));    // the vanilla worker's own
    }

    [Fact]
    public void AMonstersNewWorkerHasPrivateDataWithoutInitialValues() {
        string mod = MonsterMod("Hp = 1;") + """

            [Worker(PrivateLength = 0x10)]
            public sealed partial class Helper : BattleWorker
            {
                protected override void Init() { Mine = 2; return; }
            }
            """;
        ScriptPackage p = Compile(Monster, mod, MonsterPath, MonsterFile());
        Assert.Equal(0x10u, Assert.Single(p.Workers).PrivateLength);
        Assert.Null(p.Workers[0].Private);
        LinkResult r = ScriptLinker.Link(MonsterFile(), [new ModScript("m", p)]);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        Assert.Equal(0x10u, ScriptImage.FromBlock(ScriptFile.Parse(r.File).Script).Workers[1].PrivateLength);

        string bad = mod.Replace("protected override void Init()", "static Helper() { Mine = 3; }\n    protected override void Init()", StringComparison.Ordinal);
        CompileException ex = Assert.Throws<CompileException>(() => Compile(Monster, bad, MonsterPath, MonsterFile()));
        Assert.Contains("in battle the game clears", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Mine = 3;", "private data (storage 3) is each worker's own")]
    [InlineData("Hp = Hp + 1;", "an initial value is a constant")]
    [InlineData("Hp = 1; Hp = 2;", "Hp is set twice")]
    [InlineData("Table[4] = 1;", "Table has 4 element(s)")]
    [InlineData("Btl.SetActorProperty(ActorProperty.Hp, 1);", "a static constructor holds initial values only")]
    [InlineData("MonsterStats.Hp += 5;", "a static constructor holds initial values only")]
    [InlineData("Table[1] = 300;", "300 does not fit 1 byte(s)")]
    [InlineData("Data.At<int>(DataArea.Shared, 0x3e) = 1;", "the shared data is 0x40 bytes; this is past it")]
    public void WhatAStaticConstructorCannotSet(string body, string message) {
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.CompileMod([ScriptCompiler.Parse(Monster, "V.cs")], [ScriptCompiler.Parse(MonsterMod(body), "Mod.cs")]));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>In battle the game clears a worker's private data at set-up, so a monster's worker cannot set it.</summary>
    [Fact]
    public void AMonstersPrivateDataHasNoInitialValue() {
        string mod = MonsterMod("Hp = 1;") + "\npublic sealed partial class Larva { static Larva() { Mine = 3; } }\n";
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.CompileMod([ScriptCompiler.Parse(Monster, "V.cs")], [ScriptCompiler.Parse(mod, "Mod.cs")]));
        Assert.Contains("in battle the game clears", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventHasNoMonsterStats() {
        string mod = """
            using Ffx;
            using Ffx.Atel;
            namespace Events.Zztest00;
            public static partial class Zztest00 { static Zztest00() { MonsterStats.Hp = 5; } }
            """;
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.CompileMod([ScriptCompiler.Parse(Event, "V.cs")], [ScriptCompiler.Parse(mod, "Mod.cs")]));
        Assert.Contains("only a monster script", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The compiler's table of MonsterStats and MonsterRewards names every property of the reference assembly's classes.</summary>
    [Fact]
    public void TheMonsterFieldsMatchTheReferenceAssembly() {
        foreach (Type t in new[] { typeof(Ffx.MonsterStats), typeof(Ffx.MonsterRewards) }) {
            Assert.Equal(t.GetProperties().Select(p => p.Name).Order(), ScriptCompiler.MonsterFields[t.Name].Keys.Order());
            foreach (var p in t.GetProperties()) {
                int size = ScriptCompiler.MonsterFields[t.Name][p.Name].Type switch { 0 => 1, 2 => 2, _ => 4 };
                Assert.Equal(System.Runtime.InteropServices.Marshal.SizeOf(p.PropertyType), size);
            }
        }
    }

    /// <summary>
    ///     The Larva (<c>m209</c>, battle 34) with 4321 HP and 999 gil, as the in-game check does it: only the stat record,
    ///     its copy and the rewards change. Needs <c>FFX_GAME_DIR</c>.
    /// </summary>
    [Fact]
    public void TheLarvasStatsLinkOntoTheGamesFile() {
        string? dir = Bytecode.GameData.GameDir();
        if (dir is null) Assert.Skip($"{Bytecode.GameData.GameDirVariable} is not set");
        const string larva = "ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin";
        byte[] original;
        using (Bytecode.Vbf.VbfArchive archive = Bytecode.GameData.OpenArchive(dir!)) original = archive.Read(larva);
        ScriptFile vanilla = ScriptFile.Parse(original);
        List<SyntaxTree> source = [.. Decompiler.ScriptDecompiler.Decompile(larva, vanilla).Files().Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath))];
        SyntaxTree mod = ScriptCompiler.Parse("""
            using Ffx;
            using Ffx.Atel;

            namespace Monsters.M209;

            public static partial class M209
            {
                static M209()
                {
                    MonsterStats.Hp = 4321;
                    MonsterRewards.Gil = 999;
                }
            }
            """, "mod/Larva.cs");
        Assert.Empty(ScriptCompiler.Check([.. source, mod]));
        CompiledMod compiled = ScriptCompiler.CompileMod(source, [mod]);
        compiled.Package.File = larva;
        DataAreas.Attach(compiled.Package, original);
        LinkResult r = ScriptLinker.Link(original, [new ModScript("tough-larva", compiled.Package)]);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        Assert.Equal(original.Length, r.File.Length);
        int s2 = Section(original, 2), s4 = Section(original, 4), s6 = Section(original, 6);
        Assert.Equal(1498u, BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(s2 + 0x14)));
        Assert.Equal(4321u, BinaryPrimitives.ReadUInt32LittleEndian(r.File.AsSpan(s2 + 0x14)));
        Assert.Equal(4321u, BinaryPrimitives.ReadUInt32LittleEndian(r.File.AsSpan(s6 + 0x14)));
        Assert.Equal(999, BinaryPrimitives.ReadUInt16LittleEndian(r.File.AsSpan(s4)));
        Assert.Equal(6, Enumerable.Range(0, original.Length).Count(i => original[i] != r.File[i]));
    }
}
