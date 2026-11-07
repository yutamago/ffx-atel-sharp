// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Naming;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>
///     The handler table of monster and battle files (section 1, <see cref="HandlerTable"/>) and the names the
///     decompiler gives the workers and entries the battle runs through it (docs/research/handlers.md).
/// </summary>
public sealed class HandlerNamingTests {
    private const string MonsterPath = "ffx_ps2/ffx/master/jppc/battle/mon/_m997/m997.bin";

    /// <summary>A table as FFX.exe's <c>Mon_FindHandler</c> reads it: signal → (worker, flag, entry per k).</summary>
    private static byte[] Table(int works, int signals, params (int Signal, int Worker, int Flag, int[] Entries)[] handlers) {
        int records = ((signals + 1) >> 1) * 2 + 2;
        int tables = records + 4 * handlers.Length;
        List<byte> bytes = [(byte)works, (byte)signals];
        bytes.AddRange(Enumerable.Repeat((byte)0xff, signals));
        for (int h = 0; h < handlers.Length; h++) bytes[2 + handlers[h].Signal] = (byte)h;
        while (bytes.Count < tables) bytes.Add(0);
        foreach ((int signal, int worker, int flag, int[] entries) in handlers) {
            int record = records + 4 * Array.FindIndex(handlers, x => x.Signal == signal);
            bytes[record] = (byte)worker;
            bytes[record + 1] = (byte)flag;
            BinaryPrimitives.WriteInt16LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[(record + 2)..], (short)bytes.Count);
            bytes.AddRange(BitConverter.GetBytes((ushort)entries.Length));
            foreach (int e in entries) bytes.AddRange(BitConverter.GetBytes((ushort)e));
        }
        return [.. bytes];
    }

    [Fact]
    public void TheTableGivesEachSignalsWorkerAndItsEntryPerValue() {
        byte[] bytes = Table(3, 0x41, (0x00, 2, 0, [0xffff, 3]), (0x3d, 1, 2, [2, 0xffff, 4]));
        HandlerTable table = HandlerTable.Parse(bytes);
        Assert.Equal(3, table.WorkCount);
        Assert.Collection(table.Handlers,
            h => {
                Assert.Equal((0x00, 2, 0), (h.Signal, h.Worker, h.Flag));
                Assert.Equal([null, 3], h.Entries);
            },
            h => {
                Assert.Equal((0x3d, 1, 2), (h.Signal, h.Worker, h.Flag));
                Assert.Equal([2, null, 4], h.Entries);
            });
    }

    [Fact]
    public void ATableThatPointsPastItsEndIsAnError() {
        byte[] bytes = Table(1, 1, (0, 0, 0, [2]));
        Assert.Throws<ScriptFormatException>(() => HandlerTable.Parse(bytes.AsSpan(0, bytes.Length - 2)));
    }

    /// <summary>
    ///     A monster: W00 the camera handler (signal 0, camera 24 → entry 2), W01 the action script (signal 0x3d:
    ///     k 0 → 2, k 3 → 3, k 4 → 4), W02 the motions (0x40) whose entry the overlay names, W03 in no handler.
    /// </summary>
    private static byte[] MonsterFile() {
        const string source = """
            using Ffx;
            using Ffx.Atel;
            using static Monsters.M997.M997;

            namespace Monsters.M997;

            [Monster("m997")]
            [Workers(typeof(W00), typeof(W01), typeof(W02), typeof(W03))]
            public static partial class M997
            {
            }

            public sealed class W00 : BattleWorker
            {
                protected override void Init() { return; }
                protected override void Main() { return; }
                [Entry(2)]
                void E2() { Wait.Frames(24); return; }
            }

            public sealed class W01 : BattleWorker
            {
                protected override void Init() { return; }
                protected override void Main() { return; }
                [Entry(2)]
                void E2() { Wait.Frames(1); return; }
                [Entry(3)]
                void E3() { Wait.Frames(3); return; }
                [Entry(4)]
                public static async Task E4Async() { Wait.Frames(4); return; }
            }

            public sealed class W02 : BattleWorker
            {
                protected override void Init() { return; }
                protected override async void Main() { await W01.E4Async(); return; }
                [Entry(2)]
                void E2() { Wait.Frames(39); return; }
            }

            public sealed class W03 : BattleWorker
            {
                protected override void Init() { return; }
                protected override void Main() { return; }
            }
            """;
        SyntaxTree tree = ScriptCompiler.Parse(source, "m997.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        byte[] block = ScriptCompiler.Compile([tree]).Block();
        int[] none = [.. Enumerable.Repeat(0xffff, 25)];
        int[] cameras = (int[])none.Clone();
        cameras[24] = 2;
        int[] motions = [.. Enumerable.Repeat(0xffff, 40)];
        motions[39] = 2;
        int[] actions = [.. Enumerable.Repeat(0xffff, 21)];
        (actions[0], actions[3], actions[4]) = (2, 3, 4);
        byte[] table = Table(4, 0x41, (0x00, 0, 0, cameras), (0x3d, 1, 2, actions), (0x40, 2, 1, motions));
        int start = 0x40, next = start + (block.Length + 0x3f) / 0x40 * 0x40;
        byte[] file = new byte[next + (table.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)start);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)next);
        block.CopyTo(file, start);
        table.CopyTo(file, next);
        return file;
    }

    [Fact]
    public void HandlersNameTheirWorkersAndEntriesAndTheBytesStayTheSame() {
        byte[] original = MonsterFile();
        ScriptFile file = ScriptFile.Parse(original);
        NameOverlay overlay = NameOverlay.Parse("[entries]\n\"2.2\" = \"Appear\"\n", "m997.toml");
        DecompiledScript d = ScriptDecompiler.Decompile(MonsterPath, file, overlay: overlay);
        string[] expected = [
            "typeof(Cameras), typeof(Ai), typeof(Motions), typeof(W03)",
            "public sealed partial class Cameras : BattleWorker",
            "void OnCamera24()",
            "public sealed partial class Ai : BattleWorker",
            "void OnTurn()",
            "void OnHit()",
            "public static async Task OnDeathAsync()",
            "await Ai.OnDeathAsync();",
            "void Appear()",
            "public sealed partial class W03 : BattleWorker",
        ];
        foreach (string s in expected) Assert.Contains(s, d.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("OnMotion39", d.Source, StringComparison.Ordinal);
        Assert.Equal(original, ScriptCompiler.Compile([ScriptCompiler.Parse(d.Source, "m997.cs")]).ApplyTo(file).ToArray());
    }
}
