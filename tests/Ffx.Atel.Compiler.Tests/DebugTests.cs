// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary><c>Debug.Print</c> and <c>Debug.Break</c> (docs/formats/debug.md).</summary>
public sealed class DebugTests {
    private const string Source = """
        using Ffx;
        using Ffx.Atel;
        using static Monsters.M998.M998;

        namespace Monsters.M998;

        [Monster("m998")]
        [Workers(typeof(W00))]
        [SharedData(0x40, "")]
        [OtherData(0x40, "")]
        [EventData(0x10, "")]
        public static partial class M998
        {
            [Var(Storage.Shared, 0x0000)] public static int Count;
            [Var(Storage.Shared, 0x0004)] public static float Speed;
        }

        public sealed partial class W00 : Worker2
        {
            protected override void Init()
            {
                Count = 1;
                Debug.Print($"count {Count,3} speed {Speed:F2} {{ok}} " + "done");
                if (Count == 1)
                    Debug.Print("plain");
                Debug.Break();
                return;
            }
        }
        """;

    private static List<Instr> Init(CompiledScript s) {
        ScriptBlock block = ScriptBlock.Parse(s.Block());
        return block.Decode(block.Workers[0]);
    }

    [Fact]
    public void DebugCallsAreCSharp() {
        Assert.Empty(ScriptCompiler.Check([ScriptCompiler.Parse(Source, "M998.cs")]));
    }

    [Fact]
    public void PrintPushesTheValuesTheirCountAndTheTextsId() {
        CompiledScript s = ScriptCompiler.Compile(Source, "M998.cs");
        Assert.Equal(["count {0,3} speed {1:F2} {{ok}} done", "plain"], s.DebugStrings);
        Assert.Empty(s.Warnings);
        List<Instr> code = Init(s);
        Assert.Equal("pushimm popv pushv pushv pushimm pushc func_facc pushv pushimm eq popa_jz pushimm pushc func_facc func_facc end", Names(code));
        ScriptImage image = s.Image;
        // The ids are pool constants; the count is an immediate.
        List<Instr> funcs = [.. code.Where(i => i.Code == Opcodes.FuncFacc)];
        Assert.Equal([DebugText.Print, DebugText.Print, DebugText.Break], funcs.Select(i => i.Operand!.Value));
        Assert.Contains(unchecked((uint)DebugText.Id("count {0,3} speed {1:F2} {{ok}} done")), image.IntConstants);
        Assert.Contains(unchecked((uint)DebugText.Id("plain")), image.IntConstants);
        int count = code.FindIndex(i => i.Code == Opcodes.PushImm && i.Operand == 2);
        Assert.True(count > 0 && code[count + 1].Code == Opcodes.PushC && code[count + 2].Code == Opcodes.FuncFacc);
    }

    /// <summary>The source with every Debug call an empty statement.</summary>
    private static string Without(string source) => System.Text.RegularExpressions.Regex.Replace(source, @"Debug\.[^;]*;", ";");

    private static string Names(List<Instr> code) => string.Join(" ", code.Select(i => i.Opcode.Name));

    [Fact]
    public void DropLeavesTheCallsOutWithAWarningEach() {
        CompiledScript s = ScriptCompiler.Compile([ScriptCompiler.Parse(Source, "M998.cs")], options: new CompileOptions { Debug = DebugMode.Drop });
        Assert.Empty(s.DebugStrings);
        Assert.Equal(3, s.Warnings.Count);
        Assert.Contains("M998.cs(23,", s.Warnings[0], StringComparison.Ordinal);
        Assert.DoesNotContain(Init(s), i => i.Code == Opcodes.FuncFacc);
        // The same script without the calls.
        string without = Without(Source);
        Assert.Equal(ScriptCompiler.Compile(without, "M998.cs").Block(), s.Block());
    }

    [Fact]
    public void ErrorRefusesThem() {
        CompileException ex = Assert.Throws<CompileException>(() =>
            ScriptCompiler.Compile([ScriptCompiler.Parse(Source, "M998.cs")], options: new CompileOptions { Debug = DebugMode.Error }));
        Assert.Contains("Debug.Print", ex.Message, StringComparison.Ordinal);
        Assert.Contains("(23,", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Debug.Print(Count);", "string literal or an interpolated string")]
    [InlineData("Debug.Print($\"{\"x\"}\");", "holds a value")]
    [InlineData("Debug.Break(1);", "Debug.Break()")]
    public void OtherFormsAreErrors(string call, string message) {
        string source = Source.Replace("Debug.Break();", call, StringComparison.Ordinal);
        foreach (DebugMode mode in new[] { DebugMode.Natives, DebugMode.Drop }) {
            CompileException ex = Assert.Throws<CompileException>(() =>
                ScriptCompiler.Compile([ScriptCompiler.Parse(source, "M998.cs")], options: new CompileOptions { Debug = mode }));
            Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AtMostSixteenValues() {
        string holes = string.Concat(Enumerable.Repeat("{Count}", 17));
        string source = Source.Replace("Debug.Break();", $"Debug.Print($\"{holes}\");", StringComparison.Ordinal);
        Assert.Throws<CompileException>(() => ScriptCompiler.Compile(source, "M998.cs"));
    }

    private const string ModSource = """
        using Ffx;
        using Ffx.Atel;
        using static Monsters.M998.M998;

        namespace Monsters.M998;

        public sealed partial class W00
        {
            [Postfix(nameof(Init))]
            void Trace()
            {
                Debug.Print($"init done, count {Count}");
            }
        }
        """;

    [Fact]
    public void AModPackageCarriesItsTexts() {
        string vanilla = Without(Source);
        CompiledMod mod = ScriptCompiler.CompileMod([ScriptCompiler.Parse(vanilla, "Monsters/M998/M998.cs")], [ScriptCompiler.Parse(ModSource, "mod/W00.cs")]);
        Assert.Equal(["init done, count {0}"], mod.Package.DebugStrings);
        Assert.Contains(mod.Package.Patches[0].Code.Relocs, r => r.Kind == RelocKind.Int && r.Value == unchecked((uint)DebugText.Id("init done, count {0}")));
        ScriptPackage back = ScriptPackage.FromJson(mod.Package.ToJson());
        Assert.Equal(mod.Package.DebugStrings, back.DebugStrings);

        // Linked: the postfix calls the native.
        byte[] block = ScriptCompiler.Compile(vanilla, "M998.cs").Block();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        back.File = "ffx_ps2/ffx/master/jppc/battle/mon/_m998/m998.bin";
        LinkResult r = ScriptLinker.Link(file, [new ModScript("test", back)]);
        Assert.True(r.Changed, string.Join("\n", r.Messages));
        ScriptBlock linked = ScriptFile.Parse(r.File).Script;
        Assert.Contains(linked.Decode(linked.Workers[0]), i => i.Code == Opcodes.FuncFacc && i.Operand == DebugText.Print);

        // Dropped: nothing left of the call, the postfix is empty.
        CompiledMod dropped = ScriptCompiler.CompileMod([ScriptCompiler.Parse(vanilla, "Monsters/M998/M998.cs")], [ScriptCompiler.Parse(ModSource, "mod/W00.cs")],
            options: new CompileOptions { Debug = DebugMode.Drop });
        Assert.Null(dropped.Package.DebugStrings);
        Assert.Single(dropped.Warnings);
        Assert.Empty(dropped.Package.Patches[0].Code.Bytes);
    }

    [Fact]
    public void TheTextIsFormattedWithTheValues() {
        Assert.Equal("count   7 speed 1.50 {ok} done", DebugText.Format("count {0,3} speed {1:F2} {{ok}} done", 0, [7, 1.5f]));
        Assert.Equal("#0000002a [7, 1.5]", DebugText.Format(null, 42, [7, 1.5f]));
        Assert.Equal("{0} {1} [7]", DebugText.Format("{0} {1}", 0, [7]));
        Assert.Equal(unchecked((int)2166136261), DebugText.Id(""));
        // Braces as text, in each kind of string.
        string source = Source.Replace("Debug.Break();", "Debug.Print($$\"\"\"{a} {{Count}}\"\"\" + @\"{b}\" + $@\"{{c}}\\n\");", StringComparison.Ordinal);
        Assert.Equal("{{a}} {0}{{b}}{{c}}\\n", ScriptCompiler.Compile(source, "M998.cs").DebugStrings[^1]);
        Assert.Equal(unchecked((int)0xe40c292c), DebugText.Id("a"));
    }

    [Fact]
    public void TheTableKeepsTheFirstTextOfAnId() {
        DebugStringTable t = new();
        Assert.Empty(t.Add(["a", "b", "a"]));
        Assert.Equal("b", t.Find(DebugText.Id("b")));
        Assert.Null(t.Find(1));
        Assert.Equal(2, t.Count);
    }
}
