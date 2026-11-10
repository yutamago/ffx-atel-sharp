// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Naming;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>
///     The worker types by name (<c>Actor</c> is <c>Worker1</c>) and the role names the decompiler reads off
///     what a worker's code does (docs/language.md, "Workers"; docs/source-repo.md, "Names").
/// </summary>
public sealed class WorkerRoleTests {
    private const string EventPath = "ffx_ps2/ffx/master/jppc/event/obj/te/test01/test01.ebp";

    /// <summary>
    ///     W00 directs: its Main switches on the story and requests W01's entry. W01 only moves the camera;
    ///     W02 and W03 are lines that change to the same map; W04 is a box that only opens a message; W05
    ///     does nothing and keeps its number. Bases by old and new name alike.
    /// </summary>
    private static byte[] RolesFile() {
        const string source = """
            using Ffx;
            using Ffx.Atel;
            using static Events.Te.Test01.Test01;

            namespace Events.Te.Test01;

            [Event("test01")]
            [Workers(typeof(W00), typeof(W01), typeof(W02), typeof(W03), typeof(W04), typeof(W05))]
            public static partial class Test01
            {
                [Var(Storage.Save, Save.StoryProgress)] public static ushort StoryProgress;
            }

            public sealed class W00 : LogicWorker
            {
                protected override void Init()
                {
                    return;
                }

                protected override async void Main()
                {
                    switch (StoryProgress)
                    {
                        case Story.S00006_Znkd1300:
                            await W01.E2Async();
                            break;
                    }
                    return;
                }
            }

            public sealed class W01 : Worker1
            {
                protected override void Init()
                {
                    return;
                }

                protected override void Main()
                {
                    return;
                }

                [Entry(2)]
                public static async Task E2Async()
                {
                    Cam.RefClearAreas();
                    return;
                }
            }

            public sealed class W02 : LineTrigger
            {
                protected override void Main()
                {
                    return;
                }

                protected override void Init()
                {
                    Common.TriggerSetLine(0f, 0f, 0f, 10f, 0f, 0f);
                    return;
                }

                [Entry(2)]
                void E2()
                {
                    ChangeMap(Map.Kami0100, 1);
                    return;
                }
            }

            public sealed class W03 : Worker2
            {
                protected override void Init()
                {
                    return;
                }

                protected override void Main()
                {
                    return;
                }

                [Entry(2)]
                void E2()
                {
                    ChangeMap(Map.Kami0100, 2);
                    return;
                }
            }

            public sealed class W04 : BoxTrigger
            {
                protected override void Init()
                {
                    return;
                }

                protected override void Main()
                {
                    return;
                }

                [Entry(2)]
                void E2()
                {
                    Common.MsgOpen(0, 0);
                    return;
                }
            }

            public sealed class W05 : Worker5
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
            """;
        SyntaxTree tree = ScriptCompiler.Parse(source, "test01.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        byte[] block = ScriptCompiler.Compile([tree]).Block();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    [Fact]
    public void NamedAndNumberedBasesGiveTheSameType() {
        ScriptFile file = ScriptFile.Parse(RolesFile());
        Assert.Equal([4, 1, 2, 2, 3, 5], file.Script.Workers.Select(w => (int)w.Type));
    }

    [Fact]
    public void WorkersAreNamedAfterTheirRoleAndTypeAndTheBytesStayTheSame() {
        byte[] original = RolesFile();
        ScriptFile file = ScriptFile.Parse(original);
        DecompiledScript d = ScriptDecompiler.Decompile(EventPath, file, overlay: NameOverlay.Empty);
        string[] expected = [
            "typeof(Director), typeof(CameraWorker), typeof(ExitToKami0100_02), typeof(ExitToKami0100_03), typeof(DialogueBox), typeof(W05)",
            "public sealed partial class Director : LogicWorker",
            "await CameraWorker.MoveCameraAsync();",
            "public sealed partial class CameraWorker : Actor",
            "public sealed partial class ExitToKami0100_02 : LineTrigger",
            "public sealed partial class DialogueBox : BoxTrigger",
            "public sealed partial class W05 : LineSensor",
        ];
        foreach (string s in expected) Assert.Contains(s, d.Source, StringComparison.Ordinal);
        Assert.Equal(original, ScriptCompiler.Compile([ScriptCompiler.Parse(d.Source, "test01.cs")]).ApplyTo(file).ToArray());
    }

    [Fact]
    public void OutsideEventsTypeTwoIsABattleWorkerAndNoTriggerRolesApply() {
        ScriptFile file = ScriptFile.Parse(RolesFile());
        DecompiledScript d = ScriptDecompiler.Decompile("ffx_ps2/ffx/master/jppc/battle/mon/_m997/m997.bin", file, overlay: NameOverlay.Empty);
        Assert.Contains("typeof(W00), typeof(CameraWorker), typeof(W02), typeof(W03), typeof(DialogueWorker), typeof(W05)", d.Source, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class W02 : BattleWorker", d.Source, StringComparison.Ordinal);
    }
}
