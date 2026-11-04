// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>
///     <c>Wait.Seconds(s)</c> (docs/language.md, "Waiting in seconds"): <c>Wait.Frames(n)</c> with n = s × 30 rounded to
///     the nearest frame, halves away from zero, counted from a constant when the script is compiled.
/// </summary>
public sealed class WaitSecondsTests {
    private const string Head = """
        using Ffx;
        using Ffx.Atel;
        using static Events.Bs.Test0000.Test0000;

        namespace Events.Bs.Test0000;

        [Event("test0000")]
        [Workers(typeof(Tidus))]
        public static partial class Test0000
        {
            [Var(Storage.Shared, 0x0004)] public static float V1;
        }

        public sealed class Tidus : Worker1
        {
            protected override void Init() { return; }

            protected override void Main()
            {

        """;

    private const string Tail = """

                return;
            }
        }
        """;

    private static SyntaxTree Tree(string body) => ScriptCompiler.Parse(Head + body + Tail, "test0000.cs");

    private static byte[] Block(string body) {
        SyntaxTree tree = Tree(body);
        Assert.Empty(ScriptCompiler.Check([tree]));
        return ScriptCompiler.Compile([tree]).Block();
    }

    [Theory]
    [InlineData("Wait.Seconds(1.23f);", 37)] // 36.9 frames
    [InlineData("Wait.Seconds(1f);", 30)]
    [InlineData("Wait.Seconds(2);", 60)]
    [InlineData("Wait.Seconds(0.25f);", 8)] // 7.5: a half goes up
    [InlineData("Wait.Seconds(0.05f);", 2)] // 0.05f is 0.0500000007: 1.5000000224 frames
    [InlineData("Wait.Seconds(1f / 30f);", 1)]
    [InlineData("Wait.Seconds(0.5f * 3);", 45)]
    [InlineData("Wait.Seconds(3 / 2);", 30)] // int division, as C# has it: 1 s
    [InlineData("Wait.Seconds(-(-2.5f));", 75)]
    [InlineData("const float Pause = 1.5f; Wait.Seconds(Pause);", 45)]
    [InlineData("Ffx.Wait.Seconds(seconds: 10f);", 300)]
    public void ItIsWaitFramesOfTheRoundedFrames(string seconds, int frames) =>
        Assert.Equal(Block($"Wait.Frames({frames});"), Block(seconds));

    [Fact]
    public void TheRateIsThirtyFramesASecond() => Assert.Equal(30, Wait.FramesPerSecond);

    [Theory]
    [InlineData("Wait.Seconds(V1);", "constant")]
    [InlineData("float s = 2f; Wait.Seconds(s);", "constant")]
    [InlineData("Wait.Seconds(Common.GetYaw());", "constant")]
    [InlineData("Wait.Seconds(0.01f);", "shortest wait is one frame")] // 0.3 frames
    [InlineData("Wait.Seconds(0f);", "shortest wait is one frame")]
    [InlineData("Wait.Seconds(-1f);", "shortest wait is one frame")]
    [InlineData("Wait.Seconds(1e30f);", "the longest")]
    public void ItTakesAConstantOfAFrameOrMore(string body, string message) =>
        Assert.Contains(message, Assert.Throws<CompileException>(() => ScriptCompiler.Compile([Tree(body)])).Message, StringComparison.Ordinal);

    [Fact]
    public void TheAnalyzersSayTheSame() {
        Assert.Contains(ScriptCompiler.Check([Tree("Wait.Seconds(V1);")]), d => d.Id == "ATL3005");
        Assert.Contains(ScriptCompiler.Check([Tree("Wait.Seconds(0.01f);")]), d => d.Id == "ATL3006");
    }

    [Fact]
    public void AnInlineMethodMayWaitInSeconds() {
        const string helper = """
            [Inline(Waits = true)]
            static void Pause() { Wait.Seconds(0.5f); Common.SetYaw(1f); }

            """;
        string Source(string body) => (Head + body + Tail).Replace("    protected override void Main()", helper + "    protected override void Main()", StringComparison.Ordinal);
        byte[] Compile(string body) {
            SyntaxTree tree = ScriptCompiler.Parse(Source(body), "test0000.cs");
            Assert.Empty(ScriptCompiler.Check([tree]));
            return ScriptCompiler.Compile([tree]).Block();
        }
        Assert.Equal(Compile("Wait.Frames(15); Common.SetYaw(1f);"), Compile("Pause();"));
    }
}
