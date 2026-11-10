// SPDX-License-Identifier: MIT

using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>
///     A library class's name stands for the one worker of the script deriving from it (docs/plan.md, Phase 6,
///     "Bodies that name a sibling"): in vanilla source, as the decompiler writes shared bodies, and in a mod's
///     own code (<c>atel compile-mod</c>, which the SDK runs).
/// </summary>
public sealed class LibraryAliasTests {
    private const string Library = """
        using Ffx;
        using Ffx.Atel;

        namespace Library;

        public abstract class SetDefaults : FunctionWorker
        {
            public static void Run()
            {
                MoveSpeed = 16f;
                return;
            }
        }

        public abstract class Idle : BattleWorker
        {
            protected override void Init()
            {
                return;
            }

            protected override void Main()
            {
                return;
            }
        }

        public abstract class Unused : FunctionWorker
        {
            public static void Run()
            {
                return;
            }
        }
        """;

    private static string Vanilla(string call) => $$"""
        using Ffx;
        using Ffx.Atel;
        using Library;
        using static Monsters.M997.M997;

        namespace Monsters.M997;

        [Monster("m997")]
        [Workers(typeof(Larva), typeof(Fn), typeof(A), typeof(B))]
        [Header(Author = "test", Name = "997")]
        [SharedData(0x40, "")]
        [OtherData(0x40, "")]
        [EventData(0x10, "")]
        public static partial class M997
        {
            [Var(Storage.Shared, 0x0000)] public static int Hp;
        }

        public sealed partial class Larva : BattleWorker
        {
            protected override void Init()
            {
                {{call}}
                Hp = 5;
                return;
            }

            protected override void Main()
            {
                return;
            }
        }

        public sealed partial class Fn : SetDefaults
        {
        }

        public sealed partial class A : Idle
        {
        }

        public sealed partial class B : Idle
        {
        }
        """;

    private static string Mod(string call) => $$"""
        using Ffx;
        using Ffx.Atel;
        using Library;
        using static Monsters.M997.M997;

        namespace Monsters.M997;

        public sealed partial class Larva
        {
            [Postfix(nameof(Init))]
            void Defaults()
            {
                {{call}}
                Hp = Hp + 1;
            }
        }
        """;

    private static readonly ScriptLibrary Lib = ScriptLibrary.FromTrees([ScriptCompiler.Parse(Library, "Library/Library.cs")]);

    private static List<SyntaxTree> VanillaTrees(string call = "Fn.Run();") => [ScriptCompiler.Parse(Vanilla(call), "Monsters/M997/M997.cs")];

    private static CompiledMod CompileMod(string call) =>
        ScriptCompiler.CompileMod(VanillaTrees(), [ScriptCompiler.Parse(Mod(call), "mod/Larva.cs")], Lib);

    [Fact]
    public void TheSourceIsCSharp() {
        Assert.Empty(Lib.Check());
        Assert.Empty(ScriptCompiler.Check([.. VanillaTrees("SetDefaults.Run();"), ScriptCompiler.Parse(Mod("SetDefaults.Run();"), "mod/Larva.cs")], Lib));
    }

    [Fact]
    public void VanillaCodeCallsTheWorkerDerivingFromALibraryClass() {
        byte[] byWorker = ScriptCompiler.Compile(VanillaTrees("Fn.Run();"), Lib).Block();
        byte[] byClass = ScriptCompiler.Compile(VanillaTrees("SetDefaults.Run();"), Lib).Block();
        Assert.Equal(byWorker, byClass);
        Assert.Equal(byWorker, ScriptCompiler.Compile(VanillaTrees("Call<SetDefaults>();"), Lib).Block());
    }

    [Fact]
    public void AModCallsASiblingThroughItsLibraryClass() {
        ScriptPackage byWorker = CompileMod("Fn.Run();").Package, byClass = CompileMod("SetDefaults.Run();").Package;
        Assert.Equal(byWorker.ToJson(), byClass.ToJson());
        // The postfix calls Fn, worker 1, which the package lists as the vanilla worker it uses.
        Assert.Contains(byClass.Vanilla.Workers, w => w.Index == 1);
        Assert.Equal(byWorker.ToJson(), CompileMod("Call<SetDefaults>();").Package.ToJson());
    }

    [Theory]
    [InlineData("Idle.Run();", "Idle is a library class 2 workers of this script derive from (A, B)")]
    [InlineData("Call<Idle>();", "Idle is a library class 2 workers of this script derive from (A, B)")]
    [InlineData("Unused.Run();", "Unused is a library class no worker of this script derives from")]
    [InlineData("WaitForEntry<Unused>(2);", "Unused is a library class no worker of this script derives from")]
    public void ALibraryClassWithNoOrSeveralDerivingWorkersNamesNone(string call, string message) {
        CompileException mod = Assert.Throws<CompileException>(() => CompileMod(call));
        Assert.Contains(message, mod.Message, StringComparison.Ordinal);
        CompileException vanilla = Assert.Throws<CompileException>(() => ScriptCompiler.Compile(VanillaTrees(call), Lib));
        Assert.Contains(message, vanilla.Message, StringComparison.Ordinal);
    }
}
