// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>
///     The object-oriented surface (docs/language.md, "Members"): every member compiles to its native's call, byte for
///     byte, and the decompiler writes the member.
/// </summary>
public sealed class MemberTests {
    private const string Head = """
        using Ffx;
        using Ffx.Atel;
        using static Events.Bs.Test0000.Test0000;

        namespace Events.Bs.Test0000;

        [Event("test0000")]
        [Workers(typeof(Rikku), typeof(Tidus))]
        public static partial class Test0000
        {
            [Var(Storage.Shared, 0x0000)] public static int V0;
            [Var(Storage.Shared, 0x0004)] public static float V1;
        }

        public sealed class Tidus : Worker1
        {
            protected override void Init() { return; }
            protected override void Main() { return; }
        }

        public sealed class Rikku : Worker1
        {
            [Register(3)] public static int I3;

            protected override void Init() { return; }

            protected override void Main()
            {

        """;

    private const string Tail = """

                return;
            }
        }
        """;

    // The user's example (bsvr0000, Rikku.E8Async), as the natives and as members.
    private const string Natives = """
        Common.SetPos(4.623341f, -19.153864f, 587.17316f);
        Common.WorkerStopMove(Common.GetWorkerId(-1));
        Common.SetYaw(-0.307183f);
        Chr.SetNeckSpeed(20);
        Chr.SetNeckTarget(10);
        Common.WaitFrames(95);
        Common.TurnSetYaw(1.572062f);
        I3 = Common.GetTurnKind();
        Chr.SetMotion(Common.GetTurnMotion(I3));
        Common.TurnSetFrames(0f, Common.GetTurnFrames(I3, 15, 30));
        Common.TurnStart(0, 4099, 0);
        Common.TurnWait();
        Chr.ReleaseMotion();
        Chr.PlayMotion(0x71050, 0, 1, 0, -1);
        Chr.WaitMotion();
        V1 = Common.GetWorkerX(1) + Common.GetWorkerY(Common.GetWorkerId(-1)) + Common.GetWorkerZ(V0);
        Chr.SetAutoTalkOf(1, 1);
        Chr.SetAutoTalkOf(V0, 0);
        Chr.SetAutoTalk(1);
        Chr.Hide(AsInt(V0 > 2));
        V1 = Common.GetYaw() + Common.GetWorkerYaw(1);
        Common.WorkerStopTurn(1);
        Common.SpriteShow(3, V0);
        Common.SpriteSetColor(V0 + 1, 255, 128, 0);
        Cam.SetRoll(5f);
        Common.SetModel(Model.Rikk);
        Common.SfxPlay((Sfx)0x80000017);
        Common.SfxPlayEx((Sfx)0x80000017, 64, 127);
        Common.MoveSetTarget(1f, 2f, 3f);
        """;

    private const string Members = """
        Pos = new Vector3(4.623341f, -19.153864f, 587.17316f);
        Move.Stop();
        Yaw = -0.307183f;
        Neck.Speed = 20;
        Neck.Target = 10;
        Wait.Frames(95);
        Turn.Yaw = 1.572062f;
        I3 = Turn.GetKind();
        Motion = Turn.GetMotion(I3);
        Turn.SetFrames(0f, Turn.GetFrames(I3, 15, 30));
        Turn.Start(0, 4099, 0);
        Turn.Wait();
        Motion.Release();
        Motion.Play(0x71050, 0, 1, 0, -1);
        Motion.Wait();
        V1 = Tidus.Pos.X + Pos.Y + Workers[V0].Pos.Z;
        Tidus.AutoTalk = true;
        Workers[V0].AutoTalk = false;
        AutoTalk = true;
        Hidden = V0 > 2;
        V1 = Yaw + Tidus.Yaw;
        Tidus.Turn.Stop();
        Sprites[3].Show(V0);
        Sprites[V0 + 1].SetColor(255, 128, 0);
        Camera.Roll = 5f;
        Model = Model.Rikk;
        Sound.Play((Sfx)0x80000017);
        Sound.Play((Sfx)0x80000017, 64, 127);
        Move.Target = new(1f, 2f, 3f);
        """;

    private static byte[] Block(string body) {
        SyntaxTree tree = ScriptCompiler.Parse(Head + body + Tail, "test0000.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        return ScriptCompiler.Compile([tree]).Block();
    }

    [Fact]
    public void MembersCompileToTheNativesBytes() => Assert.Equal(Block(Natives), Block(Members));

    [Fact]
    public void TheDecompilerWritesTheMembersAndTheyCompileBack() {
        byte[] blockBytes = Block(Natives);
        byte[] file = new byte[0x40 + (blockBytes.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        blockBytes.CopyTo(file, 0x40);
        ScriptFile parsed = ScriptFile.Parse(file);
        DecompiledScript d = ScriptDecompiler.Decompile("ffx_ps2/ffx/master/jppc/battle/mon/_m996/m996.bin", parsed);
        string source = d.Source;
        foreach (string line in new[] {
            "Pos = new Vector3(4.623341f, -19.153864f, 587.17316f);", "Move.Stop();", "Yaw = -0.307183f;", "Neck.Speed = 20;",
            // The turn (TurnSetYaw … ReleaseMotion) is the reference assembly's inline Turn.To.
            "Neck.Target = 10;", "Wait.Frames(95);", "Turn.To(1.572062f);",
            "Motion.Play(0x71050, 0, 1, 0, -1);", "Motion.Wait();", ".Pos.X + Pos.Y + Workers[V0].Pos.Z;", ".AutoTalk = true;",
            "Workers[V0].AutoTalk = false;", "AutoTalk = true;", "Hidden = V0 > 2;", "Sprites[3].Show(V0);",
            "Sprites[V0 + 1].SetColor(255, 128, 0);", "Camera.Roll = 5f;", "Model = Model.Rikk;", "Sound.Play((Sfx)0x80000017, 64, 127);",
            "Move.Target = new Vector3(1f, 2f, 3f);", ".Turn.Stop();",
        }) {
            Assert.Contains(line, source, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("Common.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Chr.", source, StringComparison.Ordinal);
        SyntaxTree back = ScriptCompiler.Parse(source, "m996.cs");
        Assert.Empty(ScriptCompiler.Check([back]));
        Assert.Equal(file, ScriptCompiler.Compile([back]).ApplyTo(parsed).ToArray());
    }

    [Fact]
    public void AWorkerNativeWithANativeOfItsOwnForThisWorkerStaysANative() {
        // GetWorkerYaw(GetWorkerId(-1)) is not Yaw (that is GetYaw): it stays the native's call.
        byte[] a = Block("V1 = Common.GetWorkerYaw(Common.GetWorkerId(-1));");
        byte[] b = Block("V1 = Yaw;");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MembersAreCheckedAsTheNativesAre() {
        SyntaxTree tree = ScriptCompiler.Parse(Head + "Pos = V1;" + Tail, "test0000.cs");
        Assert.Contains("Vector3", Assert.Throws<CompileException>(() => ScriptCompiler.Compile([tree])).Message, StringComparison.Ordinal);
        tree = ScriptCompiler.Parse(Head + "Tidus.Pos = new Vector3(1f, 2f, 3f);" + Tail, "test0000.cs");
        Assert.Contains("this worker only", Assert.Throws<CompileException>(() => ScriptCompiler.Compile([tree])).Message, StringComparison.Ordinal);
    }
}

/// <summary>The members of the reference assembly against the natives they stand for.</summary>
public sealed class NativeMembersTests {
    [Fact]
    public void EveryMemberFitsItsNative() {
        NativeMembers members = NativeMembers.Default;
        Assert.True(members.All.Count() >= 80);
        foreach (MemberNative m in members.All) {
            Bytecode.Analysis.NativeSignature sig = ScriptCompiler.Natives[m.Id] ?? throw new Xunit.Sdk.XunitException($"{m.Path}: no native 0x{m.Id:x4}");
            int values = NativeMembers.ValueCount(m, sig);
            Assert.True(m.Polls == sig.Polls, $"{m.Path}: Polls");
            switch (m.Use) {
                case MemberUse.Get: Assert.True(values == 0, $"{m.Path}: a getter takes nothing"); break;
                case MemberUse.Set: Assert.True(values == (m.Vector ? 3 : 1), $"{m.Path}: a setter takes its value"); break;
            }
            if (m.Receiver != MemberReceiver.None) Assert.True(sig.ArgKinds[0] == 'i', $"{m.Path}: the receiver is an int");
            Assert.Same(m, members[m.Id]);
            Assert.Contains(m, members.Find(m.Path, m.Use));
        }
        Assert.Contains("Wait", members.StaticRoots);
        Assert.Contains("Turn", members.WorkerRoots);
        Assert.Contains("Sprites", members.IndexedRoots);
    }

    /// <summary>An inline helper may be written with members: it expands to the natives' calls.</summary>
    [Fact]
    public void InlineMethodsMayUseMembers() {
        const string head = """
            using Ffx;
            using Ffx.Atel;
            using static Events.Bs.Test0001.Test0001;

            namespace Events.Bs.Test0001;

            [Event("test0001")]
            [Workers(typeof(Rikku))]
            public static partial class Test0001
            {
            }

            public sealed class Rikku : Worker1
            {
                [Inline(Waits = true)]
                static void TurnTo(float yaw, int frames)
                {
                    Turn.Yaw = yaw;
                    Turn.SetFrames(0f, frames);
                    Turn.Start(0, 4099, 0);
                    Turn.Wait();
                }

                protected override void Init() { return; }

                protected override void Main()
                {

            """;
        static byte[] Block(string source) {
            SyntaxTree tree = ScriptCompiler.Parse(source, "test0001.cs");
            Assert.Empty(ScriptCompiler.Check([tree]));
            return ScriptCompiler.Compile([tree]).Block();
        }
        byte[] inline = Block(head + "TurnTo(1.5f, 30);\nreturn;\n}\n}\n");
        byte[] natives = Block(head + "Common.TurnSetYaw(1.5f);\nCommon.TurnSetFrames(0f, 30);\nCommon.TurnStart(0, 4099, 0);\nCommon.TurnWait();\nreturn;\n}\n}\n");
        Assert.Equal(natives, inline);
    }
}
