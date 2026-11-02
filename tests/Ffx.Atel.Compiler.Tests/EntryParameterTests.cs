// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Linker;
using static Ffx.Atel.Compiler.Tests.NewCodeTests;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>Entries with parameters and a result (docs/plan.md, "Parameters and return values"): hidden fields of the receiving worker.</summary>
public sealed class EntryParameterTests {
    private const int Hp = 0, Speed = 1, Table = 2, Out = 3;

    private const string Mod = """
        using Ffx;
        using Ffx.Atel;
        using static Monsters.M996.M996;

        namespace Monsters.M996;

        public sealed partial class Larva
        {
            public static async Task<int> AddAsync(int a, int b) { return a + b; }

            public static async Task<int> TwiceAsync(int x)
            {
                int kept = x;
                Common.WaitFrames(1);
                return kept * 2;
            }

            public static async Task<float> HalfAsync(float f) => f / 2f;

            public static async Task SetAsync(int v, Priority priority = Priority.P1) { Table[0] = v; }

            public static async Task MarkAsync(int at) { Table[at] = 1; }
        }

        public sealed class Spark : Worker2
        {
            protected override void Init() { return; }

            protected override async void Main()
            {
                Out = await Larva.AddAsync(2, 3);
                Hp = await Larva.TwiceAsync(Out) + await Larva.AddAsync(b: 100, a: 0);
                Speed = await Larva.HalfAsync(3f);
                _ = Larva.SetAsync(7, Priority.P2);
                await Larva.MarkAsync(at: 2).Started;
                return;
            }
        }
        """;

    [Fact]
    public void ParametersAndResultsGoThroughHiddenFields() {
        Linked linked = Link(Mod);
        MiniVm vm = new(linked.Image) { RunRequests = true };
        int spark = linked.Image.Workers.FindIndex(w => w.Type == 2 && w != linked.Image.Workers[0]);
        vm.Run(spark, 1);
        Assert.Equal(5, vm.Var(Out).AsInt);
        Assert.Equal(110, vm.Var(Hp).AsInt);
        Assert.Equal(1.5f, vm.Var(Speed).AsFloat);
        Assert.Equal(7, vm.Var(Table, 0).AsInt);
        Assert.Equal(1, vm.Var(Table, 2).AsInt);
        Assert.Equal([(Opcodes.ReqE0, 1), (Opcodes.ReqE0, 1), (Opcodes.ReqE0, 1), (Opcodes.ReqE0, 1), (Opcodes.Req0, 2), (Opcodes.ReqW0, 1)],
            vm.Requests.Select(r => (r.Op, r.Level)));
        // Hidden fields: a, b, the result of AddAsync; x and the result of TwiceAsync; f and HalfAsync's;
        // v; at; TwiceAsync's kept, which lives across its wait; and in Spark.Main the value of the first
        // await, which lives across the second.
        ScriptPackage p = linked.Mod.Package;
        Assert.Equal(11, p.Variables.Count);
        Assert.Contains(p.Variables, v => v.Name.Contains("Larva.AddAsync.a", StringComparison.Ordinal));
        Assert.Contains(p.Variables, v => v.Name.Contains("Larva.HalfAsync.result", StringComparison.Ordinal) && v.Type == 6);
        // Spark requests Larva's entries, so their fields are shared (storage 3 is the running worker's);
        // the locals are private, one element per level.
        Assert.All(p.Variables.Where(v => v.Name.Contains("Async.", StringComparison.Ordinal)), v => Assert.Equal((4, 1), (v.Storage, v.Length)));
        Assert.All(p.Variables.Where(v => !v.Name.Contains("Async.", StringComparison.Ordinal)), v => Assert.Equal((3, 9), (v.Storage, v.Length)));
        RoundTrips(linked);
    }

    [Fact]
    public void ARequestNamesEveryParameter() {
        string bad = Mod.Replace("await Larva.AddAsync(2, 3)", "await Larva.AddAsync(2)", StringComparison.Ordinal);
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.CompileMod(
            [ScriptCompiler.Parse(Vanilla, "Monsters/M996/M996.cs")], [ScriptCompiler.Parse(bad, "mod/Mod.cs")]));
        Assert.Contains("needs b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAnEntryWithAResultHasAValue() {
        // C# rejects this as well (Task has no result); the compiler says why in ATEL's terms.
        string bad = Mod.Replace("Out = await Larva.AddAsync(2, 3);", "Out = await Larva.SetAsync(1);", StringComparison.Ordinal);
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.CompileMod(
            [ScriptCompiler.Parse(Vanilla, "Monsters/M996/M996.cs")], [ScriptCompiler.Parse(bad, "mod/Mod.cs")]));
        Assert.Contains("has a value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScriptCompiledWholeHasThemToo() {
        string source = Vanilla.Replace("""
                    Hp = 1;
                    return;
                }
            """, """
                    Hp = 1;
                    return;
                }

                [Entry(2)]
                public static async Task<int> E2Async(int x) { return x + 1; }

                [Entry(3)]
                async void E3() { Out = await Larva.E2Async(41); return; }
            """, StringComparison.Ordinal);
        Assert.NotEqual(Vanilla, source);
        ScriptImage image = ScriptCompiler.Compile(source, "M996.cs").Image;
        Assert.Equal(6, image.Variables.Count);
        // Only Larva requests E2Async: its fields are Larva's private variables, plain (both levels share them).
        Assert.All(image.Variables.Skip(4), v => Assert.Equal((3, 1), (v.Storage, (int)v.Length)));
        Assert.Equal(8u, image.Workers[0].PrivateLength);
        MiniVm vm = new(image) { RunRequests = true };
        vm.Run(0, 3);
        Assert.Equal(42, vm.Var(Out).AsInt);
    }
}
