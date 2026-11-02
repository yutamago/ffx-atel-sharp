// SPDX-License-Identifier: MIT

namespace Ffx.Atel.Bytecode.Script;

/// <summary>What an instruction's operand refers to; only affects how it is written.</summary>
public enum OperandKind {
    /// <summary>A value whose meaning is not known.</summary>
    Raw,
    /// <summary>Index into the worker's jump label table (<c>l3</c>).</summary>
    Label,
    /// <summary>Index into the integer constant table (<c>c3</c>).</summary>
    Int,
    /// <summary>Index into the float constant table (<c>f3</c>).</summary>
    Float,
    /// <summary>Signed 16-bit immediate.</summary>
    Imm,
    /// <summary>Index into the worker's variable table (<c>v3</c>).</summary>
    Var,
    /// <summary>Native function ID.</summary>
    Func,
    /// <summary>Worker index (<c>w3</c>).</summary>
    Worker,
}

/// <summary>One opcode of the script interpreter.</summary>
public sealed record Opcode(byte Code, string Name, OperandKind Operand, string Doc);

/// <summary>
///     The instruction set of the script interpreter <c>Script_Run</c> (<c>0x008641e0</c>).
/// </summary>
/// <remarks>
///     An instruction is one byte; with the high bit set, a <c>u16</c> operand follows
///     (<c>AtelFetchCommand</c>, <c>0x00869d60</c>). The opcode is the low 7 bits. Names and
///     evidence come from ffx-rust's <c>crates/ffx-script/src/opcode.rs</c>, which this table
///     mirrors one for one, so both disassemblers write the same text.
/// </remarks>
public static class Opcodes {
    /// <summary>All 128 opcodes. <c>0x1b</c>, <c>0x41</c>–<c>0x44</c> and <c>0x7b</c>–<c>0x7f</c> are invalid.</summary>
    public static IReadOnlyList<Opcode> All { get; }

    private static readonly Dictionary<string, Opcode> s_byName;

    public const byte Nop = 0x00, Lor = 0x01, Land = 0x02, Or = 0x03, Xor = 0x04, And = 0x05;
    public const byte Eq = 0x06, Ne = 0x07, Gtu = 0x08, Ltu = 0x09, Gt = 0x0a, Lt = 0x0b;
    public const byte Geu = 0x0c, Leu = 0x0d, Ge = 0x0e, Le = 0x0f;
    public const byte Btst = 0x10, Btstn = 0x11, Shl = 0x12, Sar = 0x13;
    public const byte Add = 0x14, Sub = 0x15, Mul = 0x16, Div = 0x17, Mod = 0x18, Not = 0x19, Neg = 0x1a, Bnot = 0x1c;
    public const byte PushV = 0x1f, PopV = 0x20, PopV1 = 0x21, PushVA = 0x22, PopVA = 0x23, PopVA1 = 0x24;
    public const byte PopFacc = 0x25, PushFacc = 0x26, PushVRef = 0x27;
    public const byte PushA = 0x28, PushB = 0x29, PopA = 0x2a, Op2b = 0x2b, PopB = 0x2c;
    public const byte PushC = 0x2d, PushImm = 0x2e, PushFC = 0x2f;
    public const byte Jmp = 0x30, Jnz = 0x31, Jz = 0x32, Call = 0x33, Ret = 0x34, Func = 0x35;
    public const byte Req0 = 0x36, ReqW0 = 0x37, ReqE0 = 0x38, Op39 = 0x39, Op3a = 0x3a, Op3b = 0x3b;
    public const byte End = 0x3c, EndP = 0x3d, End1 = 0x3e, EndP1 = 0x3f, Op40 = 0x40;
    public const byte Req1 = 0x45, ReqW1 = 0x4a, ReqE1 = 0x4f, Op54 = 0x54;
    public const byte PopAJmp = 0x55, PopAJnz = 0x56, PopAJz = 0x57, FuncFacc = 0x58;
    public const byte PopI0 = 0x59, PopF0 = 0x5d, PushI0 = 0x67, PushF0 = 0x6b;
    public const byte PushG = 0x75, Nop76 = 0x76, Op77 = 0x77, Op78 = 0x78, Op79 = 0x79, Op7a = 0x7a;

    static Opcodes() {
        List<Opcode> all = [];
        void Op(string name, OperandKind operand, string doc) => all.Add(new Opcode((byte)all.Count, name, operand, doc));
        const OperandKind R = OperandKind.Raw;

        Op("nop", R, "no operation");
        Op("lor", R, "pop b, a; push a || b");
        Op("land", R, "pop b, a; push a && b");
        Op("or", R, "pop b, a; push a | b");
        Op("xor", R, "pop b, a; push a ^ b");
        Op("and", R, "pop b, a; push a & b");
        Op("eq", R, "pop b, a; push a == b (int or float)");
        Op("ne", R, "pop b, a; push a != b");
        Op("gtu", R, "pop b, a; push a > b, unsigned");
        Op("ltu", R, "pop b, a; push a < b, unsigned");
        Op("gt", R, "pop b, a; push a > b, signed");
        Op("lt", R, "pop b, a; push a < b, signed");
        Op("geu", R, "pop b, a; push a >= b, unsigned");
        Op("leu", R, "pop b, a; push a <= b, unsigned");
        Op("ge", R, "pop b, a; push a >= b, signed");
        Op("le", R, "pop b, a; push a <= b, signed");
        Op("btst", R, "pop bit, a; push (a >> bit) & 1");
        Op("btstn", R, "pop bit, a; push !((a >> bit) & 1)");
        Op("shl", R, "pop n, a; push a << n");
        Op("sar", R, "pop n, a; push a >> n (arithmetic)");
        Op("add", R, "pop b, a; push a + b (int or float)");
        Op("sub", R, "pop b, a; push a - b");
        Op("mul", R, "pop b, a; push a * b");
        Op("div", R, "pop b, a; push a / b");
        Op("mod", R, "pop b, a; push a % b");
        Op("not", R, "pop a; push !a");
        Op("neg", R, "pop a; push -a (float)");
        Op("op_1b", R, "invalid");
        Op("bnot", R, "pop a; push ~a");
        Op("nop_1d", R, "no operation");
        Op("nop_1e", R, "no operation");
        Op("pushv", OperandKind.Var, "push variable");
        Op("popv", OperandKind.Var, "pop into variable");
        Op("popv_1", OperandKind.Var, "pop into variable, store mode 1");
        Op("pushva", OperandKind.Var, "pop index; push array element");
        Op("popva", OperandKind.Var, "pop value, index; store array element");
        Op("popva_1", OperandKind.Var, "pop value, index; store array element, mode 1");
        Op("popfacc", R, "pop into the float accumulator (worker +0x28)");
        Op("pushfacc", R, "push the float accumulator");
        Op("pushvref", OperandKind.Var, "pop index; push the address of an array element");
        Op("pusha", R, "push register A");
        Op("pushb", R, "push register B");
        Op("popa", R, "pop into register A");
        Op("op_2b", R, "duplicate the top (0x0086a000 reads it without popping)");
        Op("popb", R, "pop into register B");
        Op("pushc", OperandKind.Int, "push integer constant");
        Op("pushimm", OperandKind.Imm, "push signed 16-bit immediate");
        Op("pushfc", OperandKind.Float, "push float constant");
        Op("jmp", OperandKind.Label, "jump");
        Op("jnz", OperandKind.Label, "jump if register A != 0");
        Op("jz", OperandKind.Label, "jump if register A == 0");
        Op("call", OperandKind.Worker, "call the worker's first entry; up to 3 deep");
        Op("ret", R, "return from call");
        Op("func", OperandKind.Func, "call native function; its float (slot 2) or integer (slot 3) result is pushed");
        Op("req0", R, "worker request (0x00867230)");
        Op("reqw0", R, "worker request (0x00867570)");
        Op("reqe0", R, "worker request (0x008673d0)");
        Op("op_39", R, "pop 3; worker operation");
        Op("op_3a", R, "pop 3; worker operation");
        Op("op_3b", R, "pop 3; worker operation");
        Op("end", R, "end the entry");
        Op("endp", R, "pop; end the entry");
        Op("end_1", R, "end the entry, mode 1");
        Op("endp_1", R, "pop; end the entry, mode 1");
        Op("op_40", R, "wait");
        Op("op_41", R, "invalid");
        Op("op_42", R, "invalid");
        Op("op_43", R, "invalid");
        Op("op_44", R, "invalid");
        for (int i = 1; i <= 5; i++) Op($"req{i}", R, "worker request (0x00867230)");
        for (int i = 1; i <= 5; i++) Op($"reqw{i}", R, "worker request (0x00867570)");
        for (int i = 1; i <= 5; i++) Op($"reqe{i}", R, "worker request (0x008673d0)");
        Op("op_54", R, "halt the worker");
        Op("popa_jmp", OperandKind.Label, "pop into A; jump");
        Op("popa_jnz", OperandKind.Label, "pop into A; jump if A != 0");
        Op("popa_jz", OperandKind.Label, "pop into A; jump if A == 0");
        Op("func_facc", OperandKind.Func, "call native function, then pop its result into the float accumulator");
        for (int i = 0; i < 4; i++) Op($"popi{i}", R, $"pop into integer register {i}");
        for (int i = 0; i < 10; i++) Op($"popf{i}", R, $"pop into float register {i}");
        for (int i = 0; i < 4; i++) Op($"pushi{i}", R, $"push integer register {i}");
        for (int i = 0; i < 10; i++) Op($"pushf{i}", R, $"push float register {i}");
        Op("pushg", R, "push global constant (table 0x00c52704)");
        Op("nop_76", R, "no operation");
        Op("op_77", R, "pop 2; worker operation (0x00863570)");
        Op("op_78", R, "pop 2; worker operation (0x00863570)");
        Op("op_79", R, "pop 3; 0x00870d30");
        Op("op_7a", R, "pop 2; push 0x0086fab0");
        for (int i = 0x7b; i <= 0x7f; i++) Op($"op_{i:x2}", R, "invalid");

        if (all.Count != 128) throw new InvalidOperationException("the opcode table must have 128 entries");
        All = all;
        s_byName = all.ToDictionary(o => o.Name);
    }

    /// <summary>Looks up an opcode by name.</summary>
    public static Opcode? ByName(string name) => s_byName.GetValueOrDefault(name);
}
