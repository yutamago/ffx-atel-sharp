// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode;
using Ffx.Atel.Decompiler;

namespace Ffx.Atel.Cli.Tests;

/// <summary><c>atel dead</c>: which messages live code names, and the groups (docs/research/dead-code.md).</summary>
public sealed class DeadCommandTests {
    [Fact]
    public void LiveMessagesSkipDeadAndNamedBlocks() {
        const string source = """
                Dialogue.Say(Text[1]);
                if (false)
                {
                    Common.MsgSetText(0, Text[2]);
                    if (V1 == 0)
                    {
                        Common.MsgSetText(0, Text[3]);
                    }
                }
                else
                {
                    Common.MsgSetText(0, Text[4]);
                }
                if (Cut.ZanarkandCrowdFacingReaction)
                {
                    Common.MsgSetText(0, Text[5]);
                }
                Common.MsgSetText(0, Text[6]);
            """;
        Assert.Equal([1, 4, 6], DeadCommand.LiveMessages(source).Order());
    }

    private static DeadBlock Block(string code, int instructions = 3, string worker = "W01") =>
        new(1, worker, [2], 0x10, 0x13, 0x20, false, instructions, 0, "0123456789abcdef", null, code, null);

    [Theory]
    [InlineData("bltz0000", "Common.SpriteHide(24);\n", "blitzball-hud")]
    [InlineData("lchb0806", "Common.MsgClose(5);\n", "blitzball-message")]
    [InlineData("psv_bltz0201", "V1 = 2;\n", "blitzball-data")]
    [InlineData("dome0100", "Common.MapChange(Map.Zkrn0600, 1);\n", "map-change")]
    [InlineData("znkd1300", "if (Calc.FacingWithin(1, 2, 3, 4, 5, 6))\n{\n}\n", "npc-reaction")]
    [InlineData("kino0200", "_ = W00.E12Async();\n", "scene-start")]
    public void GroupsFollowTheScriptAndTheNatives(string script, string code, string group) {
        DeadBlock b = Block(code);
        List<string> natives = [.. System.Text.RegularExpressions.Regex.Matches(code, @"\b([A-Z]\w*\.[A-Z]\w*)\(").Select(m => m.Groups[1].Value)];
        Assert.Equal(group, DeadCommand.Classify(script, ScriptCategory.Event, b, natives));
    }

    [Fact]
    public void AJumpToTheNextInstructionIsEmpty() =>
        Assert.Equal("empty", DeadCommand.Classify("bjyt02_00", ScriptCategory.Battle, Block("", instructions: 0), []));
}
