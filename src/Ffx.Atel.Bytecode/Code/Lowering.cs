// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Bytecode.Code;

/// <summary>
///     One instruction before the constant pools are laid out: a <c>pushc</c> or <c>pushfc</c> that
///     is <see cref="Pooled"/> carries its value (<see cref="Value"/>: the integer, or the float's
///     bits) and gets its index later; every other instruction has its final operand, but one whose
///     <see cref="Symbol"/> is set (a <see cref="SymbolExpr"/>), whose operand the mod linker gives.
/// </summary>
public readonly record struct CodeOp(byte Code, ushort? Operand, uint Value = 0, bool Pooled = false, object? Symbol = null) {
    public static CodeOp Of(byte code) => new(code, null);

    public static CodeOp Of(byte code, int operand) => new(code, (ushort)operand);

    /// <summary>The instruction, with <paramref name="index"/> as a pooled push's operand.</summary>
    public Instr ToInstr(ushort? index = null) => new(0, Code, Pooled ? index : Operand);

    /// <summary>An instruction of a script, with its pool constant resolved to a value.</summary>
    public static CodeOp FromInstr(ScriptBlock block, int worker, Instr i) => i switch {
        { Code: Opcodes.PushC, Operand: ushort k } when block.IntConst(worker, k) is int v => new(Opcodes.PushC, null, (uint)v, true),
        { Code: Opcodes.PushFC, Operand: ushort k } when block.FloatConst(worker, k) is float f => new(Opcodes.PushFC, null, BitConverter.SingleToUInt32Bits(f), true),
        _ => new(i.Code, i.Operand),
    };
}

/// <summary>Statements to instructions: the one encoding each <see cref="Stmt"/> and <see cref="Expr"/> has.</summary>
public static class Lowering {
    public static List<CodeOp> Lower(Stmt s) {
        List<CodeOp> o = [];
        Lower(s, o);
        return o;
    }

    public static void Lower(Stmt s, List<CodeOp> o) {
        switch (s) {
            case AssignStmt a:
                switch (a.Target) {
                    case VarExpr v:
                        Lower(a.Value, o);
                        o.Add(CodeOp.Of(a.Clamped ? Opcodes.PopV1 : Opcodes.PopV, v.Var));
                        break;
                    case ElemExpr or Elem2Expr:
                        (int var, Expr index) = Flat(a.Target);
                        Lower(index, o);
                        Lower(a.Value, o);
                        o.Add(CodeOp.Of(a.Clamped ? Opcodes.PopVA1 : Opcodes.PopVA, var));
                        break;
                    case LocalExpr l when !a.Clamped:
                        o.Add(new CodeOp(Opcodes.Nop, null, Symbol: new LocalStoreMark(l.Slot)));
                        Lower(a.Value, o);
                        o.Add(new CodeOp(Opcodes.PopV, 0, Symbol: l.Slot));
                        break;
                    case RegExpr when a.Clamped:
                        throw new ArgumentException("a register has no clamped store");
                    case RegExpr r:
                        Lower(a.Value, o);
                        o.Add(CodeOp.Of(Pop(r)));
                        break;
                    default:
                        throw new ArgumentException($"cannot assign to {a.Target}");
                }
                break;
            case CompoundStmt c: {
                if (c.Target is not (ElemExpr or Elem2Expr)) {
                    Lower(new AssignStmt(c.Target, new BinaryExpr(c.Op, c.Target, c.Value)), o);
                    break;
                }
                (int var, Expr index) = Flat(c.Target);
                Lower(index, o);
                o.Add(CodeOp.Of(Opcodes.Op2b));
                o.Add(CodeOp.Of(Opcodes.PushVA, var));
                Lower(c.Value, o);
                o.Add(CodeOp.Of(c.Op));
                o.Add(CodeOp.Of(Opcodes.PopVA, var));
                break;
            }
            case CallStmt c:
                foreach (Expr a in c.Args) Lower(a, o);
                o.Add(CodeOp.Of(Opcodes.FuncFacc, c.Native));
                break;
            case RequestStmt r:
                Lower(r.Level, o);
                Lower(r.Worker, o);
                Lower(r.Entry, o);
                o.Add(CodeOp.Of(r.Op));
                o.Add(CodeOp.Of(Opcodes.PopFacc));
                break;
            case CallWorkerStmt c:
                o.Add(CodeOp.Of(Opcodes.Call, c.Worker));
                break;
            case GotoStmt g:
                o.Add(CodeOp.Of(Opcodes.Jmp, g.Label));
                break;
            case CondGotoStmt g:
                Lower(g.Condition, o);
                o.Add(CodeOp.Of(g.IfTrue ? Opcodes.PopAJnz : Opcodes.PopAJz, g.Label));
                break;
            case OpStmt x:
                o.Add(CodeOp.Of(x.Op));
                break;
            case NopStmt n:
                o.Add(CodeOp.Of(Opcodes.Nop76, n.Operand));
                break;
            case OpArgsStmt x:
                foreach (Expr a in x.Args) Lower(a, o);
                o.Add(CodeOp.Of(x.Op));
                break;
            case EndWithStmt e:
                Lower(e.Value, o);
                o.Add(CodeOp.Of(e.Op));
                break;
            default:
                throw new ArgumentException($"unknown statement {s}");
        }
    }

    public static void Lower(Expr e, List<CodeOp> o) {
        switch (e) {
            case IntLit i:
                o.Add(i.Value is >= short.MinValue and <= short.MaxValue
                    ? CodeOp.Of(Opcodes.PushImm, (ushort)(short)i.Value)
                    : new CodeOp(Opcodes.PushC, null, (uint)i.Value, true));
                break;
            case FloatLit f:
                o.Add(new CodeOp(Opcodes.PushFC, null, f.Bits, true));
                break;
            case PoolExpr p:
                o.Add(CodeOp.Of(p.Code, p.Index));
                break;
            case SymbolExpr y:
                o.Add(new CodeOp(Opcodes.PushImm, 0, Symbol: y.Symbol));
                break;
            case VarExpr v:
                o.Add(CodeOp.Of(Opcodes.PushV, v.Var));
                break;
            case ElemExpr or Elem2Expr: {
                (int var, Expr index) = Flat(e);
                Lower(index, o);
                o.Add(CodeOp.Of(Opcodes.PushVA, var));
                break;
            }
            case AddrExpr a: {
                (int var, Expr index) = Flat(a.Element);
                Lower(index, o);
                o.Add(CodeOp.Of(Opcodes.PushVRef, var));
                break;
            }
            case RegExpr r:
                o.Add(CodeOp.Of(Push(r)));
                break;
            case UnaryExpr u:
                Lower(u.Operand, o);
                o.Add(CodeOp.Of(u.Op));
                break;
            case BinaryExpr b:
                Lower(b.Left, o);
                Lower(b.Right, o);
                o.Add(CodeOp.Of(b.Op));
                break;
            case CallExpr c:
                foreach (Expr a in c.Args) Lower(a, o);
                o.Add(CodeOp.Of(Opcodes.Func, c.Native));
                break;
            case LocalExpr l:
                o.Add(new CodeOp(Opcodes.PushV, 0, Symbol: l.Slot));
                break;
            case AndAlsoExpr or OrElseExpr:
                throw new ArgumentException("&& and || branch: they are only conditions of if, the loops and if (…) goto");
            default:
                throw new ArgumentException($"unknown expression {e}");
        }
    }

    /// <summary>
    ///     An element's variable and flat index. For a 2-D element: <c>col + row * n</c>; a constant
    ///     row folds into <c>col + k</c> (only <c>col</c> when <c>k</c> is 0), both constant into one.
    /// </summary>
    public static (int Var, Expr Index) Flat(Expr element) => element switch {
        ElemExpr e => (e.Var, e.Index),
        Elem2Expr { Row: IntLit r, Col: IntLit c } e => (e.Var, new IntLit(unchecked(r.Value * e.RowLength + c.Value))),
        Elem2Expr { Row: IntLit r } e => (e.Var, unchecked(r.Value * e.RowLength) == 0 ? e.Col
            : new BinaryExpr(Opcodes.Add, e.Col, new IntLit(unchecked(r.Value * e.RowLength)))),
        Elem2Expr e => (e.Var, new BinaryExpr(Opcodes.Add, e.Col, new BinaryExpr(Opcodes.Mul, e.Row, new IntLit(e.RowLength)))),
        _ => throw new ArgumentException($"not an array element: {element}"),
    };

    public static byte Push(RegExpr r) => r.Kind switch {
        RegKind.Int => (byte)(Opcodes.PushI0 + r.Number),
        RegKind.Float => (byte)(Opcodes.PushF0 + r.Number),
        RegKind.A => Opcodes.PushA,
        RegKind.B => Opcodes.PushB,
        _ => Opcodes.PushFacc,
    };

    public static byte Pop(RegExpr r) => r.Kind switch {
        RegKind.Int => (byte)(Opcodes.PopI0 + r.Number),
        RegKind.Float => (byte)(Opcodes.PopF0 + r.Number),
        RegKind.A => Opcodes.PopA,
        RegKind.B => Opcodes.PopB,
        _ => throw new ArgumentException("the float accumulator is only written by call statements and requests"),
    };
}
