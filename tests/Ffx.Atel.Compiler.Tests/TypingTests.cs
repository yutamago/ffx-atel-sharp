// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>
///     Phase 5's typing (docs/plan.md, "Fewer <c>AsBool</c>"): flags as <c>bool</c> variables and registers, and a battle
///     actor's properties as members (<c>Actors[Target.Own].Hp</c>, <c>Self.Hp</c>), each the same bytes as the
///     integer and native forms, and written so by the decompiler.
/// </summary>
public sealed class TypingTests {
    private static string Script(string vars, string registers, string body) => $$"""
        using Ffx;
        using Ffx.Atel;
        using static Events.Bs.Test0000.Test0000;

        namespace Events.Bs.Test0000;

        [Event("test0000")]
        [Workers(typeof(Rikku))]
        public static partial class Test0000
        {
        {{vars}}
        }

        public sealed class Rikku : Worker1
        {
        {{registers}}

            protected override void Init() { return; }

            protected override void Main()
            {
        {{body}}
                return;
            }
        }
        """;

    private const string IntVars = """
            [Var(Storage.Shared, 0x0000)] public static byte V0;
            [Var(Storage.Shared, 0x0004)] public static int V1;
            [Var(Storage.Shared, 0x0008)] public static int V2;
        """;

    private const string BoolVars = """
            [Var(Storage.Shared, 0x0000, Stored = typeof(byte))] public static bool V0;
            [Var(Storage.Shared, 0x0004)] public static bool V1;
            [Var(Storage.Shared, 0x0008)] public static int V2;
        """;

    private const string IntFlags = """
                V0 = 1;
                V1 = AsInt(V2 > 3);
                I2 = 0;
                if (AsBool(V0) & !AsBool(V1))
                {
                    V0 = 0;
                    I2 = 1;
                }
                while (!AsBool(I2))
                {
                    V2 = V2 + 1;
                    I2 = AsInt(V2 > 9);
                }
                if (AsBool(V0)) V2 = 2;
                Btl.DebugPrint(V0);
        """;

    private const string BoolFlags = """
                V0 = true;
                V1 = V2 > 3;
                I2 = false;
                if (V0 & !V1)
                {
                    V0 = false;
                    I2 = true;
                }
                while (!I2)
                {
                    V2 = V2 + 1;
                    I2 = V2 > 9;
                }
                if (V0) V2 = 2;
                Btl.DebugPrint(AsInt(V0));
        """;

    private static byte[] Block(string source) {
        SyntaxTree tree = ScriptCompiler.Parse(source, "test0000.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        return ScriptCompiler.Compile([tree]).Block();
    }

    /// <summary>The block as a script file the decompiler reads, decompiled as <paramref name="path"/>.</summary>
    private static (byte[] File, ScriptFile Parsed, string Source) Decompile(byte[] block, string path) {
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        ScriptFile parsed = ScriptFile.Parse(file);
        return (file, parsed, ScriptDecompiler.Decompile(path, parsed).Source);
    }

    private static void CompilesBack(byte[] file, ScriptFile parsed, string source) {
        SyntaxTree back = ScriptCompiler.Parse(source, "back.cs");
        Assert.Empty(ScriptCompiler.Check([back]));
        Assert.Equal(file, ScriptCompiler.Compile([back]).ApplyTo(parsed).ToArray());
    }

    [Fact]
    public void FlagsAreTheIntegersBytes() =>
        Assert.Equal(Block(Script(IntVars, "    [Register(2)] public static int I2;", IntFlags)), Block(Script(BoolVars, "    [Register(2)] public static bool I2;", BoolFlags)));

    [Fact]
    public void TheDecompilerTypesFlagsBool() {
        (byte[] file, ScriptFile parsed, string source) = Decompile(Block(Script(IntVars, "    [Register(2)] public static int I2;", IntFlags)),
            "ffx_ps2/ffx/master/jppc/event/obj/bs/test0000/test0000.ebp");
        foreach (string line in new[] {
            "[Var(Storage.Shared, 0x0000, Stored = typeof(byte))] public static bool V0;", "[Var(Storage.Shared, 0x0004)] public static bool V1;",
            "public static int V2;", "[Register(2)] public static bool I2;", "V0 = true;", "V1 = V2 > 3;", "if (V0 & !V1)", "while (!I2)",
            "Btl.DebugPrint(V0);",
        }) {
            Assert.Contains(line, source, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("AsBool", source, StringComparison.Ordinal);
        CompilesBack(file, parsed, source);
    }

    [Fact]
    public void AVariableReadAsANumberStaysAnInteger() {
        (_, _, string source) = Decompile(Block(Script(IntVars, "", """
                    V0 = 1;
                    if (AsBool(V0)) V2 = 1;
                    V2 = V0 + 1;
            """)), "ffx_ps2/ffx/master/jppc/event/obj/bs/test0000/test0000.ebp");
        Assert.Contains("public static byte V0;", source, StringComparison.Ordinal);
        Assert.Contains("AsBool(V0)", source, StringComparison.Ordinal);
    }

    private const string ActorNatives = """
                Btl.SetActorsProperty(Target.AllPly, ActorProperty.Hp, 0);
                V2 = Btl.GetActorProperty(Target.Own, ActorProperty.Mp);
                Btl.SetActorProperty(ActorProperty.Hp, V2);
                V2 = Btl.GetSelfProperty(ActorProperty.Str) + Btl.GetActorProperty((Target)V2, ActorProperty.Hp);
                Btl.SetActorsProperty((Target)V2, ActorProperty.DeathPattern, 3);
                Btl.SetActorsProperty(Target.Own, (ActorProperty)V2, 1);
        """;

    private const string ActorMembers = """
                Actors[Target.AllPly].Hp = 0;
                V2 = Actors[Target.Own].Mp;
                Self.Hp = V2;
                V2 = Self.Str + Actors[(Target)V2].Hp;
                Actors[(Target)V2].DeathPattern = 3;
                Btl.SetActorsProperty(Target.Own, (ActorProperty)V2, 1);
        """;

    [Fact]
    public void ActorPropertiesAreTheNativesBytes() => Assert.Equal(Block(Script(IntVars, "", ActorNatives)), Block(Script(IntVars, "", ActorMembers)));

    [Fact]
    public void TheDecompilerWritesActorProperties() {
        (byte[] file, ScriptFile parsed, string source) = Decompile(Block(Script(IntVars, "", ActorNatives)), "ffx_ps2/ffx/master/jppc/battle/mon/_m996/m996.bin");
        foreach (string line in ActorMembers.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            Assert.Contains(line, source, StringComparison.Ordinal);
        }
        CompilesBack(file, parsed, source);
    }

    [Fact]
    public void AnActorPropertyTakesOnlyAPlainAssignment() {
        SyntaxTree tree = ScriptCompiler.Parse(Script(IntVars, "", "Actors[Target.Own].Hp += 1;"), "test0000.cs");
        Assert.Contains("plain assignment", Assert.Throws<CompileException>(() => ScriptCompiler.Compile([tree])).Message, StringComparison.Ordinal);
    }

    private const string ShakeNatives = """
                Cam.EyeShakeKind1(7, 0.5f, 30, 4, 2);
                Cam.FocusShakeKind8(1, 0.25f, 10, 2, 0);
                Cam.ScreenShakeKind1(0, 3, 2, 20, 8, 0);
                Cam.ScreenShakeKind5(1, 1, 0.5f, V2, 8, 0);
        """;

    private const string ShakeMembers = """
                Camera.Eye.Shake(ShakeKind.SineFadeOut, 7, 0.5f, 30, 4, 2);
                Camera.Focus.Shake(ShakeKind.Blend, 1, 0.25f, 10, 2, 0);
                Camera.Screen.Shake(ShakeKind.SineFadeOut, 0, 3, 2, 20, 8, 0);
                Camera.Screen.Shake(ShakeKind.JerkyFadeOut, 1, 1, 0.5f, V2, 8, 0);
        """;

    /// <summary>One <c>Shake</c> per side; its kind picks the native, and the bytes are the native's call.</summary>
    [Fact]
    public void AShakesKindPicksItsNative() => Assert.Equal(Block(Script(IntVars, "", ShakeNatives)), Block(Script(IntVars, "", ShakeMembers)));

    [Fact]
    public void TheDecompilerWritesTheShakesKind() {
        (byte[] file, ScriptFile parsed, string source) = Decompile(Block(Script(IntVars, "", ShakeNatives)), "ffx_ps2/ffx/master/jppc/event/obj/bs/test0000/test0000.ebp");
        foreach (string line in ShakeMembers.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            Assert.Contains(line, source, StringComparison.Ordinal);
        }
        CompilesBack(file, parsed, source);
    }

    [Fact]
    public void AShakesKindIsAConstant() {
        SyntaxTree tree = ScriptCompiler.Parse(Script(IntVars, "", "Camera.Eye.Shake((ShakeKind)V2, 7, 0.5f, 30, 4, 2);"), "test0000.cs");
        Assert.Contains("is a constant", Assert.Throws<CompileException>(() => ScriptCompiler.Compile([tree])).Message, StringComparison.Ordinal);
    }
}
