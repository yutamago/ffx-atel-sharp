// SPDX-License-Identifier: MIT

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
}
