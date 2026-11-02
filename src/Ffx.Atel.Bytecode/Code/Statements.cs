// SPDX-License-Identifier: MIT

namespace Ffx.Atel.Bytecode.Code;

// The statements and expressions of Atel source, between the bytecode and C#: the decompiler lifts
// stack code into them and prints them as C#; the compiler reads C# into them and lowers them to
// bytecode (Lowering). One statement is the code from an empty stack to the next empty stack
// (docs/corpus.md, "Stack"), so every node maps to a fixed instruction sequence.

/// <summary>An expression: code that leaves one value on the stack.</summary>
public abstract record Expr;

/// <summary>An integer constant: <c>pushimm</c> when it fits <c>i16</c>, else <c>pushc</c>.</summary>
public sealed record IntLit(int Value) : Expr;

/// <summary>A float constant, <c>pushfc</c>, by its bits.</summary>
public sealed record FloatLit(uint Bits) : Expr {
    public float Value => BitConverter.UInt32BitsToSingle(Bits);
}

/// <summary>
///     A constant by its pool index (<c>pushc</c>, <c>pushfc</c>), for a pool that holds the value twice and
///     code that uses the later copy; <see cref="Value"/> is the integer or the float's bits.
/// </summary>
public sealed record PoolExpr(byte Code, ushort Index, uint Value) : Expr;

/// <summary>
///     A number only the mod linker knows (a new worker's or entry's): <c>pushimm</c> with an operand it
///     fills in. <see cref="Symbol"/> says which, for the compiler of mod packages.
/// </summary>
public sealed record SymbolExpr(object Symbol) : Expr;

/// <summary>A variable that is not an array: <c>pushv</c>.</summary>
public sealed record VarExpr(int Var) : Expr;

/// <summary>An array element by its flat index: <c>pushva</c>.</summary>
public sealed record ElemExpr(int Var, Expr Index) : Expr;

/// <summary>
///     An element of a 2-D array (<c>T[,]</c>, the variable table's row length): its flat index is
///     <c>col + row * RowLength</c>, folded when the row is a constant (<see cref="Lowering"/>).
/// </summary>
public sealed record Elem2Expr(int Var, Expr Row, Expr Col, int RowLength) : Expr;

/// <summary>The address of an array element, <c>pushvref</c>: <see cref="Element"/> is an <see cref="ElemExpr"/> or <see cref="Elem2Expr"/>.</summary>
public sealed record AddrExpr(Expr Element) : Expr;

/// <summary>The registers and the float accumulator.</summary>
public enum RegKind {
    /// <summary>Integer registers 0-3 (<c>pushi0</c>, <c>popi0</c>, …).</summary>
    Int,
    /// <summary>Float registers 0-9.</summary>
    Float,
    /// <summary>Register A (<c>pusha</c>, <c>popa</c>; the conditional jumps pop into it).</summary>
    A,
    /// <summary>Register B (<c>pushb</c>, <c>popb</c>; the switch selector).</summary>
    B,
    /// <summary>The float accumulator (<c>pushfacc</c>): a call statement's result.</summary>
    Facc,
}

/// <summary>A register read: <c>pushi0</c>…, <c>pushf0</c>…, <c>pusha</c>, <c>pushb</c>, <c>pushfacc</c>.</summary>
public sealed record RegExpr(RegKind Kind, int Number = 0) : Expr;

/// <summary><c>not</c>, <c>bnot</c> or <c>neg</c> on one operand.</summary>
public sealed record UnaryExpr(byte Op, Expr Operand) : Expr;

/// <summary>A two-operand opcode (<c>lor</c> … <c>mod</c>): left pushed first.</summary>
public sealed record BinaryExpr(byte Op, Expr Left, Expr Right) : Expr;

/// <summary>A native call whose result is used: arguments, then <c>func</c>.</summary>
public sealed record CallExpr(ushort Native, IReadOnlyList<Expr> Args) : Expr;

/// <summary>A statement: code from an empty stack to an empty stack.</summary>
public abstract record Stmt;

/// <summary>
///     <c>target = value</c>: value, then <c>popv</c>/<c>popi0</c>/…; for an element the index is
///     pushed first and <c>popva</c> stores. Targets: <see cref="VarExpr"/>, <see cref="ElemExpr"/>,
///     <see cref="Elem2Expr"/>, <see cref="RegExpr"/> (not <see cref="RegKind.Facc"/>). <see cref="Clamped"/>
///     stores with mode 1 (<c>popv_1</c>, <c>popva_1</c>): the value is clamped to the variable's range.
/// </summary>
public sealed record AssignStmt(Expr Target, Expr Value, bool Clamped = false) : Stmt;

/// <summary>
///     <c>a[i] op= value</c> on an array element: the index once, <c>op_2b</c> to keep a copy,
///     <c>pushva</c>, value, op, <c>popva</c>. (On a variable or register, <c>x op= v</c> is the same code
///     as <c>x = x op v</c>, an <see cref="AssignStmt"/>.)
/// </summary>
public sealed record CompoundStmt(Expr Target, byte Op, Expr Value) : Stmt;

/// <summary>A native call as a statement: arguments, then <c>func_facc</c>.</summary>
public sealed record CallStmt(ushort Native, IReadOnlyList<Expr> Args) : Stmt;

/// <summary>A request to a worker's entry: level, worker, entry, the request opcode (or <c>op_39</c>, a party member's worker by slot), <c>popfacc</c>.</summary>
public sealed record RequestStmt(byte Op, Expr Level, Expr Worker, Expr Entry) : Stmt;

/// <summary><c>call wN</c>: runs function worker N on this worker.</summary>
public sealed record CallWorkerStmt(int Worker) : Stmt;

/// <summary><c>jmp lN</c>.</summary>
public sealed record GotoStmt(int Label) : Stmt;

/// <summary>The condition, then <c>popa_jnz</c> (<see cref="IfTrue"/>) or <c>popa_jz</c>.</summary>
public sealed record CondGotoStmt(int Label, Expr Condition, bool IfTrue) : Stmt;

/// <summary>An instruction without operands that is a statement on its own: <c>end</c>, <c>ret</c>, <c>op_54</c>, <c>op_40</c>.</summary>
public sealed record OpStmt(byte Op) : Stmt;

/// <summary>An opcode that pops its operands and pushes nothing (<c>op_77</c>, <c>op_78</c>, <c>op_79</c>): the operands, then it.</summary>
public sealed record OpArgsStmt(byte Op, IReadOnlyList<Expr> Args) : Stmt;

/// <summary><c>nop_76</c> with its operand: no operation (a marker the original compiler left).</summary>
public sealed record NopStmt(ushort Operand) : Stmt;

/// <summary>A value, then <c>endp</c> or <c>endp_1</c>.</summary>
public sealed record EndWithStmt(byte Op, Expr Value) : Stmt;
