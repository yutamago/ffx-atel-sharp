// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;
using Value = Ffx.Atel.Compiler.Tests.MiniVm.Value;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>
///     The C# new code may use beyond what the original compiler made (docs/language.md): compiled as a mod
///     against a vanilla script, linked, run on <see cref="MiniVm"/>, and the linked script decompiled and
///     compiled back.
/// </summary>
public sealed class NewCodeTests {
    internal const string Path = "ffx_ps2/ffx/master/jppc/battle/mon/_m996/m996.bin";

    // Variables 0-3; worker 0 names registers 0 and 3 (so new code leaves them alone), worker 1 is a
    // function worker that changes integer register 1.
    internal const string Vanilla = """
        using Ffx;
        using Ffx.Atel;
        using static Monsters.M996.M996;

        namespace Monsters.M996;

        [Monster("m996")]
        [Workers(typeof(Larva), typeof(Fn))]
        [Header(Author = "test", Name = "996")]
        [SharedData(0x40, "")]
        [OtherData(0x40, "")]
        [EventData(0x10, "")]
        public static partial class M996
        {
            [Var(Storage.Shared, 0x0000)] public static int Hp;
            [Var(Storage.Shared, 0x0004)] public static float Speed;
            [Var(Storage.Shared, 0x0008)] public static int[] Table = new int[4];
            [Var(Storage.Shared, 0x0018)] public static int Out;
        }

        public sealed partial class Larva : Worker2
        {
            [Register(0)] public static int I0;
            [Register(3)] public static int I3;

            protected override void Init()
            {
                I0 = 1;
                I3 = 2;
                return;
            }

            protected override void Main()
            {
                Hp = 1;
                return;
            }
        }

        public sealed partial class Fn : Worker0
        {
            [Register(1)] public static int I1;

            public static void Run()
            {
                I1 = 9;
                return;
            }
        }
        """;

    internal static byte[] VanillaFile() {
        byte[] block = ScriptCompiler.Compile(Vanilla, "M996.cs").Block();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    /// <summary>A mod of <c>m996</c> from its source (a file in namespace <c>Monsters.M996</c>), linked.</summary>
    internal sealed record Linked(CompiledMod Mod, ScriptImage Image, ScriptFile File, byte[] Bytes);

    internal static Linked Link(string mod) {
        SyntaxTree vanilla = ScriptCompiler.Parse(Vanilla, "Monsters/M996/M996.cs");
        SyntaxTree tree = ScriptCompiler.Parse(mod, "mod/Mod.cs");
        IReadOnlyList<Diagnostic> found = ScriptCompiler.Check([vanilla, tree]);
        Assert.True(found.Count == 0, string.Join("\n", found));
        CompiledMod compiled = ScriptCompiler.CompileMod([vanilla], [tree]);
        compiled.Package.File = Path;
        LinkResult r = ScriptLinker.Link(VanillaFile(), [new ModScript("test", ScriptPackage.FromJson(compiled.Package.ToJson()))]);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        ScriptFile file = ScriptFile.Parse(r.File);
        return new Linked(compiled, ScriptImage.FromBlock(file.Script), file, r.File);
    }

    /// <summary>A mod that replaces <c>Larva.Main</c> with <paramref name="body"/> (and adds <paramref name="members"/>).</summary>
    internal static string Replaced(string body, string members = "") => $$"""
        using Ffx;
        using Ffx.Atel;
        using static Monsters.M996.M996;

        namespace Monsters.M996;

        public sealed partial class Larva
        {
            [Replace(nameof(Main))]
            void Test()
            {
        {{body}}
            }
        {{members}}
        }
        """;

    /// <summary>Links <paramref name="mod"/>, runs <c>Larva.Main</c>, and checks that the linked script decompiles and compiles back.</summary>
    internal static (MiniVm Vm, Linked Linked) Run(string mod, Func<ushort, Value[], Value>? native = null) {
        Linked linked = Link(mod);
        MiniVm vm = new(linked.Image, native);
        vm.Run(0, 1);
        RoundTrips(linked);
        return (vm, linked);
    }

    internal static void RoundTrips(Linked linked) {
        DecompiledScript d = ScriptDecompiler.Decompile(Path, linked.File);
        List<SyntaxTree> trees = [.. d.Files().Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath))];
        Assert.Empty(ScriptCompiler.Check(trees));
        Assert.Equal(linked.Bytes, ScriptCompiler.Compile(trees).ApplyTo(linked.File).ToArray());
    }

    private const int Hp = 0, Speed = 1, Table = 2, Out = 3;

    // ----- ++ and -- ----------------------------------------------------------------------------

    [Fact]
    public void IncrementAndDecrementAsStatements() {
        (MiniVm vm, _) = Run(Replaced("""
                    Hp = 3;
                    Hp++;
                    ++Hp;
                    Hp--;
                    Table[1] = 7;
                    Table[1]++;
                    Speed = 1.5f;
                    Speed++;
                    I0--;
            """));
        Assert.Equal(4, vm.Var(Hp).AsInt);
        Assert.Equal(8, vm.Var(Table, 1).AsInt);
        Assert.Equal(2.5f, vm.Var(Speed).AsFloat);
        Assert.Equal(-1, vm.Register(false, 0).AsInt);
    }

    [Fact]
    public void IncrementAndDecrementAsValues() {
        (MiniVm vm, _) = Run(Replaced("""
                    Hp = 3;
                    Out = Hp++ * 10 + ++Hp;
                    Table[2] = 5;
                    Table[0] = Table[2]--;
                    Table[1] = --Table[2];
                    Speed = 2f;
                    Speed = Speed++ + 0.5f;
            """));
        Assert.Equal(5, vm.Var(Hp).AsInt);
        Assert.Equal(35, vm.Var(Out).AsInt);
        Assert.Equal(5, vm.Var(Table, 0).AsInt);
        Assert.Equal(3, vm.Var(Table, 1).AsInt);
        Assert.Equal(3, vm.Var(Table, 2).AsInt);
        Assert.Equal(2.5f, vm.Var(Speed).AsFloat);
    }

    [Fact]
    public void AnElementsIndexIsEvaluatedOnce() {
        (MiniVm vm, _) = Run(Replaced("""
                    Hp = 0;
                    Table[Hp++]++;
                    Out = Table[Hp++]++;
            """));
        Assert.Equal(2, vm.Var(Hp).AsInt);
        Assert.Equal(1, vm.Var(Table, 0).AsInt);
        Assert.Equal(1, vm.Var(Table, 1).AsInt);
        Assert.Equal(0, vm.Var(Out).AsInt);
    }

    [Fact]
    public void CompoundAssignmentsOnEveryTarget() {
        (MiniVm vm, _) = Run(Replaced("""
                    Table[3] = 6;
                    Table[3] *= 7;
                    I0 = 5;
                    I0 <<= 2;
                    int n = 10;
                    n -= 3;
                    n %= 4;
                    Out = n;
                    Hp = I0;
            """));
        Assert.Equal(42, vm.Var(Table, 3).AsInt);
        Assert.Equal(20, vm.Var(Hp).AsInt);
        Assert.Equal(3, vm.Var(Out).AsInt);
    }

    // ----- Unary minus --------------------------------------------------------------------------

    [Fact]
    public void UnaryMinusIsNegForFloatsAndSubtractionForIntegers() {
        (MiniVm vm, Linked linked) = Run(Replaced("""
                    Hp = 7;
                    Hp = -Hp;
                    Speed = 1.25f;
                    Speed = -Speed;
                    Out = -(Hp * 2);
            """));
        Assert.Equal(-7, vm.Var(Hp).AsInt);
        Assert.Equal(-1.25f, vm.Var(Speed).AsFloat);
        Assert.Equal(14, vm.Var(Out).AsInt);
        byte[] code = linked.Mod.Package.Patches[0].Code.Bytes;
        Assert.Contains(Opcodes.Neg, code);
    }

    // ----- ?: -----------------------------------------------------------------------------------

    [Fact]
    public void ConditionalExpressions() {
        int handle = 3;
        (MiniVm vm, _) = Run(Replaced("""
                    Hp = Common.ChrGetHandle() > 2 ? 10 : 20;
                    Out = 1 + (Hp == 10 ? Hp * 2 : 0) + (Hp != 10 ? 100 : 1000);
                    Speed = Hp > 5 ? 0.5f : 1;
                    Table[0] = Hp < 0 ? 1 : Hp > 9 ? 2 : 3;
            """), (id, _) => id == 0x0009 ? Value.Int(handle) : Value.Int(0));
        Assert.Equal(10, vm.Var(Hp).AsInt);
        Assert.Equal(1021, vm.Var(Out).AsInt);
        Assert.Equal(0.5f, vm.Var(Speed).AsFloat);
        Assert.Equal(2, vm.Var(Table, 0).AsInt);
    }

    [Fact]
    public void AConditionalEvaluatesOnlyItsBranch() {
        (MiniVm vm, _) = Run(Replaced("""
                    Hp = 1;
                    Out = Hp > 0 ? Hp : Common.ChrGetHandle();
            """));
        Assert.Equal(1, vm.Var(Out).AsInt);
        Assert.Empty(vm.Calls);
    }

    // ----- && and || ----------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(5, 1)]
    public void ShortCircuitConditions(int hp, int calls) {
        (MiniVm vm, _) = Run(Replaced($$"""
                    Hp = {{hp}};
                    Out = 0;
                    if (Hp > 0 && Common.ChrGetHandle() == 4) Out += 1;
                    if (Hp == 0 || Common.ChrGetHandle() == 4) Out += 10;
                    if (!(Hp > 1 && Hp < 9)) Out += 100;
                    while (Hp > 0 && Hp < 3) Hp++;
                    Table[0] = AsInt(Hp > 2 || Hp < 0);
            """), (id, _) => Value.Int(4));
        Assert.Equal(calls * 2 + (hp == 1 ? 2 : 0), vm.Calls.Count);
        int expected = hp == 0 ? 10 + 100 : hp == 1 ? 1 + 10 + 100 : 1 + 10;
        Assert.Equal(expected, vm.Var(Out).AsInt);
        Assert.Equal(hp == 0 ? 0 : 1, vm.Var(Table, 0).AsInt);
    }

    [Fact]
    public void TheRightOperandsStatementsRunOnlyWhenNeeded() {
        (MiniVm vm, _) = Run(Replaced("""
                    Hp = 0;
                    Out = 0;
                    if (Hp > 0 && Hp++ > 0) Out = 1;
                    if (Hp == 0 || Hp++ > 0) Out += 10;
                    if (Hp == 5 || Hp++ > 5) Out += 100;
            """));
        Assert.Equal(1, vm.Var(Hp).AsInt);
        Assert.Equal(10, vm.Var(Out).AsInt);
    }

    [Fact]
    public void ShortCircuitConditionsInEveryLoop() {
        (MiniVm vm, _) = Run(Replaced("""
                    Hp = 0;
                    Out = 0;
                    do { Hp++; } while (Hp < 3 || Hp == 4);
                    for (Table[0] = 0; Table[0] < 10 && Out < 20; Table[0]++) Out += 5;
                    while (Out > 0 && Out-- > 15) { }
            """));
        Assert.Equal(3, vm.Var(Hp).AsInt);
        Assert.Equal(4, vm.Var(Table, 0).AsInt);
        Assert.Equal(14, vm.Var(Out).AsInt);
    }

    // ----- Locals -------------------------------------------------------------------------------

    [Fact]
    public void LocalsLiveInFreeRegisters() {
        (MiniVm vm, Linked linked) = Run(Replaced("""
                    int n = 3;
                    for (int i = 0; i < 4; i++) n += i;
                    float f = n * 0.5f;
                    Hp = n;
                    Speed = f;
            """));
        Assert.Equal(9, vm.Var(Hp).AsInt);
        Assert.Equal(4.5f, vm.Var(Speed).AsFloat);
        // No hidden storage, and not the registers the worker names (0 and 3).
        Assert.Empty(linked.Mod.Package.Variables);
        byte[] code = linked.Mod.Package.Patches[0].Code.Bytes;
        Assert.DoesNotContain(code, b => b is Opcodes.PopI0 or Opcodes.PopI0 + 3);
        Assert.Contains(code, b => b is Opcodes.PopI0 + 1 or Opcodes.PopI0 + 2);
    }

    [Fact]
    public void ALocalLiveAcrossAWaitLivesInHiddenStorage() {
        (MiniVm vm, Linked linked) = Run(Replaced("""
                    int kept = 7;
                    int gone = 1;
                    Hp = gone;
                    Common.WaitFrames(1);
                    Hp = kept + Hp;
            """));
        Assert.Equal(8, vm.Var(Hp).AsInt);
        NewVariable hidden = Assert.Single(linked.Mod.Package.Variables);
        Assert.Equal(5, hidden.Type);
        Assert.Equal(4, hidden.Storage);
    }

    [Fact]
    public void ALocalLiveAcrossACallOfAFunctionWorkerAvoidsItsRegisters() {
        (MiniVm vm, Linked linked) = Run(Replaced("""
                    int a = 4, b = 5;
                    Fn.Run();
                    Hp = a + b;
            """));
        Assert.Equal(9, vm.Var(Hp).AsInt);
        // A call is a wait to the allocator (Fn might poll), so both go to hidden storage.
        Assert.Equal(2, linked.Mod.Package.Variables.Count);
    }

    [Fact]
    public void LocalsInSwitchesAndNestedScopes() {
        (MiniVm vm, _) = Run(Replaced("""
                    const int Base = 100;
                    var total = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        switch (i)
                        {
                            case 0:
                                int x = 1;
                                total += x;
                                break;
                            case 1:
                                x = 20;
                                total += x;
                                break;
                            default:
                                total += Base;
                                break;
                        }
                    }
                    {
                        bool big = total > 100;
                        if (big & total < 1000) Hp = total;
                    }
            """));
        Assert.Equal(121, vm.Var(Hp).AsInt);
    }

    [Fact]
    public void AnUninitialisedLocalAndAFloatLocal() {
        (MiniVm vm, _) = Run(Replaced("""
                    float speed;
                    int k;
                    k = 2;
                    speed = k * 1.5f;
                    speed -= 0.25f;
                    Speed = -speed;
            """));
        Assert.Equal(-2.75f, vm.Var(Speed).AsFloat);
    }

    // ----- Named arguments ------------------------------------------------------------------------

    [Fact]
    public void NamedArgumentsBindByName() {
        (MiniVm vm, _) = Run(Replaced("""
                    Common.WaitFrames(frames: 3);
                    Speed = Common.MoveSetFrames(frames: 2f, elapsed: 1f);
                    Common.MoveSetFrames(1f, frames: 4f);
            """), (id, args) => id == 0x001c ? Value.Float(args[0].AsFloat - args[1].AsFloat) : Value.Int(0));
        Assert.Equal(3, vm.Calls[0].Args[0].AsInt);
        Assert.Equal(-1f, vm.Var(Speed).AsFloat);
        Assert.Equal([1f, 4f], vm.Calls[2].Args.Select(a => a.AsFloat));
    }

    [Fact]
    public void NamedArgumentsOutOfOrderAreEvaluatedInSourceOrder() {
        List<int> order = [];
        (MiniVm vm, _) = Run(Replaced("""
                    Out = Common.MapChange(entrance: Common.ChrGetHandle(), map: Common.WorkerDetach());
            """), (id, args) => {
                order.Add(id);
                return id == 0x0011 ? Value.Int(args[0].AsInt * 10 + args[1].AsInt) : Value.Int(id);
            });
        Assert.Equal([0x0009, 0x0005, 0x0011], order);
        Assert.Equal(5 * 10 + 9, vm.Var(Out).AsInt);
    }

    // ----- Without a mod --------------------------------------------------------------------------

    [Fact]
    public void AScriptWithNewCodeGetsItsHiddenVariablesInItsSharedData() {
        string source = Vanilla.Replace("""
                    Hp = 1;
                    return;
            """, """
                    int kept = Hp > 0 ? 40 : 50;
                    Common.WaitFrames(1);
                    Out = kept + 2;
                    return;
            """, StringComparison.Ordinal);
        Assert.NotEqual(Vanilla, source);
        ScriptImage image = ScriptCompiler.Compile(source, "M996.cs").Image;
        Assert.Equal(5, image.Variables.Count);
        Assert.Equal((5, 4, 0x40), (image.Variables[4].Type, image.Variables[4].Storage, image.Variables[4].Offset));
        Assert.Equal(0x44, image.Shared!.Length);
        MiniVm vm = new(image);
        vm.Run(0, 1);
        Assert.Equal(52, vm.Var(Out).AsInt);
    }

    [Theory]
    [InlineData("int[] a = new int[2];", "a local cannot be an array")]
    [InlineData("long a = 1;", "a local cannot be long")]
    [InlineData("var a;", "var needs an initializer")]
    public void WhatALocalCannotBe(string declaration, string message) {
        string source = Vanilla.Replace("        Hp = 1;\n        return;", $"        {declaration}\n        return;", StringComparison.Ordinal);
        Assert.NotEqual(Vanilla, source);
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.Compile(source, "M996.cs"));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }
}
