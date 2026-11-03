// SPDX-License-Identifier: MIT

using Ffx.Atel.Compiler;

namespace Ffx.Atel.Cli.Tests;

/// <summary><c>atel compile-mod</c>: the dead blocks a mod switches on (docs/research/dead-code.md, "Switching a block on").</summary>
public sealed class ModCommandTests {
    [Fact]
    public void EnableCutAttributesNameTheBlocks() {
        const string source = """
            using Ffx;
            using Ffx.Atel;

            [assembly: EnableCut(nameof(Cut.ZanarkandCrowdFacingReaction))]
            [assembly: Ffx.Atel.EnableCutAttribute(nameof(Ffx.Cut.HighroadBattleLine335Tidus)), EnableCut("HighroadBattleLine336AuronTidus")]
            [assembly: System.Reflection.AssemblyTitle("not a cut")]

            namespace Events.Znkd1300;
            """;
        Assert.Equal(["ZanarkandCrowdFacingReaction", "HighroadBattleLine335Tidus", "HighroadBattleLine336AuronTidus"],
            ModCommands.EnabledCuts(ScriptCompiler.Parse(source, "Cuts.cs"), "Cuts.cs"));
    }

    [Fact]
    public void AnEnableCutWithoutOneNameStops() {
        const string source = "[assembly: Ffx.Atel.EnableCut(\"A\", \"B\")]\n";
        ArgumentException ex = Assert.Throws<ArgumentException>(() => ModCommands.EnabledCuts(ScriptCompiler.Parse(source, "Cuts.cs"), "Cuts.cs").ToList());
        Assert.Contains("EnableCut takes one name", ex.Message, StringComparison.Ordinal);
    }
}
