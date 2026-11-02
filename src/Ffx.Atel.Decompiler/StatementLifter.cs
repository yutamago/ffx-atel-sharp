// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler;

/// <summary>
///     Stack code to statements: one statement is the code from an empty stack to the next empty
///     stack (docs/corpus.md, "Stack"), lifted into a tree by simulating the stack symbolically. A
///     statement is only taken if lowering it (<see cref="Lowering"/>) gives back exactly its
///     instructions; everything else stays assembly.
/// </summary>
internal sealed class StatementLifter(ScriptBlock block, int worker, IReadOnlyList<VarEntry> variables, NativeSignatures natives) {
    /// <summary>The copy <c>op_2b</c> pushes: legal only as the index of a compound element store.</summary>
    private sealed record Dup(Expr Inner) : Expr;

    /// <summary>The code of each instruction, with pool constants as values (by index where the pool has the value at a lower index too).</summary>
    public CodeOp Op(Instr i) => i is { Code: Opcodes.PushC or Opcodes.PushFC, Operand: ushort k } && !First(i.Code, k, Value(i.Code, k))
        ? new CodeOp(i.Code, k)
        : CodeOp.FromInstr(block, worker, i);

    private uint Value(byte code, ushort k) => code == Opcodes.PushC
        ? (uint)(block.IntConst(worker, k) ?? 0)
        : BitConverter.SingleToUInt32Bits(block.FloatConst(worker, k) ?? 0);

    /// <summary>Whether pool entry <paramref name="k"/> is the first with its value (the compiler gives a value its first index).</summary>
    private bool First(byte code, ushort k, uint value) {
        for (ushort j = 0; j < k; j++) {
            if (code == Opcodes.PushC ? block.IntConst(worker, j) is int a && (uint)a == value
                : block.FloatConst(worker, j) is float f && BitConverter.SingleToUInt32Bits(f) == value) return false;
        }
        return true;
    }

    /// <summary>
    ///     Splits <paramref name="code"/>[<paramref name="from"/>..<paramref name="to"/>) into statements:
    ///     (start, end) index ranges, end exclusive; null if the stack does not return to empty before
    ///     a label (<paramref name="boundary"/>) or the end.
    /// </summary>
    public List<(int Start, int End)>? Split(List<Instr> code, int from, int to, Func<int, bool> boundary) {
        List<(int, int)> result = [];
        Func<ushort, int?> argc = ConstantPools.Argc(natives);
        int start = from, depth = 0;
        for (int i = from; i < to; i++) {
            if (i > start && boundary(i)) return null;
            CodeOp op = new(code[i].Code, code[i].Operand);
            if (op.Code is Opcodes.Func or Opcodes.FuncFacc && natives[op.Operand ?? 0] is null) return null;
            (int pops, int pushes) = op.Code is Opcodes.Func or Opcodes.FuncFacc
                ? StackEffects.Native(op.Code, natives[op.Operand!.Value]!.Argc)
                : StackEffects.Of(op.Code);
            if (pops > depth) return null;
            depth = depth - pops + pushes;
            bool ends = !StackEffects.FallsThrough(op.Code) || StackEffects.Jumps(op.Code);
            if (ends && depth != 0) return null;
            if (depth == 0) {
                result.Add((start, i + 1));
                start = i + 1;
            }
        }
        return start == to ? result : null;
    }

    /// <summary>Lifts one statement's instructions; null where no statement gives them back.</summary>
    public Stmt? Lift(List<Instr> code, int start, int end) {
        Stmt? s;
        try {
            s = LiftRaw(code, start, end);
        }
        catch (InvalidOperationException) {
            return null;
        }
        if (s is null) return null;
        s = TwoD(s);
        List<CodeOp> want = [];
        for (int i = start; i < end; i++) want.Add(Op(code[i]));
        List<CodeOp> got;
        try {
            got = Lowering.Lower(s);
        }
        catch (ArgumentException) {
            return null;
        }
        return got.SequenceEqual(want) ? s : null;
    }

    private Stmt? LiftRaw(List<Instr> code, int start, int end) {
        Stack<Expr> stack = new();
        Expr Pop() => stack.Count > 0 ? stack.Pop() : throw new InvalidOperationException("underflow");
        VarEntry Var(ushort v, bool array) =>
            v < variables.Count && variables[v].IsArray == array ? variables[v] : throw new InvalidOperationException("variable kind");
        for (int i = start; i < end; i++) {
            Instr ins = code[i];
            byte op = ins.Code;
            ushort arg = ins.Operand ?? 0;
            bool last = i == end - 1;
            switch (op) {
                case Opcodes.PushImm: stack.Push(new IntLit((short)arg)); break;
                case Opcodes.PushC: {
                    int v = block.IntConst(worker, arg) ?? throw new InvalidOperationException();
                    stack.Push(First(Opcodes.PushC, arg, (uint)v) ? new IntLit(v) : new PoolExpr(Opcodes.PushC, arg, (uint)v));
                    break;
                }
                case Opcodes.PushFC: {
                    uint bits = BitConverter.SingleToUInt32Bits(block.FloatConst(worker, arg) ?? throw new InvalidOperationException());
                    stack.Push(First(Opcodes.PushFC, arg, bits) ? new FloatLit(bits) : new PoolExpr(Opcodes.PushFC, arg, bits));
                    break;
                }
                case Opcodes.PushV: Var(arg, false); stack.Push(new VarExpr(arg)); break;
                case Opcodes.PushVA: Var(arg, true); stack.Push(new ElemExpr(arg, Pop())); break;
                case Opcodes.PushVRef: Var(arg, true); stack.Push(new AddrExpr(new ElemExpr(arg, Pop()))); break;
                case >= Opcodes.PushI0 and < Opcodes.PushI0 + 4: stack.Push(new RegExpr(RegKind.Int, op - Opcodes.PushI0)); break;
                case >= Opcodes.PushF0 and < Opcodes.PushF0 + 10: stack.Push(new RegExpr(RegKind.Float, op - Opcodes.PushF0)); break;
                case Opcodes.PushA: stack.Push(new RegExpr(RegKind.A)); break;
                case Opcodes.PushB: stack.Push(new RegExpr(RegKind.B)); break;
                case Opcodes.PushFacc: stack.Push(new RegExpr(RegKind.Facc)); break;
                case Opcodes.Lor or Opcodes.Land or Opcodes.Or or Opcodes.Xor or Opcodes.And or Opcodes.Eq or Opcodes.Ne
                    or Opcodes.Gt or Opcodes.Lt or Opcodes.Ge or Opcodes.Le or Opcodes.Shl or Opcodes.Sar
                    or Opcodes.Add or Opcodes.Sub or Opcodes.Mul or Opcodes.Div or Opcodes.Mod: {
                    Expr r = Pop(), l = Pop();
                    stack.Push(new BinaryExpr(op, l, r));
                    break;
                }
                case Opcodes.Not or Opcodes.Bnot: stack.Push(new UnaryExpr(op, Pop())); break;
                case Opcodes.Op2b: {
                    Dup d = new(Pop());
                    stack.Push(d);
                    stack.Push(d);
                    break;
                }
                case Opcodes.Func: stack.Push(new CallExpr(arg, Args(arg))); break;

                // Statement ends.
                case Opcodes.FuncFacc when last: return Done(new CallStmt(arg, Args(arg)));
                case Opcodes.PopV or Opcodes.PopV1 when last: Var(arg, false); return Done(new AssignStmt(new VarExpr(arg), Pop(), op == Opcodes.PopV1));
                case Opcodes.PopVA1 when last: {
                    Var(arg, true);
                    Expr value = Pop(), index = Pop();
                    return index is Dup ? null : Done(new AssignStmt(new ElemExpr(arg, index), value, true));
                }
                case Opcodes.PopVA when last: {
                    Var(arg, true);
                    Expr value = Pop(), index = Pop();
                    if (index is Dup d) {
                        return value is BinaryExpr { Left: ElemExpr { Index: Dup d2 } e } b && ReferenceEquals(d, d2) && e.Var == arg && IsCompound(b.Op)
                            ? Done(new CompoundStmt(new ElemExpr(arg, d.Inner), b.Op, b.Right))
                            : null;
                    }
                    return Done(new AssignStmt(new ElemExpr(arg, index), value));
                }
                case >= Opcodes.PopI0 and < Opcodes.PopI0 + 4 when last: return Done(new AssignStmt(new RegExpr(RegKind.Int, op - Opcodes.PopI0), Pop()));
                case >= Opcodes.PopF0 and < Opcodes.PopF0 + 10 when last: return Done(new AssignStmt(new RegExpr(RegKind.Float, op - Opcodes.PopF0), Pop()));
                case Opcodes.PopA when last: return Done(new AssignStmt(new RegExpr(RegKind.A), Pop()));
                case Opcodes.PopB when last: return Done(new AssignStmt(new RegExpr(RegKind.B), Pop()));
                case Opcodes.PopFacc when last && i > start && IsRequest(code[i - 1].Code): {
                    Expr entry = Pop(), w = Pop(), level = Pop();
                    return Done(new RequestStmt(code[i - 1].Code, level, w, entry));
                }
                case var _ when IsRequest(op) && i == end - 2:
                    break;    // lifted with the popfacc that follows
                case Opcodes.Jmp when last: return Done(new GotoStmt(arg));
                case Opcodes.PopAJz when last: return Done(new CondGotoStmt(arg, Pop(), false));
                case Opcodes.PopAJnz when last: return Done(new CondGotoStmt(arg, Pop(), true));
                case Opcodes.End or Opcodes.Ret or Opcodes.Op54 or Opcodes.Op40 when last: return Done(new OpStmt(op));
                case Opcodes.EndP or Opcodes.EndP1 when last: return Done(new EndWithStmt(op, Pop()));
                case Opcodes.Nop76 when last && i == start: return new NopStmt(arg);
                case Opcodes.Call when last: return Done(new CallWorkerStmt(arg));
                case Opcodes.Op77 or Opcodes.Op78 or Opcodes.Op79 when last: {
                    Expr[] args = new Expr[op == Opcodes.Op79 ? 3 : 2];
                    for (int k = args.Length - 1; k >= 0; k--) args[k] = Pop();
                    return Done(new OpArgsStmt(op, args));
                }
                default: return null;
            }
        }
        return null;

        Stmt? Done(Stmt s) => stack.Count == 0 ? s : null;

        IReadOnlyList<Expr> Args(ushort id) {
            NativeSignature sig = natives[id] ?? throw new InvalidOperationException("unknown native");
            Expr[] args = new Expr[sig.Argc];
            for (int k = sig.Argc - 1; k >= 0; k--) args[k] = Pop();
            return args;
        }
    }

    // Requests: the forms the reference assembly has a method for (req0-5, reqw0-5, reqe0-5, and op_39 to a party member).
    private static bool IsRequest(byte op) => op is (>= Opcodes.Req0 and <= Opcodes.Op39) or (>= Opcodes.Req1 and <= 0x53);

    /// <summary>Opcodes C# has a compound assignment for.</summary>
    public static bool IsCompound(byte op) => op is Opcodes.Add or Opcodes.Sub or Opcodes.Mul or Opcodes.Div or Opcodes.Mod
        or Opcodes.And or Opcodes.Or or Opcodes.Xor or Opcodes.Shl or Opcodes.Sar;

    // ----- 2-D arrays ---------------------------------------------------------------------------

    /// <summary>Elements of 2-D arrays as <c>[row, col]</c> where the flat index has that shape (and lowers the same).</summary>
    private Stmt TwoD(Stmt s) => s switch {
        AssignStmt a => a with { Target = TwoD(a.Target), Value = TwoD(a.Value) },
        CompoundStmt c => new CompoundStmt(TwoD(c.Target), c.Op, TwoD(c.Value)),
        CallStmt c => new CallStmt(c.Native, [.. c.Args.Select(TwoD)]),
        RequestStmt r => new RequestStmt(r.Op, TwoD(r.Level), TwoD(r.Worker), TwoD(r.Entry)),
        CondGotoStmt g => g with { Condition = TwoD(g.Condition) },
        EndWithStmt e => e with { Value = TwoD(e.Value) },
        OpArgsStmt x => new OpArgsStmt(x.Op, [.. x.Args.Select(TwoD)]),
        _ => s,
    };

    private Expr TwoD(Expr e) {
        switch (e) {
            case ElemExpr el: {
                Expr index = TwoD(el.Index);
                int n = variables[el.Var].RowLength;
                if (n > 0 && Shape(el.Var, index, n) is Elem2Expr two) return two;
                return new ElemExpr(el.Var, index);
            }
            case AddrExpr a: return new AddrExpr(TwoD(a.Element));
            case UnaryExpr u: return u with { Operand = TwoD(u.Operand) };
            case BinaryExpr b: return b with { Left = TwoD(b.Left), Right = TwoD(b.Right) };
            case CallExpr c: return new CallExpr(c.Native, [.. c.Args.Select(TwoD)]);
            default: return e;
        }
    }

    /// <summary><c>col + row * n</c>, <c>col + k*n</c> or a constant, as a 2-D element that lowers to the same code.</summary>
    private static Elem2Expr? Shape(int var, Expr index, int n) {
        Elem2Expr? candidate = index switch {
            IntLit { Value: >= 0 } k => new Elem2Expr(var, new IntLit(k.Value / n), new IntLit(k.Value % n), n),
            BinaryExpr { Op: Opcodes.Add, Right: BinaryExpr { Op: Opcodes.Mul, Right: IntLit m } mul } add when m.Value == n
                => new Elem2Expr(var, mul.Left, add.Left, n),
            BinaryExpr { Op: Opcodes.Add, Left: not IntLit, Right: IntLit { Value: > 0 } k } add when k.Value % n == 0
                => new Elem2Expr(var, new IntLit(k.Value / n), add.Left, n),
            _ => null,
        };
        if (candidate is null) return null;
        List<CodeOp> want = [], got = [];
        Lowering.Lower(index, want);
        Lowering.Lower(Lowering.Flat(candidate).Index, got);
        return want.SequenceEqual(got) ? candidate : null;
    }
}
