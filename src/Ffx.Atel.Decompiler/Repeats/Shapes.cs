// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;

namespace Ffx.Atel.Decompiler.Repeats;

// The shape of a statement (docs/research/repeats.md, "Method"): its tree with every constant, and with
// --vars every variable, replaced by a hole. Two statements of the same shape differ only in the values
// in their holes, so a run of them is one inline method with a parameter per hole whose value varies.
// One statement is a fixed instruction sequence (Statements.cs), so filling the holes back gives the
// same bytes. Statements inside a statement count by their own shape, interned, so shapes compare as ints.

/// <summary>What a statement does that an inline method may not today (docs/language.md, "Inline methods"), and what it is.</summary>
[Flags]
internal enum Traits {
    None = 0,
    /// <summary>Holds <c>if</c>, a loop or <c>switch</c>.</summary>
    Control = 1,
    /// <summary>Holds a dead <c>if (false)</c> block.</summary>
    Dead = 2,
    /// <summary>Calls a native that waits.</summary>
    Waits = 4,
    /// <summary>Assigns a variable (<c>V1 = …</c>, <c>V2[i] += …</c>).</summary>
    Assigns = 8,
    /// <summary>Reads or writes a register (a local: <c>i0</c>, <c>f1</c>, register A or B).</summary>
    Registers = 16,
    /// <summary>Reads a variable.</summary>
    ReadsVars = 32,
    /// <summary>Requests an entry or calls a function worker.</summary>
    Requests = 64,
    /// <summary>An opcode statement with operands (<c>op_77</c>…), no native.</summary>
    Ops = 128,
}

/// <summary>What the miner keeps of one statement.</summary>
/// <param name="Id">The shape, interned; -1 for a statement no inline method can hold (a jump, a label, <c>Asm</c>, an end).</param>
/// <param name="EscapeKinds">The <c>break</c>, <c>continue</c> and <c>goto case</c> (bits 1, 2, 4) that leave it: a helper cannot hold it alone.</param>
/// <param name="Size">Statements in it, nested ones included (<see cref="Inline.Count"/>).</param>
/// <param name="Lines">Lines the decompiler writes for it.</param>
internal readonly record struct StmtInfo(int Id, byte EscapeKinds, int Size, int Lines, Traits Traits) {
    /// <summary>Whether a jump leaves it (<see cref="EscapeKinds"/>).</summary>
    public bool Escapes => EscapeKinds != 0;
}

/// <summary>The shapes seen so far: one number per distinct shape (thread-safe).</summary>
internal sealed class ShapeTable {
    private readonly ConcurrentDictionary<string, int> _ids = new(StringComparer.Ordinal);
    private int _next = -1;

    public int Id(string key) => _ids.GetOrAdd(key, _ => Interlocked.Increment(ref _next));

    public int Count => _ids.Count;
}

/// <summary>Where a block of statements is: its parent statement, and which of its blocks it is.</summary>
internal sealed class BlockRecord {
    public int Parent = -1;
    public int ParentAt;
    public int Ordinal;
    public int Method;
    /// <summary>Inside a dead <c>if (false)</c> (the decompiler folds nothing there).</summary>
    public bool Dead;
    public StmtInfo[] Stmts = [];
    public int[] HoleFrom = [];
    public int[] HoleTo = [];
    /// <summary>The block's statements as walked.</summary>
    public IReadOnlyList<Stmt> Source = [];
}

/// <summary>
///     Walks statements in one fixed order, writing each one's shape and the values in its holes; with a
///     <c>replace</c> function it also rebuilds them with holes replaced (an inline method's template).
/// </summary>
internal sealed class ShapeWalker(ShapeTable table, NativeSignatures natives, bool vars, Func<int, Expr, Expr?>? replace = null) {
    public const long TagInt = 1, TagFloat = 2, TagPool = 3, TagVar = 4, TagElem = 5, TagVarTarget = 6, TagElemTarget = 7;

    /// <summary>The value in each hole, in walk order: the tag in the top byte (<see cref="TagInt"/>…).</summary>
    public List<long> Holes { get; } = [];

    /// <summary>The blocks met, when <see cref="WalkMethod"/> registers them; the first is the method's body.</summary>
    public List<BlockRecord> Blocks { get; } = [];

    private bool Rebuild => replace is not null;
    private int _block = -1, _at;
    private bool _dead;
    private int _method;

    /// <summary>A method's body and every block in it, registered in <see cref="Blocks"/>.</summary>
    public void WalkMethod(IReadOnlyList<Stmt> body, int method) {
        _method = method;
        _block = -1;
        _dead = false;
        List(body, register: true, ordinal: 0);
    }

    /// <summary>Statements rebuilt with the holes <c>replace</c> gives (a template), and their infos; no blocks registered.</summary>
    public (List<Stmt> Stmts, StmtInfo[] Infos) Rewrite(IReadOnlyList<Stmt> list) {
        (IReadOnlyList<Stmt> r, StmtInfo[] i) = List(list, register: false, ordinal: 0);
        return ([.. r], i);
    }

    private (IReadOnlyList<Stmt>, StmtInfo[]) List(IReadOnlyList<Stmt> list, bool register, int ordinal) {
        BlockRecord? rec = null;
        int saveBlock = _block, saveAt = _at;
        if (register) {
            rec = new BlockRecord { Parent = _block, ParentAt = _at, Ordinal = ordinal, Method = _method, Dead = _dead, HoleFrom = new int[list.Count], HoleTo = new int[list.Count], Source = list };
            _block = Blocks.Count;
            Blocks.Add(rec);
        }
        StmtInfo[] infos = new StmtInfo[list.Count];
        List<Stmt>? rebuilt = Rebuild ? new(list.Count) : null;
        for (int k = 0; k < list.Count; k++) {
            if (register) {
                _at = k;
                rec!.HoleFrom[k] = Holes.Count;
            }
            (Stmt s, StmtInfo info) = Statement(list[k]);
            infos[k] = info;
            rebuilt?.Add(s);
            if (register) rec!.HoleTo[k] = Holes.Count;
        }
        if (rec is not null) rec.Stmts = infos;
        _block = saveBlock;
        _at = saveAt;
        return (rebuilt ?? list, infos);
    }

    // ----- Statements ------------------------------------------------------------------------------

    private enum Esc { None = 0, Break = 1, Continue = 2, GotoCase = 4 }

    private (Stmt, StmtInfo) Statement(Stmt s) {
        StringBuilder k = new();
        Traits t = Traits.None;
        Esc esc = Esc.None;
        bool jumps = false;
        int size = 1, lines = 1;
        Stmt r = s;

        // A nested block: its statements' shapes by id, its traits, escapes and size.
        (IReadOnlyList<Stmt> List, int Lines) Nested(IReadOnlyList<Stmt> list, bool register, int ordinal, bool dead = false) {
            bool saveDead = _dead;
            _dead |= dead;
            (IReadOnlyList<Stmt> rl, StmtInfo[] infos) = List(list, register, ordinal);
            _dead = saveDead;
            k.Append('{');
            int n = 0;
            foreach (StmtInfo i in infos) {
                k.Append(i.Id).Append(';');
                t |= i.Traits;
                size += i.Size;
                n += i.Lines;
                if (i.Id < 0) jumps = true;
                esc |= (Esc)i.EscapeKinds;
            }
            k.Append('}');
            return (rl, n);
        }

        switch (s) {
            case AssignStmt x: {
                k.Append('A').Append(x.Clamped ? 'c' : '_');
                Expr target = Ex(x.Target, k, ref t, target: true);
                k.Append('=');
                Expr value = Ex(x.Value, k, ref t);
                if (Rebuild) r = new AssignStmt(target, value, x.Clamped);
                break;
            }
            case CompoundStmt x: {
                k.Append('K').Append(x.Op);
                Expr target = Ex(x.Target, k, ref t, target: true);
                k.Append('=');
                Expr value = Ex(x.Value, k, ref t);
                if (Rebuild) r = new CompoundStmt(target, x.Op, value);
                break;
            }
            case CallStmt x: {
                k.Append('S').Append(x.Native);
                if (natives[x.Native]?.Polls ?? true) t |= Traits.Waits;
                List<Expr> args = Args(x.Args, k, ref t);
                if (Rebuild) r = new CallStmt(x.Native, args);
                break;
            }
            case RequestStmt x: {
                t |= Traits.Requests;
                k.Append('R').Append(x.Op).Append('(');
                Expr level = Ex(x.Level, k, ref t);
                k.Append(',');
                Expr worker = Ex(x.Worker, k, ref t);
                k.Append(',');
                Expr entry = Ex(x.Entry, k, ref t);
                k.Append(')');
                if (Rebuild) r = new RequestStmt(x.Op, level, worker, entry);
                break;
            }
            case CallWorkerStmt x:
                t |= Traits.Requests;
                k.Append('W').Append(x.Worker);
                break;
            case OpArgsStmt x: {
                t |= Traits.Ops;
                k.Append('X').Append(x.Op);
                List<Expr> args = Args(x.Args, k, ref t);
                if (Rebuild) r = new OpArgsStmt(x.Op, args);
                break;
            }
            case NopStmt x:
                k.Append('N').Append(x.Operand);
                break;
            case IfStmt x: {
                t |= Traits.Control;
                Expr? cond = null;
                if (x.Condition is null) {
                    t |= Traits.Dead;
                    k.Append("F").Append(x.Cut);
                }
                else {
                    k.Append('I');
                    cond = Ex(x.Condition, k, ref t);
                }
                // The dead branch of `if (false)` is the then-block; its else is live.
                (IReadOnlyList<Stmt> then, int thenLines) = Nested(x.Then, true, 0, dead: x.Condition is null);
                lines = 3 + thenLines;
                IReadOnlyList<Stmt>? other = null;
                if (x.Else is not null) {
                    k.Append("else");
                    (other, int elseLines) = Nested(x.Else, true, 1);
                    // `else if` writes the nested if's lines; a plain else adds "else", braces.
                    lines += x.Else is [IfStmt] ? elseLines : 3 + elseLines;
                }
                if (Rebuild) r = new IfStmt(cond, then, other) { Cut = x.Cut };
                break;
            }
            case WhileStmt x: {
                t |= Traits.Control;
                k.Append('L');
                Expr? cond = x.Condition is null ? null : Ex(x.Condition, k, ref t);
                (IReadOnlyList<Stmt> body, int bodyLines) = Nested(x.Body, true, 0);
                esc &= ~(Esc.Break | Esc.Continue);
                lines = 3 + bodyLines;
                if (Rebuild) r = new WhileStmt(cond, body);
                break;
            }
            case DoStmt x: {
                t |= Traits.Control;
                k.Append('D');
                (IReadOnlyList<Stmt> body, int bodyLines) = Nested(x.Body, true, 0);
                esc &= ~(Esc.Break | Esc.Continue);
                Expr cond = Ex(x.Condition, k, ref t);
                lines = 3 + bodyLines;
                if (Rebuild) r = new DoStmt(body, cond);
                break;
            }
            case ForStmt x: {
                t |= Traits.Control;
                k.Append('O');
                (IReadOnlyList<Stmt> init, _) = Nested(x.Init, false, 0);
                Expr? cond = x.Condition is null ? null : Ex(x.Condition, k, ref t);
                (IReadOnlyList<Stmt> step, _) = Nested(x.Step, false, 0);
                (IReadOnlyList<Stmt> body, int bodyLines) = Nested(x.Body, true, 0);
                esc &= ~(Esc.Break | Esc.Continue);
                lines = 3 + bodyLines;
                if (Rebuild) r = new ForStmt(init, cond, step, body);
                break;
            }
            case SwitchStmt x: {
                t |= Traits.Control;
                k.Append('Z');
                Expr sel = Ex(x.Selector, k, ref t);
                List<SwitchSection> sections = [];
                lines = 3;
                for (int c = 0; c < x.Sections.Count; c++) {
                    SwitchSection sec = x.Sections[c];
                    k.Append(sec.Label is CaseLabel l ? string.Create(CultureInfo.InvariantCulture, $"c{(int)l.Kind}:{l.Value}:{l.High}") : "d");
                    (IReadOnlyList<Stmt> body, int bodyLines) = Nested(sec.Body, true, c);
                    lines += 1 + bodyLines;
                    sections.Add(new SwitchSection(sec.Label, body));
                }
                esc &= ~(Esc.Break | Esc.GotoCase);
                if (Rebuild) r = new SwitchStmt(sel, sections);
                break;
            }
            case InlineStmt x: {
                // A call of an inline method folded before (Dialogue.Say, Turn.To): one statement, its arguments the holes.
                if (x.Macro.Waits) t |= Traits.Waits;
                k.Append('M').Append(x.Macro.Name);
                List<Expr> args = Args(x.Args, k, ref t);
                if (Rebuild) r = new InlineStmt(x.Macro, args, Inline.Expand(x.Macro, args));
                break;
            }
            case BreakStmt:
                k.Append('B');
                esc |= Esc.Break;
                break;
            case ContinueStmt:
                k.Append('C');
                esc |= Esc.Continue;
                break;
            case GotoCaseStmt x:
                k.Append('G').Append(x.Case);
                esc |= Esc.GotoCase;
                break;
            default:
                // Jumps, labels, Asm, Entry(n), end/return, and inline calls: no helper holds them.
                jumps = true;
                break;
        }
        int id = jumps ? -1 : table.Id(k.ToString());
        return (r, new StmtInfo(id, (byte)esc, size, lines, t));
    }

    private List<Expr> Args(IReadOnlyList<Expr> args, StringBuilder k, ref Traits t) {
        List<Expr> result = new(args.Count);
        k.Append('(');
        foreach (Expr a in args) {
            result.Add(Ex(a, k, ref t));
            k.Append(',');
        }
        k.Append(')');
        return result;
    }

    // ----- Expressions -----------------------------------------------------------------------------

    private Expr Hole(Expr e, long tag, long value) {
        int index = Holes.Count;
        Holes.Add((tag << 56) | (value & 0x00ff_ffff_ffff_ffffL));
        return replace?.Invoke(index, e) ?? e;
    }

    private Expr Ex(Expr e, StringBuilder k, ref Traits t, bool target = false) {
        switch (e) {
            case IntLit x:
                k.Append('i');
                return Hole(e, TagInt, (uint)x.Value);
            case FloatLit x:
                k.Append('f');
                return Hole(e, TagFloat, x.Bits);
            case PoolExpr x:
                k.Append('p').Append(x.Code);
                return Hole(e, TagPool, ((long)x.Code << 48) | ((long)x.Index << 32) | x.Value);
            case VarExpr x:
                t |= target ? Traits.Assigns : Traits.ReadsVars;
                if (vars) {
                    k.Append(target ? 'T' : 'v');
                    return Hole(e, target ? TagVarTarget : TagVar, x.Var);
                }
                k.Append('v').Append(x.Var);
                return e;
            case ElemExpr or Elem2Expr when vars && Inline.IsArgument(e):
                // The element as a whole is the argument (V63[V192]): an array is no value of its own.
                t |= (target ? Traits.Assigns : Traits.None) | Traits.ReadsVars;
                k.Append(target ? 'E' : 'e');
                return Hole(e, target ? TagElemTarget : TagElem, StableHash(e.ToString()));
            case ElemExpr x: {
                t |= (target ? Traits.Assigns : Traits.None) | Traits.ReadsVars;
                k.Append('e').Append(x.Var).Append('[');
                Expr index = Ex(x.Index, k, ref t);
                k.Append(']');
                return Rebuild ? new ElemExpr(x.Var, index) : e;
            }
            case Elem2Expr x: {
                t |= (target ? Traits.Assigns : Traits.None) | Traits.ReadsVars;
                k.Append('e').Append(x.Var).Append('[');
                Expr row = Ex(x.Row, k, ref t);
                k.Append(',');
                Expr col = Ex(x.Col, k, ref t);
                k.Append(']').Append(x.RowLength);
                return Rebuild ? new Elem2Expr(x.Var, row, col, x.RowLength) : e;
            }
            case AddrExpr x: {
                k.Append('&');
                Expr el = Ex(x.Element, k, ref t);
                return Rebuild ? new AddrExpr(el) : e;
            }
            case RegExpr x:
                if (x.Kind != RegKind.Facc) t |= Traits.Registers;
                k.Append('r').Append((int)x.Kind).Append('.').Append(x.Number);
                return e;
            case UnaryExpr x: {
                k.Append('u').Append(x.Op).Append('(');
                Expr o = Ex(x.Operand, k, ref t);
                k.Append(')');
                return Rebuild ? new UnaryExpr(x.Op, o) : e;
            }
            case BinaryExpr x: {
                k.Append('b').Append(x.Op).Append('(');
                Expr l = Ex(x.Left, k, ref t);
                k.Append(',');
                Expr rr = Ex(x.Right, k, ref t);
                k.Append(')');
                return Rebuild ? new BinaryExpr(x.Op, l, rr) : e;
            }
            case CallExpr x: {
                if (natives[x.Native]?.Polls ?? true) t |= Traits.Waits;
                k.Append('c').Append(x.Native);
                List<Expr> args = Args(x.Args, k, ref t);
                return Rebuild ? new CallExpr(x.Native, args) : e;
            }
            case AndAlsoExpr x: {
                k.Append("&&(");
                Expr l = Ex(x.Left, k, ref t);
                k.Append(',');
                Expr rr = Ex(x.Right, k, ref t);
                k.Append(')');
                return Rebuild ? new AndAlsoExpr(l, rr) : e;
            }
            case OrElseExpr x: {
                k.Append("||(");
                Expr l = Ex(x.Left, k, ref t);
                k.Append(',');
                Expr rr = Ex(x.Right, k, ref t);
                k.Append(')');
                return Rebuild ? new OrElseExpr(l, rr) : e;
            }
            default:
                k.Append('?').Append(e.GetType().Name);
                return e;
        }
    }

    /// <summary>FNV-1a, 56 bits: the same in every run (string hashes are not).</summary>
    internal static long StableHash(string s) {
        ulong h = 14695981039346656037UL;
        foreach (char c in s) {
            h ^= c;
            h *= 1099511628211UL;
        }
        return (long)(h & 0x00ff_ffff_ffff_ffffUL);
    }
}
