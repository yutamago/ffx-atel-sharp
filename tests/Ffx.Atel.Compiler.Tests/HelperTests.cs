// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Linker;
using static Ffx.Atel.Compiler.Tests.NewCodeTests;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>Helper methods (docs/plan.md, "Three kinds of code"): hidden function workers with parameters and a result.</summary>
public sealed class HelperTests {
    private const int Hp = 0, Speed = 1, Table = 2, Out = 3;

    [Fact]
    public void HelpersTakeParametersAndReturnValues() {
        (MiniVm vm, Linked linked) = Run(Replaced("""
                    Hp = Twice(21);
                    Out = Add(Twice(2), 3);
                    Speed = Half(5f);
                    Count();
                    Count();
                    Table[1] = Add(b: 10, a: Twice(Add(1, 1)));
                    Table[2] = Add(Twice(1), Twice(Add(2, 0)));
            """, """
                static int Twice(int x) { return x * 2; }
                static int Add(int a, int b) => a + b;
                static float Half(float f) => f / 2f;
                void Count() { Table[0]++; }
            """));
        Assert.Equal(42, vm.Var(Hp).AsInt);
        Assert.Equal(7, vm.Var(Out).AsInt);
        Assert.Equal(2.5f, vm.Var(Speed).AsFloat);
        Assert.Equal(2, vm.Var(Table, 0).AsInt);
        Assert.Equal(14, vm.Var(Table, 1).AsInt);
        Assert.Equal(6, vm.Var(Table, 2).AsInt);
        // Four hidden function workers, after the vanilla ones. Twice(1)'s result is live across two calls
        // whose registers cover the two the worker leaves free (i1, i2): the one local in hidden storage.
        ScriptPackage p = linked.Mod.Package;
        Assert.Equal(4, p.Workers.Count);
        Assert.All(p.Workers, w => Assert.Equal(0, w.Type));
        Assert.Single(p.Variables);
        Assert.Equal([2, 0, 0, 0, 0, 0], linked.Image.Workers.Select(w => (int)w.Type));
    }

    [Fact]
    public void AHelperRunsOnTheCallersWorkerWithReturnAndBranches() {
        (MiniVm vm, _) = Run(Replaced("""
                    Out = Clamp(-5) + Clamp(50) * 100 + Clamp(7) * 10000;
            """, """
                static int Clamp(int v)
                {
                    if (v < 0) return 0;
                    if (v > 9) { return 9; }
                    int r = v;
                    return r;
                }
            """));
        Assert.Equal(0 + 900 + 70000, vm.Var(Out).AsInt);
    }

    [Fact]
    public void ACallersLocalsAvoidTheHelpersRegisters() {
        (MiniVm vm, Linked linked) = Run(Replaced("""
                    int a = 5;
                    int b = Twice(a);
                    float f = 1.5f;
                    float g = Scale(f);
                    Hp = a + b;
                    Speed = f + g;
            """, """
                static int Twice(int x) { int y = x * 2; return y; }
                static float Scale(float x) { float y = x * 3f; return y; }
            """));
        Assert.Equal(15, vm.Var(Hp).AsInt);
        Assert.Equal(6f, vm.Var(Speed).AsFloat);
        // The helpers do not wait, so a and f stay in registers across their calls.
        Assert.True(linked.Mod.Package.Variables.Count == 0, string.Join(", ", linked.Mod.Package.Variables.Select(x => x.Name)));
    }

    [Fact]
    public void LocalsAcrossAWaitingHelperGoToHiddenStorage() {
        (MiniVm vm, Linked linked) = Run(Replaced("""
                    int a = 5;
                    Pause(2);
                    Hp = a;
            """, """
                static void Pause(int frames)
                {
                    int left = frames;
                    Common.WaitFrames(1);
                    Out = left;
                }
            """));
        Assert.Equal(5, vm.Var(Hp).AsInt);
        Assert.Equal(2, vm.Var(Out).AsInt);
        // a (across the call of a helper that waits) and the helper's left (across its wait).
        Assert.Equal(2, linked.Mod.Package.Variables.Count);
    }

    [Fact]
    public void HelpersOfTheScriptClassAndOfNewWorkers() {
        (MiniVm vm, Linked linked) = Run($$"""
            using Ffx;
            using Ffx.Atel;
            using static Monsters.M996.M996;

            namespace Monsters.M996;

            public static partial class M996
            {
                public static int Square(int x) => x * x;
            }

            public sealed partial class Larva
            {
                [Replace(nameof(Main))]
                void Test()
                {
                    Hp = Square(4) + Spark.Cube(2);
                    Out = M996.Square(3);
                }
            }

            public sealed class Spark : Worker2
            {
                protected override void Init() { Hp = Cube(3); }
                protected override void Main() { return; }
                public static int Cube(int x) => Square(x) * x;
            }
            """);
        Assert.Equal(16 + 8, vm.Var(Hp).AsInt);
        Assert.Equal(9, vm.Var(Out).AsInt);
        Assert.Equal(["Spark", "<helper>M996.Square", "<helper>Spark.Cube"], linked.Mod.Package.Workers.Select(w => w.Name));
        MiniVm spark = new(linked.Image);
        spark.Run(1, 0);
        Assert.Equal(27, spark.Var(Hp).AsInt);
    }

    [Fact]
    public void AHelperCallsAFunctionWorkerAndUsesTheWorkersRegisters() {
        (MiniVm vm, _) = Run(Replaced("""
                    I0 = 3;
                    Hp = Bump();
            """, """
                static int Bump()
                {
                    int y = I0 + 1;
                    Fn.Run();
                    return y;
                }
            """));
        Assert.Equal(4, vm.Var(Hp).AsInt);
        Assert.Equal(9, vm.Register(false, 1).AsInt);
    }

    [Fact]
    public void ThreeLevelsCompileAFourthIsAnError() {
        Run(Replaced("Hp = A(1);", """
                static int A(int x) => B(x) + 1;
                static int B(int x) => C(x) + 1;
                static int C(int x) => x + 1;
            """));
        string deep = Replaced("Hp = A(1);", """
                static int A(int x) => B(x) + 1;
                static int B(int x) => C(x) + 1;
                static int C(int x) => D(x) + 1;
                static int D(int x) => x;
            """);
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.CompileMod(
            [ScriptCompiler.Parse(Vanilla, "Monsters/M996/M996.cs")], [ScriptCompiler.Parse(deep, "mod/Mod.cs")]));
        Assert.Contains("3 return addresses", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecursionIsAnError() {
        string mod = Replaced("Hp = A(1);", """
                static int A(int x) => x > 0 ? B(x - 1) : 0;
                static int B(int x) => A(x);
            """);
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.CompileMod(
            [ScriptCompiler.Parse(Vanilla, "Monsters/M996/M996.cs")], [ScriptCompiler.Parse(mod, "mod/Mod.cs")]));
        Assert.Contains("recursion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScriptCompiledWholeGetsItsHelpersAsFunctionWorkers() {
        string source = Vanilla.Replace("""
                    Hp = 1;
                    return;
                }
            """, """
                    Hp = Twice(Twice(5));
                    return;
                }

                static int Twice(int x) => x + x;
            """, StringComparison.Ordinal);
        Assert.NotEqual(Vanilla, source);
        ScriptImage image = ScriptCompiler.Compile(source, "M996.cs").Image;
        Assert.Equal([2, 0, 0], image.Workers.Select(w => (int)w.Type));
        MiniVm vm = new(image);
        vm.Run(0, 1);
        Assert.Equal(20, vm.Var(Hp).AsInt);
    }
}
