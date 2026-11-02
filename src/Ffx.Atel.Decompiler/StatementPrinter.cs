// SPDX-License-Identifier: MIT

using System.Globalization;
using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler;

/// <summary>The C# type an expression has in the source.</summary>
internal enum CType { Bool, Byte, SByte, UShort, Short, Int, Float }

/// <summary>A statement the printer has no C# for; the decompiler keeps its assembly.</summary>
internal sealed class NotPrintableException(string message) : Exception(message);

/// <summary>A variable as the source declares it.</summary>
internal sealed record VarInfo(string Name, CType Type, bool IsArray, int RowLength);

/// <summary>
///     Statements as C#, typed so that Roslyn accepts them. ATEL has untyped 32-bit values and no
///     conversions; C# needs <c>bool</c> conditions, <c>int</c> operands for bitwise operators and casts
///     into narrow variables. The printer adds those where C# needs them, as forms that compile to no
///     code: <c>AsBool(x)</c>, <c>AsInt(c)</c>, casts and <c>unchecked(…)</c> (the compiler drops all four).
/// </summary>
/// <remarks>
///     The compiler reads the text back into the same statement (<c>StatementReader</c>), so the
///     printer avoids the forms that would read back as something else: <c>&amp;</c> and <c>|</c> are
///     <c>land</c>/<c>lor</c> exactly when both operands are <c>bool</c>; <c>if (!x) goto</c> is
///     <c>popa_jz</c>, so <c>popa_jnz</c> on a <c>not</c> stays assembly.
/// </remarks>
internal sealed class StatementPrinter(VarInfo[] vars, NativeSignatures natives, string[] workerNames, bool functionWorker) {
    private const int PPrimary = 15, PUnary = 14, PMul = 13, PAdd = 12, PShift = 11, PRel = 10, PEq = 9, PAnd = 8, PXor = 7, POr = 6;

    /// <summary>Registers the printed statements use: (kind, number) for the integer and float registers.</summary>
    public SortedSet<(RegKind Kind, int Number)> Registers { get; } = [];

    /// <param name="Const">The value of a C# constant expression: <c>long</c> for <c>int</c>, <c>float</c>; else null.</param>
    private readonly record struct E(string Text, CType Type, int Prec, object? Const = null);

    private static NotPrintableException No(string why) => new(why);

    // ----- Statements ----------------------------------------------------------------------------

    public string Statement(Stmt s) => s switch {
        AssignStmt a => Assign(a),
        CompoundStmt c => Compound(c),
        CallStmt c => Call(c.Native, c.Args).Text + ";",
        RequestStmt r => Request(r),
        CallWorkerStmt c => c.Worker < workerNames.Length ? $"Call<{workerNames[c.Worker]}>();" : throw No("call to a worker that does not exist"),
        GotoStmt g => $"goto l{g.Label};",
        CondGotoStmt { IfTrue: true, Condition: UnaryExpr { Op: Opcodes.Not } } => throw No("popa_jnz on a not reads back as popa_jz"),
        CondGotoStmt { IfTrue: true } g => $"if ({ToBool(Expr(g.Condition)).Text}) goto l{g.Label};",
        CondGotoStmt g => $"if (!{Wrap(ToBool(Expr(g.Condition)), PUnary)}) goto l{g.Label};",
        OpStmt { Op: Opcodes.End } when !functionWorker => "return;",
        OpStmt { Op: Opcodes.Ret } when functionWorker => "return;",
        OpStmt { Op: Opcodes.Op54 } => "Halt();",
        OpStmt { Op: Opcodes.Op40 } => "Breakpoint();",
        EndWithStmt { Op: Opcodes.EndP } e => $"End({ToInt(Expr(e.Value)).Text});",
        OpArgsStmt x => $"Op{x.Op:x2}({string.Join(", ", x.Args.Select(a => ToInt(Expr(a)).Text))});",
        _ => throw No($"no C# for {s}"),
    };

    private string Request(RequestStmt r) {
        (string name, int variant) = r.Op switch {
            >= Opcodes.Req0 and <= Opcodes.ReqE0 => (Names[r.Op - Opcodes.Req0], 0),
            >= Opcodes.Req1 and <= 0x53 => (Names[(r.Op - Opcodes.Req1) / 5], (r.Op - Opcodes.Req1) % 5 + 1),
            _ => throw No("not a request"),
        };
        string args = $"{ToInt(Expr(r.Level)).Text}, {ToInt(Expr(r.Worker)).Text}, {ToInt(Expr(r.Entry)).Text}";
        return variant == 0 ? $"{name}({args});" : string.Create(CultureInfo.InvariantCulture, $"{name}({args}, variant: {variant});");
    }

    private static readonly string[] Names = ["Request", "RequestWaitStart", "RequestWaitEnd"];

    /// <summary>The C# request methods, by the name the compiler reads: (base opcode of variant 0, base opcode of variants 1-5).</summary>
    public static IReadOnlyList<string> RequestNames => Names;

    private (string Text, CType Type) Target(Expr t) => t switch {
        VarExpr v => (vars[v.Var].Name, vars[v.Var].Type),
        ElemExpr or Elem2Expr => (Element(t).Text, vars[t is ElemExpr e ? e.Var : ((Elem2Expr)t).Var].Type),
        RegExpr { Kind: RegKind.Int or RegKind.Float } r => (Reg(r).Text, r.Kind == RegKind.Int ? CType.Int : CType.Float),
        RegExpr { Kind: RegKind.A } => ("RegA", CType.Int),
        RegExpr { Kind: RegKind.B } => ("RegB", CType.Int),
        _ => throw No($"cannot assign to {t}"),
    };

    private string Assign(AssignStmt a) {
        (string target, CType t) = Target(a.Target);
        if (a.Target is VarExpr or RegExpr && a.Value is BinaryExpr b && StatementLifter.IsCompound(b.Op) && b.Left == a.Target
            && CompoundOperand(b.Op, b.Right, t, cast: false) is E rhs) {
            return $"{target} {Symbol(b.Op)}= {rhs.Text};";
        }
        E value = Expr(a.Value);
        // `x = x;` is CS1717 (assignment to the same variable); unary plus is no code.
        if (a.Value == a.Target) value = Plus(value);
        return $"{target} = {ToType(value, t).Text};";
    }

    private string Compound(CompoundStmt c) {
        (string target, CType t) = Target(c.Target);
        E rhs = CompoundOperand(c.Op, c.Value, t, cast: true) ?? throw No("no compound assignment for this operand");
        return $"{target} {Symbol(c.Op)}= {rhs.Text};";
    }

    /// <summary>
    ///     The right operand of <c>x op= y</c> as C# allows it: implicitly convertible to the target's
    ///     type (any <c>int</c> for a shift). With <paramref name="cast"/> a cast makes it so; without,
    ///     null where it is not.
    /// </summary>
    private E? CompoundOperand(byte op, Expr value, CType t, bool cast) {
        bool arithmetic = op is Opcodes.Add or Opcodes.Sub or Opcodes.Mul or Opcodes.Div or Opcodes.Mod;
        if (t == CType.Float && !arithmetic) return null;
        E rhs = arithmetic ? ToNum(Expr(value)) : ToInt(Expr(value));
        if (op is Opcodes.Div or Opcodes.Mod && t != CType.Float && rhs.Type != CType.Float && rhs.Const is 0L) throw No("division by constant zero");
        if (op is Opcodes.Shl or Opcodes.Sar || Fits(rhs, t)) return rhs;
        return cast ? Convert(rhs, t) : null;
    }

    // ----- Expressions ---------------------------------------------------------------------------

    private E Expr(Expr e) {
        switch (e) {
            case IntLit i:
                return new E(IntText(i.Value), CType.Int, i.Value < 0 ? PUnary : PPrimary, (long)i.Value);
            case FloatLit f: {
                string text = ScriptDecompiler.FloatLiteral(f.Bits) ?? throw No("a float no literal gives");
                return new E(text, CType.Float, text.StartsWith('-') ? PUnary : PPrimary, f.Value);
            }
            case VarExpr v:
                return new E(vars[v.Var].Name, vars[v.Var].Type, PPrimary);
            case ElemExpr or Elem2Expr:
                return Element(e);
            case AddrExpr a:
                return new E($"Addr(ref {Element(a.Element).Text})", CType.Int, PPrimary);
            case RegExpr r:
                return Reg(r);
            case UnaryExpr { Op: Opcodes.Not } u:
                return new E("!" + Wrap(ToBool(Expr(u.Operand)), PUnary), CType.Bool, PUnary);
            case UnaryExpr { Op: Opcodes.Bnot } u: {
                E x = ToInt(Expr(u.Operand));
                return new E("~" + Wrap(x, PUnary), CType.Int, PUnary, x.Const is long c ? ~c : null);
            }
            case BinaryExpr b:
                return Binary(b);
            case CallExpr c:
                return Call(c.Native, c.Args);
            default:
                throw No($"no C# for {e}");
        }
    }

    private E Element(Expr e) {
        switch (e) {
            case ElemExpr el: {
                VarInfo v = vars[el.Var];
                string index = ToInt(Expr(el.Index)).Text;
                return new E(v.RowLength > 0 ? $"At({v.Name}, {index})" : $"{v.Name}[{index}]", v.Type, PPrimary);
            }
            case Elem2Expr el: {
                VarInfo v = vars[el.Var];
                return new E($"{v.Name}[{ToInt(Expr(el.Row)).Text}, {ToInt(Expr(el.Col)).Text}]", v.Type, PPrimary);
            }
            default:
                throw No("not an element");
        }
    }

    private E Reg(RegExpr r) {
        switch (r.Kind) {
            case RegKind.Int:
                Registers.Add((r.Kind, r.Number));
                return new E(string.Create(CultureInfo.InvariantCulture, $"I{r.Number}"), CType.Int, PPrimary);
            case RegKind.Float:
                Registers.Add((r.Kind, r.Number));
                return new E(string.Create(CultureInfo.InvariantCulture, $"F{r.Number}"), CType.Float, PPrimary);
            case RegKind.A: return new E("RegA", CType.Int, PPrimary);
            case RegKind.B: return new E("RegB", CType.Int, PPrimary);
            default: return new E("LastResult", CType.Int, PPrimary);
        }
    }

    private E Call(ushort id, IReadOnlyList<Expr> args) {
        NativeSignature sig = natives[id] ?? throw No("unknown native");
        if (sig.Argc != args.Count) throw No("argument count");
        IEnumerable<string> texts = args.Select((a, k) => (sig.ArgKinds[k] == 'f' ? ToNum(Expr(a)) : ToInt(Expr(a))).Text);
        return new E($"{sig.Name}({string.Join(", ", texts)})", sig.Result == "float" ? CType.Float : CType.Int, PPrimary);
    }

    private E Binary(BinaryExpr b) {
        E l = Expr(b.Left), r = Expr(b.Right);
        int p = Prec(b.Op);
        switch (b.Op) {
            case Opcodes.Add or Opcodes.Sub or Opcodes.Mul or Opcodes.Div or Opcodes.Mod: {
                l = ToNum(l);
                r = ToNum(r);
                bool isFloat = l.Type == CType.Float || r.Type == CType.Float;
                if (!isFloat && b.Op is Opcodes.Div or Opcodes.Mod && r.Const is 0L) throw No("division by constant zero");
                return Fold(b.Op, l, r, p, isFloat ? CType.Float : CType.Int);
            }
            case Opcodes.And or Opcodes.Or or Opcodes.Shl or Opcodes.Sar:
                return Fold(b.Op, ToInt(l), ToInt(r), p, CType.Int);
            case Opcodes.Xor:
                return l.Type == CType.Bool && r.Type == CType.Bool
                    ? Make(b.Op, l, r, p, CType.Bool)
                    : Fold(b.Op, ToInt(l), ToInt(r), p, CType.Int);
            case Opcodes.Land or Opcodes.Lor:
                return Make(b.Op, ToBool(l), ToBool(r), p, CType.Bool);
            case Opcodes.Eq or Opcodes.Ne when l.Type == CType.Bool && r.Type == CType.Bool:
                return Make(b.Op, l, r, p, CType.Bool);
            default: {
                // `x == x` is CS1718 (comparison to the same variable); unary plus is no code.
                if (b.Left == b.Right) r = Plus(r);
                // Comparisons. A narrow variable against a constant outside its range is CS0652
                // ("comparison to integral constant is useless"): compare it as an int.
                l = ToNum(l);
                r = ToNum(r);
                if (IsNarrow(l.Type) && l.Const is null && r.Const is long rc && !InRange(rc, l.Type)) l = Cast(CType.Int, l);
                if (IsNarrow(r.Type) && r.Const is null && l.Const is long lc && !InRange(lc, r.Type)) r = Cast(CType.Int, r);
                return Make(b.Op, l, r, p, CType.Bool);
            }
        }
    }

    private static E Make(byte op, E l, E r, int p, CType type, object? constant = null) =>
        new($"{Wrap(l, p)} {Symbol(op)} {Wrap(r, p + 1)}", type, p, constant);

    /// <summary>A binary operation, with its value when both operands are constants; one that overflows in C#'s checked constant arithmetic is wrapped in <c>unchecked</c>.</summary>
    private static E Fold(byte op, E l, E r, int p, CType type) {
        if (type == CType.Int && l.Const is long a && r.Const is long c) {
            long v = op switch {
                Opcodes.Add => a + c, Opcodes.Sub => a - c, Opcodes.Mul => a * c,
                Opcodes.Div => c == -1 ? -a : a / c, Opcodes.Mod => c == -1 ? 0 : a % c,
                Opcodes.And => a & c, Opcodes.Or => a | c, Opcodes.Xor => a ^ c,
                Opcodes.Shl => (int)a << (int)(c & 31), _ => (int)a >> (int)(c & 31),
            };
            E made = Make(op, l, r, p, type, (long)(int)v);
            return v is >= int.MinValue and <= int.MaxValue ? made : new E($"unchecked({made.Text})", type, PPrimary, (long)unchecked((int)v));
        }
        if (type == CType.Float && l.Const is not null && r.Const is not null) {
            float x = l.Const is long la ? la : (float)l.Const, y = r.Const is long lc ? lc : (float)r.Const;
            float v = op switch { Opcodes.Add => x + y, Opcodes.Sub => x - y, Opcodes.Mul => x * y, Opcodes.Div => x / y, _ => x % y };
            return Make(op, l, r, p, type, v);
        }
        return Make(op, l, r, p, type);
    }

    // ----- Conversions ---------------------------------------------------------------------------

    /// <summary><c>+x</c>: C#'s promotion of a narrow value to <c>int</c>, no code.</summary>
    private static E Plus(E x) => new("+" + Wrap(x, PUnary), x.Type is CType.Float ? CType.Float : x.Type is CType.Bool ? CType.Bool : CType.Int, PUnary, x.Const);

    private static E ToBool(E x) => x.Type == CType.Bool ? x : new E($"AsBool({x.Text})", CType.Bool, PPrimary);

    private static E ToNum(E x) => x.Type == CType.Bool ? new E($"AsInt({x.Text})", CType.Int, PPrimary) : x;

    private static E ToInt(E x) => x.Type switch {
        CType.Bool => new E($"AsInt({x.Text})", CType.Int, PPrimary),
        CType.Float => Cast(CType.Int, x),
        _ => x,
    };

    /// <summary>The value as a <paramref name="t"/>, for an assignment.</summary>
    private static E ToType(E x, CType t) => t switch {
        CType.Float => ToNum(x),
        CType.Int => ToInt(x),
        _ => Fits(x, t) ? x : Convert(ToNum(x), t),
    };

    /// <summary>A cast; a constant out of the type's range is cast in <c>unchecked</c> (CS0221 otherwise).</summary>
    private static E Convert(E x, CType t) => Cast(t, x);

    private static E Cast(CType t, E x) {
        string text = $"({TypeName(t)}){Wrap(x, PUnary)}";
        switch (x.Const) {
            case long v when t != CType.Float:
                return InRange(v, t) ? new E(text, t, PUnary, v) : new E($"unchecked({text})", t, PPrimary, Wrapped(v, t));
            case float f when t != CType.Float:
                double d = Math.Truncate(f);
                return !float.IsNaN(f) && InRange(d, t) ? new E(text, t, PUnary, (long)d) : new E($"unchecked({text})", t, PPrimary);
            default:
                return new E(text, t, PUnary, t == CType.Float ? x.Const switch { long v => (float)v, float f => f, _ => null } : null);
        }
    }

    /// <summary>Whether C# converts the value to <paramref name="t"/> without a cast.</summary>
    private static bool Fits(E x, CType t) {
        if (x.Type == t) return true;
        if (x.Type == CType.Bool) return false;
        if (t == CType.Float) return true;
        if (x.Type == CType.Float) return false;
        if (x.Const is long v && x.Type == CType.Int) return InRange(v, t);
        return (x.Type, t) switch {
            (_, CType.Int) => true,
            (CType.Byte, CType.Short or CType.UShort) => true,
            (CType.SByte, CType.Short) => true,
            _ => false,
        };
    }

    private static bool IsNarrow(CType t) => t is CType.Byte or CType.SByte or CType.UShort or CType.Short;

    private static bool InRange(double v, CType t) => t switch {
        CType.Byte => v is >= byte.MinValue and <= byte.MaxValue,
        CType.SByte => v is >= sbyte.MinValue and <= sbyte.MaxValue,
        CType.UShort => v is >= ushort.MinValue and <= ushort.MaxValue,
        CType.Short => v is >= short.MinValue and <= short.MaxValue,
        _ => v is >= int.MinValue and <= int.MaxValue,
    };

    private static long Wrapped(long v, CType t) => t switch {
        CType.Byte => unchecked((byte)v), CType.SByte => unchecked((sbyte)v),
        CType.UShort => unchecked((ushort)v), CType.Short => unchecked((short)v),
        _ => unchecked((int)v),
    };

    public static string TypeName(CType t) => t switch {
        CType.Byte => "byte", CType.SByte => "sbyte", CType.UShort => "ushort", CType.Short => "short",
        CType.Int => "int", CType.Float => "float", _ => "bool",
    };

    // ----- Text ----------------------------------------------------------------------------------

    private static string Wrap(E x, int min) => x.Prec >= min ? x.Text : $"({x.Text})";

    /// <summary>Decimal, or hex from <c>0x10000</c> up (motion and model IDs read better so).</summary>
    public static string IntText(int v) => v >= 0x10000
        ? "0x" + v.ToString("x", CultureInfo.InvariantCulture)
        : v.ToString(CultureInfo.InvariantCulture);

    private static int Prec(byte op) => op switch {
        Opcodes.Mul or Opcodes.Div or Opcodes.Mod => PMul,
        Opcodes.Add or Opcodes.Sub => PAdd,
        Opcodes.Shl or Opcodes.Sar => PShift,
        Opcodes.Lt or Opcodes.Gt or Opcodes.Le or Opcodes.Ge => PRel,
        Opcodes.Eq or Opcodes.Ne => PEq,
        Opcodes.And or Opcodes.Land => PAnd,
        Opcodes.Xor => PXor,
        _ => POr,
    };

    public static string Symbol(byte op) => op switch {
        Opcodes.Add => "+", Opcodes.Sub => "-", Opcodes.Mul => "*", Opcodes.Div => "/", Opcodes.Mod => "%",
        Opcodes.Shl => "<<", Opcodes.Sar => ">>",
        Opcodes.Lt => "<", Opcodes.Gt => ">", Opcodes.Le => "<=", Opcodes.Ge => ">=",
        Opcodes.Eq => "==", Opcodes.Ne => "!=",
        Opcodes.And or Opcodes.Land => "&", Opcodes.Xor => "^", Opcodes.Or or Opcodes.Lor => "|",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };
}
