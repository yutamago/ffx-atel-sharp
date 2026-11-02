// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Bytecode.Tests;

public sealed class UnitTests {
    [Fact]
    public void OpcodeNamesAreUniqueAndIndexed() {
        Assert.Equal(128, Opcodes.All.Count);
        foreach (Opcode o in Opcodes.All) Assert.Equal(o.Code, Opcodes.ByName(o.Name)!.Code);
        Assert.Equal("popa_jmp", Opcodes.All[Opcodes.PopAJmp].Name);
        Assert.Equal("pushg", Opcodes.All[Opcodes.PushG].Name);
        Assert.Equal("reqe1", Opcodes.All[Opcodes.ReqE1].Name);
        Assert.Equal("pushf0", Opcodes.All[Opcodes.PushF0].Name);
    }

    [Theory]
    [InlineData(1f, "1.0")]
    [InlineData(-2.5f, "-2.5")]
    [InlineData(0.1f, "0.1")]
    [InlineData(0.0001f, "0.0001")]
    [InlineData(0.00001f, "1e-5")]
    [InlineData(1.5e20f, "1.5e20")]
    [InlineData(1e16f, "1e16")]
    [InlineData(123456789f, "123456790.0")]
    [InlineData(0f, "0.0")]
    [InlineData(1050.15625f, "1050.1563")]
    [InlineData(float.Epsilon, "1e-45")]
    [InlineData(float.MaxValue, "3.4028235e38")]
    [InlineData(float.NaN, "NaN")]
    [InlineData(float.PositiveInfinity, "inf")]
    public void FloatsFormatAsRustDebug(float value, string text) => Assert.Equal(text, RustFloat.Format(value));

    [Theory]
    [InlineData("l3", (ushort)3)]
    [InlineData("-1", (ushort)0xffff)]
    [InlineData("0x6012", (ushort)0x6012)]
    [InlineData("-32768", (ushort)0x8000)]
    [InlineData("65535", (ushort)0xffff)]
    public void OperandsParse(string text, ushort value) => Assert.Equal(value, Assembly.ParseOperand(text));

    [Theory]
    [InlineData("65536")]
    [InlineData("-32769")]
    [InlineData("x1")]
    public void BadOperandsDoNotParse(string text) => Assert.Null(Assembly.ParseOperand(text));

    [Fact]
    public void Op2bDuplicatesTheTop() => Assert.Equal((1, 2), StackEffects.Of(Opcodes.Op2b));

    [Fact]
    public void NativeSignaturesParse() {
        NativeSignatures n = NativeSignatures.Parse("""
            { "natives": [ { "id": "0x0012", "class": "Common", "method": "Foo", "args": [ { "name": "a", "kind": "int" }, { "name": "b", "kind": "float" } ],
                             "result": "float", "argc_source": "both", "kinds_source": "exe" } ] }
            """);
        NativeSignature foo = n[0x0012]!;
        Assert.Equal("Common.Foo", foo.Name);
        Assert.Equal("if", foo.ArgKinds);
        Assert.Equal(2, foo.Argc);
        Assert.True(foo.KindsFromExe);
        Assert.Null(n[0x0013]);
    }

    [Fact]
    public void HexTextLeavesZerosOutAndReadsBack() {
        byte[] data = new byte[0x90];
        data[2] = 0x30;
        data[0x41] = 0xff;
        data[0x8f] = 1;
        string text = HexText.Format(data);
        Assert.Equal("0000: 000030\n0040: 00ff\n0080: 00000000 00000000 00000000 00000001\n", text);
        Assert.Equal(data, HexText.Parse(text, data.Length));
        Assert.Equal(new byte[8], HexText.Parse("", 8));
        Assert.Throws<FormatException>(() => HexText.Parse("0000: 0102", 1));
        Assert.Throws<FormatException>(() => HexText.Parse("0000: 012", 4));
    }

    [Fact]
    public void AWorkerAssemblesWithoutAnOriginal() {
        WorkerAssembler asm = new();
        asm.PlaceEntry(0);
        asm.Add("""
            pushimm 1
        l1:
            popa_jz l0  ; comment
        l0:
            end
        """);
        asm.PlaceEntry(1);
        asm.Add("end");
        AssembledWorker w = asm.Finish();
        Assert.Equal([0u, 7u], w.Entries);
        Assert.Equal([6u, 3u], w.Labels);
        Assert.Equal(8, w.Code.Length);
    }

    [Fact]
    public void AWorkerAssemblerReportsGapsAndRepeats() {
        WorkerAssembler asm = new();
        asm.PlaceEntry(1);
        asm.Add("end");
        Assert.Throws<AssemblyException>(() => asm.Finish());
        Assert.Throws<AssemblyException>(() => asm.PlaceEntry(1));
        Assert.Throws<AssemblyException>(() => asm.Add("worker 1"));
    }
}
