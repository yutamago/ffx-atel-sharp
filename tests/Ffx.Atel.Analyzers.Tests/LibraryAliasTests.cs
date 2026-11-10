// SPDX-License-Identifier: MIT

using static Ffx.Atel.Analyzers.Tests.Harness;

namespace Ffx.Atel.Analyzers.Tests;

/// <summary>ATL3007: a library class named as a worker where none or several of the script's workers derive from it.</summary>
public sealed class LibraryAliasTests {
    private const string Library = """
        namespace Library
        {
            public abstract class SetDefaults : FunctionWorker
            {
                public static void Run() { return; }
            }
            public abstract class Idle : Worker1
            {
                public static void Run2() { return; }
            }
            public abstract class Unused : FunctionWorker
            {
                public static void Run() { return; }
            }
        }

        """;

    [Fact]
    public void OneDerivingWorkerIsFine() => Verify(Library + """
        namespace Monsters.M997
        {
            public sealed class Fn : Library.SetDefaults { }
            public sealed class W01 : Worker1
            {
                [Entry(0)] void E0() { Library.SetDefaults.Run(); Call<Library.SetDefaults>(); }
            }
        }
        """);

    [Fact]
    public void NoneOrSeveralIsAnError() => Verify(Library + """
        namespace Monsters.M997
        {
            public sealed class A : Library.Idle { }
            public sealed class B : Library.Idle { }
            public sealed class W02 : Worker1
            {
                [Entry(0)] void E0() { {|ATL3007:Unused|}.Run(); Call<{|ATL3007:Unused|}>(); {|ATL3007:Idle|}.Run2(); }
            }
        }
        """, "using Library;\n");

    [Fact]
    public void TheLibrarysOwnCodeIsNotChecked() => Verify(Library + """
        namespace Library
        {
            public abstract class Caller : Worker1
            {
                [Entry(0)] void E0() { Unused.Run(); }
            }
        }
        """);
}
