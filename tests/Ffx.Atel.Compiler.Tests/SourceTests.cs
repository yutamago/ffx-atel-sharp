// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>Decompiling and compiling without the game: a made-up script and hand-written source.</summary>
public sealed class SourceTests {
    internal const string MonsterPath = "ffx_ps2/ffx/master/jppc/battle/mon/_m999/m999.bin";

    /// <summary>A small monster file with one of everything the source has to carry.</summary>
    internal static byte[] MadeUpFile() {
        ScriptImage image = new() {
            Header14 = 3, Header18 = 0xffff, Header24 = 0x8000_0001,
            Author = "test"u8.ToArray(), Name = "999"u8.ToArray(),
            Shared = new byte[0x40], Other = new byte[0x40], Event = new byte[0x10],
        };
        image.Shared[5] = 7;
        image.Other[0x3f] = 0xff;
        image.Variables.Add(new VarEntry(0x5600_0010, 1, 0));    // i32, private, 0x10
        image.Variables.Add(new VarEntry(0x0900_0004, 4, 2));    // u8[4], shared, 0x04, bit 24, extra 2
        image.IntConstants.AddRange([0x8000_0000, 12345]);
        image.FloatConstants.AddRange([BitConverter.SingleToUInt32Bits(35f), 0x7fc0_0001]);    // a NaN payload

        WorkerAssembler w0 = new();
        w0.PlaceEntry(0);
        w0.Add("end");
        AssembledWorker a0 = w0.Finish();
        image.Workers.Add(new WorkerImage { Type = 0, Code = a0.Code, Entries = a0.Entries, Labels = a0.Labels });

        WorkerAssembler w1 = new();
        w1.PlaceEntry(0);
        w1.PlaceEntry(2);    // entries 0 and 2 share code; entry 1 is inside what entry 0 runs into
        w1.Add("""
            nop
            pushc c0
        l0:
        e1:
            popa_jz l0
            pushfc f1
            end
        """);
        AssembledWorker a1 = w1.Finish();
        byte[] priv = new byte[16];
        priv[3] = 9;
        image.Workers.Add(new WorkerImage { Type = 2, PrivateLength = 0x10, Private = priv, Code = a1.Code, Entries = a1.Entries, Labels = a1.Labels });

        byte[] block = image.Write();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    [Fact]
    public void AMadeUpScriptRoundTrips() {
        byte[] original = MadeUpFile();
        ScriptFile file = ScriptFile.Parse(original);
        DecompiledScript d = ScriptDecompiler.Decompile(MonsterPath, file);
        Assert.Equal("Monsters.M999", d.Names.Namespace);
        Assert.Contains("[Entry(0)][Entry(2)]", d.Source, StringComparison.Ordinal);
        Assert.Contains("[FloatBits(", d.Source, StringComparison.Ordinal);
        Assert.Contains("public static byte[,] V1 = new byte[2, 2];", d.Source, StringComparison.Ordinal);

        SyntaxTree tree = ScriptCompiler.Parse(d.Source, "m999.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        CompiledScript compiled = ScriptCompiler.Compile([tree]);
        Assert.Equal((Bytecode.ScriptCategory.Monster, "m999"), (compiled.Category, compiled.Name));
        Assert.Equal(original, compiled.ApplyTo(file).ToArray());
    }

    /// <summary>A monster whose code is one of each statement Phase 3 writes as C#.</summary>
    internal static byte[] StatementsFile() {
        ScriptImage image = new() {
            Author = "test"u8.ToArray(), Name = "998"u8.ToArray(), Shared = new byte[0x20], Other = new byte[0x40], Event = new byte[0x10],
        };
        image.Variables.Add(new VarEntry(0x5600_0010, 1, 0));                          // V0: int, private 0x10
        image.Variables.Add(new VarEntry(0x0800_0004 | VarEntry.ArrayBit, 4, 2));      // V1: byte[2, 2], shared 0x04
        image.Variables.Add(new VarEntry(0x1600_0014, 1, 0));                          // V2: sbyte, private 0x14
        image.IntConstants.Add(70000);
        image.FloatConstants.Add(BitConverter.SingleToUInt32Bits(2.5f));

        WorkerAssembler w0 = new();
        w0.PlaceEntry(0);
        w0.Add("ret");
        AssembledWorker a0 = w0.Finish();
        image.Workers.Add(new WorkerImage { Type = 0, Code = a0.Code, Entries = a0.Entries, Labels = a0.Labels });

        WorkerAssembler w1 = new();
        w1.PlaceEntry(0);
        w1.Add("""
                pushimm 5
                popv v0
                pushv v0
                pushimm 3
                add
                popv v0
                pushimm 3
                op_2b
                pushva v1
                pushimm -65
                and
                popva v1
                pushimm 300
                popv v2
                pushc c0
                popv v0
                pushfc f0
                func_facc 0x0016
                pushv v0
                pushimm 2
                gt
                pushv v2
                land
                popa_jz l0
                pushimm 1
                pushimm 0
                pushimm 0
                req0
                popfacc
                pushi1
                popf0
                call w0
            l0:
                end
            """);
        AssembledWorker a1 = w1.Finish();
        image.Workers.Add(new WorkerImage { Type = 2, Code = a1.Code, Entries = a1.Entries, Labels = a1.Labels });

        byte[] block = image.Write();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    [Fact]
    public void StatementsRoundTripAsCSharp() {
        byte[] original = StatementsFile();
        ScriptFile file = ScriptFile.Parse(original);
        DecompiledScript d = ScriptDecompiler.Decompile("ffx_ps2/ffx/master/jppc/battle/mon/_m998/m998.bin", file);
        string[] expected = [
            "V0 = 5;",
            "V0 += 3;",
            "V1[1, 1] &= unchecked((byte)-65);",
            "V2 = unchecked((sbyte)300);",
            "V0 = 0x11170;",
            "Move.Speed = 2.5f;",
            "if (V0 > 2 & AsBool(V2))",
            "Request(1, 0, 0);",
            "F0 = I1;",
            "W00.Run();",
            "public static void Run()",
            "return;",
            "[Register(1)] public static int I1;",
        ];
        foreach (string line in expected) Assert.Contains(line, d.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("Asm(", d.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("[Ints(", d.Source, StringComparison.Ordinal);    // the pools follow the rules
        Assert.Equal((12, 0), (d.Stats.Statements, d.Stats.AsmInstructions));

        SyntaxTree tree = ScriptCompiler.Parse(d.Source, "m998.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        Assert.Equal(original, ScriptCompiler.Compile([tree]).ApplyTo(file).ToArray());
    }

    /// <summary>
    ///     A <c>u32</c> variable (type 4; no shipped script declares one, a mod's field may) next to the other types
    ///     (issue #74): read as an operand it is <c>(int)x</c>, stored from another type it is cast, and the source
    ///     compiles back to the same bytes.
    /// </summary>
    [Fact]
    public void UintVariablesAreCastWhereCSharpNeedsIt() {
        ScriptImage image = new() {
            Author = "test"u8.ToArray(), Name = "996"u8.ToArray(), Shared = new byte[0x20], Other = new byte[0x40], Event = new byte[0x10],
        };
        image.Variables.Add(new VarEntry(0x5600_0010, 1, 0));                          // V0: int
        image.Variables.Add(new VarEntry(0x4600_0014, 1, 0));                          // V1: uint
        image.Variables.Add(new VarEntry(0x0600_0018, 1, 0));                          // V2: byte
        image.Variables.Add(new VarEntry(0x6600_001c, 1, 0));                          // V3: float
        image.Variables.Add(new VarEntry(0x4600_0020 | VarEntry.ArrayBit, 2, 0));      // V4: uint[2]

        WorkerAssembler w0 = new();
        w0.PlaceEntry(0);
        w0.Add("""
                pushv v1
                popv v0
                pushv v0
                popv v1
                pushv v1
                pushimm 1
                add
                popv v1
                pushv v1
                pushv v0
                add
                popv v1
                pushv v1
                pushimm 2
                sar
                popv v1
                pushv v1
                pushv v0
                div
                popv v1
                pushimm -1
                popv v1
                pushv v2
                popv v1
                pushv v1
                popv v2
                pushv v1
                popv v3
                pushimm 1
                pushv v1
                popva v4
                pushimm 0
                op_2b
                pushva v4
                pushv v0
                add
                popva v4
                pushv v1
                pushimm 3
                gt
                popa_jz l0
                pushv v1
                popa_jz l0
                pushv v1
                pushv v0
                and
                popv v0
            l0:
                end
            """);
        AssembledWorker a0 = w0.Finish();
        image.Workers.Add(new WorkerImage { Type = 2, Code = a0.Code, Entries = a0.Entries, Labels = a0.Labels });
        byte[] block = image.Write();
        byte[] original = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(original, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(4), 0x40);
        block.CopyTo(original, 0x40);

        ScriptFile file = ScriptFile.Parse(original);
        DecompiledScript d = ScriptDecompiler.Decompile("ffx_ps2/ffx/master/jppc/battle/mon/_m996/m996.bin", file);
        string[] expected = [
            "public static uint V1;",
            "V0 = (int)V1;",
            "V1 = (uint)V0;",
            "V1 += 1;",
            "V1 += (uint)V0;",
            "V1 = (uint)((int)V1 >> 2);",
            "V1 = (uint)((int)V1 / V0);",
            "V1 = unchecked((uint)-1);",
            "V1 = V2;",
            "V2 = (byte)V1;",
            "V3 = (int)V1;",
            "V4[1] = V1;",
            "V4[0] += (uint)V0;",
            "(int)V1 > 3",
            "AsBool((int)V1)",
            "V0 = (int)V1 & V0;",
        ];
        foreach (string line in expected) Assert.Contains(line, d.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("Asm(", d.Source, StringComparison.Ordinal);

        SyntaxTree tree = ScriptCompiler.Parse(d.Source, "m996.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        Assert.Equal(original, ScriptCompiler.Compile([tree]).ApplyTo(file).ToArray());
    }

    /// <summary>
    ///     Named values (Phase 5, review P7): maps, sound effects in hex, battle ids as constants (also where a
    ///     native returns one), monster-id selectors and the property of <c>SetActorProperty</c>; the source
    ///     compiles back to the same bytes.
    /// </summary>
    [Fact]
    public void NamedValuesRoundTrip() {
        ScriptImage image = new() {
            Author = "test"u8.ToArray(), Name = "997"u8.ToArray(), Shared = new byte[0x20], Other = new byte[0x40], Event = new byte[0x10],
        };
        image.IntConstants.AddRange([0x8000_0017, 0x012c_0000, 0x0001_6008]);
        WorkerAssembler w0 = new();
        w0.PlaceEntry(0);
        w0.Add("""
                pushimm 194
                pushimm 0
                func_facc 0x010b
                pushc c0
                func_facc 0x00df
                pushc c2
                pushimm 63
                pushimm 127
                func_facc 0x00fd
                pushimm 3
                pushimm 1
                func_facc 0x70ab
                pushimm 4230
                pushimm 0
                pushimm 0
                func_facc 0x7018
                pushc c1
                pushimm 1
                func_facc 0x7002
                func 0x7024
                pushc c1
                eq
                popa_jz l0
                pushimm 2
                pushimm 0
                func_facc 0x0011
            l0:
                end
            """);
        AssembledWorker a0 = w0.Finish();
        image.Workers.Add(new WorkerImage { Type = 2, Code = a0.Code, Entries = a0.Entries, Labels = a0.Labels });
        byte[] block = image.Write();
        byte[] original = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(original, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(4), 0x40);
        block.CopyTo(original, 0x40);

        ScriptFile file = ScriptFile.Parse(original);
        DecompiledScript d = ScriptDecompiler.Decompile("ffx_ps2/ffx/master/jppc/battle/mon/_m997/m997.bin", file);
        string[] expected = [
            "Common.MapJump(Map.Hiku0800, 0);",
            "Sound.Play((Sfx)0x80000017);",
            "Sound.Play((Sfx)0x16008, 63, 127);",
            // A property by its constant (Phase 5, "Typing and members picked by a constant").
            "Self.Maxmp = 1;",
            "Actors[Target.Biran].Hp = 0;",
            "Btl.LabelExe(Battle.Kami00_00, 1);",
            "if (Btl.GetBattleId() == Battle.Kami00_00)",
            "Common.MapChange(Map.Camtest0, 0);",
        ];
        foreach (string line in expected) Assert.Contains(line, d.Source, StringComparison.Ordinal);

        SyntaxTree tree = ScriptCompiler.Parse(d.Source, "m997.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        Assert.Equal(original, ScriptCompiler.Compile([tree]).ApplyTo(file).ToArray());
    }

    /// <summary>Phase 5: constants inside a condition are false/true, and a native typed bool is a condition without AsBool.</summary>
    [Fact]
    public void BoolConstantsAndBoolNativesAreConditions() {
        const string source = """
            using Ffx;
            using Ffx.Atel;
            using static Monsters.M999.M999;

            namespace Monsters.M999;

            [Monster("m999")]
            [Workers(typeof(W00))]
            public static partial class M999
            {
                [Var(Storage.Private, 0x0000)] public static int Count;
            }

            public sealed class W00 : Worker2
            {
                [Entry(0)]
                void E0()
                {
                    if (false & Count > 2)
                    {
                        Count = 1;
                    }
                    if (!Common.VoiceIsPlaying() | true)
                    {
                        Count = 2;
                    }
                    return;
                }
            }
            """;
        SyntaxTree tree = ScriptCompiler.Parse(source, "m999.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        ScriptBlock block = ScriptBlock.Parse(ScriptCompiler.Compile([tree]).Block());
        Assert.Equal(
            "pushimm pushv pushimm gt land popa_jz pushimm popv func not pushimm lor popa_jz pushimm popv end",
            string.Join(" ", block.Decode(block.Workers[0]).Select(i => i.Opcode.Name)));
    }

    [Fact]
    public void HandWrittenStatementsCompile() {
        string source = """
            using Ffx;
            using Ffx.Atel;
            using static Monsters.M999.M999;

            namespace Monsters.M999;

            [Monster("m999")]
            [Workers(typeof(W00))]
            public static partial class M999
            {
                [Var(Storage.Private, 0x0000)] public static int Count;
            }

            public sealed class W00 : Worker2
            {
                [Entry(0)]
                void E0()
                {
                    Count = (Count + 1) * 2;
                    if (Count == 4 | Count < 0) goto done;
                    Common.WaitFrames(Count & 3);
                done:
                    return;
                }
            }
            """;
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.Compile(source, "m999.cs"));
        Assert.Contains("labels are lN", ex.Message, StringComparison.Ordinal);
        CompiledScript compiled = ScriptCompiler.Compile(source.Replace("done", "l0", StringComparison.Ordinal), "m999.cs");
        List<Instr> code = ScriptBlock.Parse(compiled.Block()).Decode(ScriptBlock.Parse(compiled.Block()).Workers[0]);
        Assert.Equal(
            "pushv pushimm add pushimm mul popv pushv pushimm eq pushv pushimm lt lor popa_jnz pushv pushimm and func_facc end",
            string.Join(" ", code.Select(i => i.Opcode.Name)));
    }

    /// <summary>The structured statements, written by hand: what each lowers to, labels numbered per construct.</summary>
    [Fact]
    public void ControlFlowCompiles() {
        string source = """
            using Ffx;
            using Ffx.Atel;
            using static Monsters.M999.M999;

            namespace Monsters.M999;

            [Monster("m999")]
            [Workers(typeof(W00))]
            public static partial class M999
            {
                [Var(Storage.Private, 0x0000)] public static int Count;
                [Var(Storage.Private, 0x0004)] public static ushort Small;
            }

            public sealed class W00 : Worker2
            {
                [Entry(0)]
                void E0()
                {
                    if (Count == 1) { Count = 2; } else { Count = 3; }
                    while (Count < 9)
                    {
                        if (Count == 5) { break; }
                        Count += 1;
                    }
                    for (Count = 0; Count < 3; Count += 1) { continue; }
                    do { Count -= 1; } while (Count > 0);
                    switch (Count)
                    {
                        case 1:
                            Count = 4;
                            goto case 2;
                        case 2:
                            break;
                        case >= 5 and <= 7:
                            return;
                        default:
                            break;
                    }
                    if (false) { Count = 8; }
                    StoreClamped(ref Small, Small + 1);
                    Nop(3);
                    RequestParty(3, 0, 6);
                    End(0, reset: true);
                    Entry(1);
                    return;
                }
            }
            """;
        SyntaxTree tree = ScriptCompiler.Parse(source, "m999.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        ScriptBlock block = ScriptBlock.Parse(ScriptCompiler.Compile([tree]).Block());
        Bytecode.Script.Worker w = block.Workers[0];
        string text = string.Join("\n", block.Decode(w).Select(i => Bytecode.Script.Assembly.Render(block, 0, i)));
        string[] expected = [
            // if/else: the else label at the condition (l0), the end label when the else starts (l1).
            "pushv v0\npushimm 1\neq\npopa_jz l0\npushimm 2\npopv v0\njmp l1",
            // while: exit l2, head l3; the inner if's end label l4.
            "pushv v0\npushimm 9\nlt\npopa_jz l2\npushv v0\npushimm 5\neq\npopa_jz l4\njmp l2",
            "jmp l3",
            // for: exit l5, body l6, head l7, step l8; the step comes before the body.
            "pushimm 0\npopv v0\npushv v0\npushimm 3\nlt\npopa_jz l5\njmp l6\npushv v0\npushimm 1\nadd\npopv v0\njmp l7\njmp l8\njmp l8",
            // do: popa_jnz back to its head l10 (its exit l9 unused).
            "pushv v0\npushimm 0\ngt\npopa_jnz l9",
            // switch: chain l10, break l11, cases l12-l14, default l15, the range skip l16; the fall-through has no jmp.
            "pushv v0\npopb\njmp l10\npushimm 4\npopv v0\njmp l11\nend\njmp l11",
            "pushimm 1\npushb\neq\npopa_jnz l12\npushimm 2\npushb\neq\npopa_jnz l13\npushb\npushimm 5\nge\npopa_jz l16\npushb\npushimm 7\nle\npopa_jnz l14\njmp l15",
            "jmp l17\npushimm 8\npopv v0",
            "pushv v1\npushimm 1\nadd\npopv_1 v1\nnop_76 0x0003\npushimm 3\npushimm 0\npushimm 6\nop_39\npopfacc\npushimm 0\nendp_1\nend",
        ];
        foreach (string part in expected) Assert.Contains(part, text, StringComparison.Ordinal);
        Assert.Equal(2, w.Entries.Length);
        Assert.Equal(block.Decode(w)[^1].Offset, w.Entries[1]);
    }

    /// <summary>
    ///     Named bits of save fields (<c>[Bits]</c> on <see cref="Save"/>): a bit test or set on such a field decompiles
    ///     to the constant where the mask has a name, a number where it has none or is on another field, and the
    ///     source compiles back to the same bytes.
    /// </summary>
    [Fact]
    public void SaveBitsDecompileToTheirNames() {
        string source = """
            using Ffx;
            using Ffx.Atel;
            using static Monsters.M997.M997;

            namespace Monsters.M997;

            [Monster("m997")]
            [Workers(typeof(W00))]
            public static partial class M997
            {
                [Var(Storage.Save, Save.LightningDodgingRewards)] public static byte[] Rewards = new byte[1];
                [Var(Storage.Save, Save.CompletionFlagsDarkValefor)] public static byte[] Valefor = new byte[1];
                [Var(Storage.Save, 0x0a33)] public static byte Other;
            }

            public sealed class W00 : Worker2
            {
                [Entry(0)]
                void E0()
                {
                    if (AsBool(Rewards[0] & 8))
                    {
                        Rewards[0] |= 64;
                    }
                    Valefor[0] |= 128;
                    Valefor[0] |= 2;
                    if (AsBool(Valefor[0] & 129))
                    {
                        return;
                    }
                    Other |= 1;
                }
            }
            """;
        SyntaxTree compiled = ScriptCompiler.Parse(source, "m997.cs");
        Assert.Empty(ScriptCompiler.Check([compiled]));
        byte[] block = ScriptCompiler.Compile([compiled]).Block();
        byte[] original = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(original, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(4), 0x40);
        block.CopyTo(original, 0x40);

        ScriptFile file = ScriptFile.Parse(original);
        DecompiledScript d = ScriptDecompiler.Decompile("ffx_ps2/ffx/master/jppc/battle/mon/_m997/m997.bin", file);
        string[] expected = [
            "LightningDodgingRewards[0] & Save.LightningReward.Streak50",
            "LightningDodgingRewards[0] |= Save.LightningReward.Bolts30;",
            "CompletionFlagsDarkValefor[0] |= Save.DarkAeonFlag.Defeated;",
            "CompletionFlagsDarkValefor[0] |= 2;",                // bit 1 has no name
            "CompletionFlagsDarkValefor[0] & 129",                // nor has a mask of two bits
            "Save_0a33 |= 1;",                                    // a field without named bits
        ];
        foreach (string line in expected) Assert.Contains(line, d.Source, StringComparison.Ordinal);

        SyntaxTree tree = ScriptCompiler.Parse(d.Source, "m997.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        Assert.Equal(original, ScriptCompiler.Compile([tree]).ApplyTo(file).ToArray());
    }

    /// <summary>
    ///     A switch with only a default section (its chain is the one <c>jmp</c> to it) decompiles to a
    ///     <c>switch</c>, not to gotos, and compiles back to the same bytes.
    /// </summary>
    [Fact]
    public void DefaultOnlySwitchRoundTrips() {
        string source = """
            using Ffx;
            using Ffx.Atel;
            using static Monsters.M996.M996;

            namespace Monsters.M996;

            [Monster("m996")]
            [Workers(typeof(W00))]
            public static partial class M996
            {
                [Var(Storage.Private, 0x0000)] public static int Count;
            }

            public sealed class W00 : Worker2
            {
                [Entry(0)]
                void E0()
                {
                    switch (Count)
                    {
                        default:
                            Count = 3;
                            break;
                    }
                    Count += 1;
                }
            }
            """;
        SyntaxTree compiled = ScriptCompiler.Parse(source, "m996.cs");
        Assert.Empty(ScriptCompiler.Check([compiled]));
        byte[] block = ScriptCompiler.Compile([compiled]).Block();
        byte[] original = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(original, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(4), 0x40);
        block.CopyTo(original, 0x40);

        ScriptFile file = ScriptFile.Parse(original);
        DecompiledScript d = ScriptDecompiler.Decompile("ffx_ps2/ffx/master/jppc/battle/mon/_m996/m996.bin", file);
        Assert.Contains("default:", d.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("goto", d.Source, StringComparison.Ordinal);

        SyntaxTree tree = ScriptCompiler.Parse(d.Source, "m996.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        Assert.Equal(original, ScriptCompiler.Compile([tree]).ApplyTo(file).ToArray());
    }

    /// <summary>The reference assembly's constants (Phase 5): story values and save offsets compile to their numbers.</summary>
    [Fact]
    public void NamedConstantsCompile() {
        string source = """
            using Ffx;
            using Ffx.Atel;
            using static Events.Zz.Zz0000.Zz0000;

            namespace Events.Zz.Zz0000;

            [Event("zz0000")]
            [Workers(typeof(W00))]
            public static partial class Zz0000
            {
                [Var(Storage.Save, Save.StoryProgress)] public static ushort StoryProgress;
            }

            public sealed class W00 : Worker1
            {
                [Entry(0)]
                void E0()
                {
                    if (StoryProgress < Story.S01340_Kami0400)
                    {
                        StoryProgress = Story.S00005_Znkd1400;
                    }
                    switch (StoryProgress)
                    {
                        case Story.S00006_Znkd1300:
                            return;
                    }
                }
            }
            """;
        SyntaxTree tree = ScriptCompiler.Parse(source, "zz0000.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        ScriptBlock block = ScriptBlock.Parse(ScriptCompiler.Compile([tree]).Block());
        Assert.Equal(0x0a00, ScriptImage.FromBlock(block).Variables[0].Offset);
        string text = string.Join("\n", block.Decode(block.Workers[0]).Select(i => Bytecode.Script.Assembly.Render(block, 0, i)));
        Assert.Contains("pushv v0\npushimm 1340\nlt", text, StringComparison.Ordinal);
        Assert.Contains("pushimm 5\npopv v0", text, StringComparison.Ordinal);
        Assert.Contains("pushimm 6\npushb\neq", text, StringComparison.Ordinal);
    }

    /// <summary>Typed native arguments (Phase 5): enum members, casts and messages compile to their numbers.</summary>
    [Fact]
    public void TypedArgumentsCompile() {
        string source = """
            using Ffx;
            using Ffx.Atel;
            using static Monsters.M999.M999;

            namespace Monsters.M999;

            [Monster("m999")]
            [Workers(typeof(W00))]
            public static partial class M999
            {
                [Var(Storage.Private, 0x0000)] public static int Count;
            }

            public sealed class W00 : Worker2
            {
                [Entry(0)]
                void E0()
                {
                    Btl.SetActorsProperty(Target.Own, ActorProperty.Hp, 3);
                    Btl.UseCommand((Target)(-30), (Command)Count);
                    Common.MsgSetText(0, Text[7]);
                    Common.MsgSetText(0, Count);
                }
            }
            """;
        SyntaxTree tree = ScriptCompiler.Parse(source, "m999.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        ScriptBlock block = ScriptBlock.Parse(ScriptCompiler.Compile([tree]).Block());
        string text = string.Join("\n", block.Decode(block.Workers[0]).Select(i => Bytecode.Script.Assembly.Render(block, 0, i)));
        Assert.Contains("pushimm -13\npushimm 0\npushimm 3\nfunc_facc 0x7018", text, StringComparison.Ordinal);
        Assert.Contains("pushimm -30\npushv v0\nfunc_facc 0x700b", text, StringComparison.Ordinal);
        Assert.Contains("pushimm 0\npushimm 7\nfunc_facc 0x0064", text, StringComparison.Ordinal);
    }

    /// <summary>Init and Main overrides are entries 0 and 1 (Phase 5); a Model argument compiles to its number.</summary>
    [Fact]
    public void EngineEntriesAreOverrides() {
        string source = """
            using Ffx;
            using Ffx.Atel;

            namespace Events.Zz.Zz0000;

            [Event("zz0000")]
            [Workers(typeof(Tidu))]
            public static partial class Zz0000
            {
            }

            public sealed class Tidu : Worker1
            {
                protected override void Init()
                {
                    Common.SetModel(Model.Tidu);
                }

                protected override void Main()
                {
                    return;
                }

                [Entry(2)]
                void E2()
                {
                    return;
                }
            }
            """;
        SyntaxTree tree = ScriptCompiler.Parse(source, "zz0000.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        ScriptBlock block = ScriptBlock.Parse(ScriptCompiler.Compile([tree]).Block());
        Bytecode.Script.Worker w = block.Workers[0];
        List<Instr> code = block.Decode(w);
        Assert.Equal(3, w.Entries.Length);
        Assert.Equal(code[0].Offset, w.Entries[0]);
        Assert.Equal("pushimm 1", Bytecode.Script.Assembly.Render(block, 0, code[0]));
        Assert.Equal(code[2].Offset, w.Entries[1]);
    }

    /// <summary>Requests as calls of async entries (Phase 5): the three forms, a priority, and the fallback for the worker's own entry.</summary>
    [Fact]
    public void RequestsAreCallsOfAsyncEntries() {
        string source = """
            using Ffx;
            using Ffx.Atel;

            namespace Monsters.M997;

            [Monster("m997")]
            [Workers(typeof(Lulu), typeof(Wakka))]
            public static partial class M997
            {
            }

            public sealed class Lulu : Worker2
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
                public static async Task E2Async(Priority priority = Priority.P1)
                {
                    return;
                }
            }

            public sealed class Wakka : Worker2
            {
                protected override async void Init()
                {
                    _ = Lulu.E2Async();
                    await Lulu.E2Async(Priority.P3);
                    await Lulu.E2Async().Started;
                    await Lulu.E2Async().Finished;
                    Request(1, 1, 2);
                    return;
                }

                protected override void Main()
                {
                    return;
                }

                [Entry(2)]
                void E2()
                {
                    return;
                }
            }
            """;
        SyntaxTree tree = ScriptCompiler.Parse(source, "m997.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        CompiledScript compiled = ScriptCompiler.Compile([tree]);
        ScriptBlock block = ScriptBlock.Parse(compiled.Block());
        string text = string.Join("\n", block.Decode(block.Workers[1]).Select(i => Bytecode.Script.Assembly.Render(block, 1, i)));
        Assert.Contains("pushimm 1\npushimm 0\npushimm 2\nreq0\npopfacc", text, StringComparison.Ordinal);
        Assert.Contains("pushimm 3\npushimm 0\npushimm 2\nreqe0\npopfacc", text, StringComparison.Ordinal);
        Assert.Contains("pushimm 1\npushimm 0\npushimm 2\nreqw0\npopfacc", text, StringComparison.Ordinal);
        Assert.Contains("pushimm 1\npushimm 0\npushimm 2\nreqe0\npopfacc", text, StringComparison.Ordinal);
        Assert.Contains("pushimm 1\npushimm 1\npushimm 2\nreq0\npopfacc", text, StringComparison.Ordinal);

        // The decompiler writes the same forms back, and they compile to the same block.
        byte[] blockBytes = compiled.Block();
        byte[] file = new byte[0x40 + (blockBytes.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        blockBytes.CopyTo(file, 0x40);
        ScriptFile parsed = ScriptFile.Parse(file);
        DecompiledScript d = ScriptDecompiler.Decompile("ffx_ps2/ffx/master/jppc/battle/mon/_m997/m997.bin", parsed);
        string[] expected = [
            "public static async Task E2Async(Priority priority = Priority.P1)",
            "protected override async void Init()",
            "_ = W00.E2Async();",
            "await W00.E2Async(Priority.P3);",
            "await W00.E2Async().Started;",
            "await W00.E2Async();",
            "Request(1, 1, 2);",
            "void E2()",
        ];
        foreach (string line in expected) Assert.Contains(line, d.Source, StringComparison.Ordinal);
        SyntaxTree back = ScriptCompiler.Parse(d.Source, "m997.cs");
        Assert.Empty(ScriptCompiler.Check([back]));
        Assert.Equal(file, ScriptCompiler.Compile([back]).ApplyTo(parsed).ToArray());
    }

    [Fact]
    public void ARequestToAMethodThatIsNoEntryIsAnError() {
        const string source = """
            using Ffx.Atel;

            namespace Monsters.M997;

            [Monster("m997")]
            [Workers(typeof(W00))]
            public static partial class M997 { }

            public sealed class W00 : Worker2
            {
                protected override void Init()
                {
                    _ = W00.E9Async();
                }
            }
            """;
        CompileException e = Assert.Throws<CompileException>(() => ScriptCompiler.Compile([ScriptCompiler.Parse(source, "m997.cs")]));
        Assert.Contains("W00 has no entry method E9Async", e.Message, StringComparison.Ordinal);
    }

    private const string BadSource = """"
        using Ffx.Atel;

        namespace Monsters.M999;

        [Monster("m999")]
        [Workers(typeof(W00))]
        public static partial class M999 { }

        sealed class W00 : Worker2
        {
            [Entry(0)]
            void E0()
            {
                Asm("""
                    nop
                    bogus 1
                    """);
            }
        }
        """";

    [Fact]
    public void AssemblyErrorsGiveTheSourceLine() {
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.Compile(BadSource, "m999.cs"));
        Assert.Equal("m999.cs(16): unknown instruction bogus", ex.Message);
    }

    [Fact]
    public void AnUnlistedWorkerIsAnError() {
        string source = BadSource.Replace("bogus 1", "end", StringComparison.Ordinal) + "\nsealed class W01 : Worker1 { }\n";
        CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.Compile(source, "m999.cs"));
        Assert.Contains("W01 is not in [Workers]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSourceIsCheckedAsCSharp() {
        string source = BadSource.Replace("void E0()", "void E0(Undefined x)", StringComparison.Ordinal);
        Assert.Contains(ScriptCompiler.Check([ScriptCompiler.Parse(source)]), d => d.Id == "CS0246");
    }

    [Theory]
    [InlineData(MonsterPath, "Monsters.M999", "M999")]
    [InlineData("ffx_ps2/ffx/master/jppc/event/obj/bj/bjyt0400/bjyt0400.ebp", "Events.Bj_BaajTemple.Bjyt0400", "Bjyt0400")]
    [InlineData("ffx_ps2/ffx/master/jppc/event/obj/ka/kami0400/200thunder_kami0400.ebp", "Events.Ka_ThunderPlains._200thunder_kami0400", "_200thunder_kami0400")]
    [InlineData("ffx_ps2/ffx/master/jppc/battle/btl/cdsp00_00/cdsp00_00.bin", "Battles.Cdsp_SalvageShip.Cdsp00_00", "Cdsp00_00")]
    [InlineData("ffx_ps2/ffx/master/jppc/event/obj/te/test01/test01.ebp", "Events.Te.Test01", "Test01")]
    [InlineData("ffx_ps2/ffx/master/jppc/battle/btl/zzzz00_00/zzzz00_00.bin", "Battles.Zzzz.Zzzz00_00", "Zzzz00_00")]
    public void ScriptsAreNamedAfterTheirPath(string path, string ns, string cls) {
        SourceNames names = SourceNames.ForPath(path);
        Assert.Equal((ns, cls), (names.Namespace, names.ClassName));
    }
}
