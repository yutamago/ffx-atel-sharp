// SPDX-License-Identifier: MIT

using System.Globalization;
using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

/// <summary>A variable of the script class: its table index and entry.</summary>
internal sealed record VariableSymbol(int Index, VarEntry Entry);

/// <summary>
///     Reads C# statements of an entry method into <see cref="Stmt"/>s, from the syntax alone (Phase 3
///     of the plan; docs/plan.md, "Phase 3: statements"). Names resolve to the worker's
///     <c>[Register]</c> fields, the script class's <c>[Var]</c> fields, the <see cref="Ffx.Atel.Worker"/>
///     members and the natives of the reference assembly.
/// </summary>
/// <remarks>
///     Forms that compile to no code are dropped: parentheses, casts, unary <c>+</c>,
///     <c>unchecked(…)</c>, <c>AsInt(…)</c>, <c>AsBool(…)</c>. <c>&amp;</c> and <c>|</c> are <c>land</c>/<c>lor</c> when both
///     operands are conditions (a comparison, <c>!</c>, <c>AsBool</c>, or <c>&amp;</c>, <c>|</c>, <c>^</c> of
///     conditions), else <c>and</c>/<c>or</c>.
/// </remarks>
internal sealed class StatementReader(
    IReadOnlyDictionary<string, VariableSymbol> variables,
    IReadOnlyDictionary<string, RegExpr> registers,
    IReadOnlyDictionary<string, NativeSignature> natives,
    IReadOnlyDictionary<string, int> workers,
    bool functionWorker) {
    private static CompileException Error(SyntaxNode node, string message) => CompileException.At(node, message);

    // ----- Statements ----------------------------------------------------------------------------

    public Stmt Read(StatementSyntax s) {
        switch (s) {
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax a }:
                return Assignment(a);
            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call }:
                return CallStatement(call);
            case ReturnStatementSyntax { Expression: null }:
                return new OpStmt(functionWorker ? Opcodes.Ret : Opcodes.End);
            case GotoStatementSyntax g when g.IsKind(SyntaxKind.GotoStatement):
                return new GotoStmt(Label(g.Expression!));
            case IfStatementSyntax { Else: null, Statement: GotoStatementSyntax g } i when g.IsKind(SyntaxKind.GotoStatement): {
                ExpressionSyntax c = Strip(i.Condition, conversions: false);
                return c is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not
                    ? new CondGotoStmt(Label(g.Expression!), ReadExpr(not.Operand), false)
                    : new CondGotoStmt(Label(g.Expression!), ReadExpr(c), true);
            }
            default:
                throw Error(s, "not a statement Atel compiles yet (Phase 3: assignments, calls, requests, return, goto, if (…) goto)");
        }
    }

    /// <summary>The number of label <c>lN</c>.</summary>
    public static int? LabelNumber(string name) =>
        name.Length > 1 && name[0] == 'l' && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;

    private static int Label(ExpressionSyntax e) =>
        e is IdentifierNameSyntax id && LabelNumber(id.Identifier.ValueText) is int n ? n : throw Error(e, "labels are lN");

    private Stmt Assignment(AssignmentExpressionSyntax a) {
        Expr target = Target(a.Left);
        Expr value = ReadExpr(a.Right);
        if (a.IsKind(SyntaxKind.SimpleAssignmentExpression)) return new AssignStmt(target, value);
        byte op = a.Kind() switch {
            SyntaxKind.AddAssignmentExpression => Opcodes.Add,
            SyntaxKind.SubtractAssignmentExpression => Opcodes.Sub,
            SyntaxKind.MultiplyAssignmentExpression => Opcodes.Mul,
            SyntaxKind.DivideAssignmentExpression => Opcodes.Div,
            SyntaxKind.ModuloAssignmentExpression => Opcodes.Mod,
            SyntaxKind.AndAssignmentExpression => Opcodes.And,
            SyntaxKind.OrAssignmentExpression => Opcodes.Or,
            SyntaxKind.ExclusiveOrAssignmentExpression => Opcodes.Xor,
            SyntaxKind.LeftShiftAssignmentExpression => Opcodes.Shl,
            SyntaxKind.RightShiftAssignmentExpression => Opcodes.Sar,
            _ => throw Error(a, $"no ATEL operation for {a.OperatorToken.ValueText}"),
        };
        return target is ElemExpr or Elem2Expr
            ? new CompoundStmt(target, op, value)
            : new AssignStmt(target, new BinaryExpr(op, target, value));
    }

    private Expr Target(ExpressionSyntax e) {
        Expr t = ReadExpr(e);
        return t is VarExpr or ElemExpr or Elem2Expr or RegExpr { Kind: not RegKind.Facc } ? t : throw Error(e, "not something to assign to");
    }

    private Stmt CallStatement(InvocationExpressionSyntax call) {
        List<ArgumentSyntax> args = [.. call.ArgumentList.Arguments];
        switch (call.Expression) {
            case IdentifierNameSyntax { Identifier.ValueText: "Request" or "RequestWaitStart" or "RequestWaitEnd" } id: {
                int kind = id.Identifier.ValueText switch { "Request" => 0, "RequestWaitStart" => 1, _ => 2 };
                List<ArgumentSyntax> positional = args.Where(a => a.NameColon is null).ToList();
                ArgumentSyntax? named = args.FirstOrDefault(a => a.NameColon is not null);
                if (positional.Count is < 3 or > 4 || named is not null && (named.NameColon!.Name.Identifier.ValueText != "variant" || positional.Count != 3)) {
                    throw Error(call, $"{id.Identifier.ValueText}(level, worker, entry[, variant])");
                }
                ArgumentSyntax? v = named ?? (positional.Count == 4 ? positional[3] : null);
                int variant = v is null ? 0 : ReadExpr(v.Expression) is IntLit { Value: >= 0 and <= 5 } k ? k.Value : throw Error(v, "the variant is a constant 0-5");
                byte op = (byte)(variant == 0 ? Opcodes.Req0 + kind : Opcodes.Req1 + kind * 5 + variant - 1);
                return new RequestStmt(op, ReadExpr(positional[0].Expression), ReadExpr(positional[1].Expression), ReadExpr(positional[2].Expression));
            }
            case GenericNameSyntax { Identifier.ValueText: "Call", TypeArgumentList.Arguments: [TypeSyntax t] } when args.Count == 0:
                return new CallWorkerStmt(workers.TryGetValue(TypeNameOf(t), out int w) ? w : throw Error(t, $"no worker {t} in [Workers]"));
            case IdentifierNameSyntax { Identifier.ValueText: "Halt" } when args.Count == 0:
                return new OpStmt(Opcodes.Op54);
            case IdentifierNameSyntax { Identifier.ValueText: "Breakpoint" } when args.Count == 0:
                return new OpStmt(Opcodes.Op40);
            case IdentifierNameSyntax { Identifier.ValueText: "Op77" or "Op78" or "Op79" } id when args.Count == (id.Identifier.ValueText == "Op79" ? 3 : 2):
                return new OpArgsStmt(id.Identifier.ValueText switch { "Op77" => Opcodes.Op77, "Op78" => Opcodes.Op78, _ => Opcodes.Op79 },
                    [.. args.Select(a => ReadExpr(a.Expression))]);
            case IdentifierNameSyntax { Identifier.ValueText: "End" } when args.Count == 1:
                return new EndWithStmt(Opcodes.EndP, ReadExpr(args[0].Expression));
            case MemberAccessExpressionSyntax:
                (ushort id2, IReadOnlyList<Expr> a2) = Native(call);
                return new CallStmt(id2, a2);
            default:
                throw Error(call, $"no ATEL call {call.Expression}");
        }
    }

    private static string TypeNameOf(TypeSyntax t) => t switch {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => t.ToString(),
    };

    // ----- Expressions ---------------------------------------------------------------------------

    /// <summary>
    ///     Strips what compiles to no code: parentheses, <c>unchecked</c>, and (with
    ///     <paramref name="conversions"/>) casts, unary <c>+</c>, <c>AsInt</c>, <c>AsBool</c>.
    /// </summary>
    private static ExpressionSyntax Strip(ExpressionSyntax e, bool conversions = true) {
        while (true) {
            switch (e) {
                case ParenthesizedExpressionSyntax p: e = p.Expression; continue;
                case CheckedExpressionSyntax c when c.IsKind(SyntaxKind.UncheckedExpression): e = c.Expression; continue;
                case CastExpressionSyntax c when conversions: e = c.Expression; continue;
                case PrefixUnaryExpressionSyntax u when conversions && u.IsKind(SyntaxKind.UnaryPlusExpression): e = u.Operand; continue;
                case InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "AsInt" or "AsBool" }, ArgumentList.Arguments: [ArgumentSyntax a] }
                    when conversions:
                    e = a.Expression;
                    continue;
                default: return e;
            }
        }
    }

    /// <summary>Whether C# types the expression <c>bool</c>: a condition.</summary>
    private static bool IsBool(ExpressionSyntax e) {
        e = Strip(e, conversions: false);
        return e switch {
            InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "AsBool" } } => true,
            PrefixUnaryExpressionSyntax u => u.IsKind(SyntaxKind.LogicalNotExpression),
            LiteralExpressionSyntax l => l.IsKind(SyntaxKind.TrueLiteralExpression) || l.IsKind(SyntaxKind.FalseLiteralExpression),
            BinaryExpressionSyntax b => b.Kind() switch {
                SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or SyntaxKind.LessThanExpression
                    or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression => true,
                SyntaxKind.BitwiseAndExpression or SyntaxKind.BitwiseOrExpression or SyntaxKind.ExclusiveOrExpression => IsBool(b.Left) && IsBool(b.Right),
                _ => false,
            },
            _ => false,
        };
    }

    public Expr ReadExpr(ExpressionSyntax syntax) {
        ExpressionSyntax e = Strip(syntax);
        switch (e) {
            case LiteralExpressionSyntax l when l.IsKind(SyntaxKind.NumericLiteralExpression):
                return Number(l.Token.Value, l, negate: false);
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.UnaryMinusExpression)
                && Strip(u.Operand) is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NumericLiteralExpression):
                return Number(lit.Token.Value, u, negate: true);
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression):
                return new UnaryExpr(Opcodes.Not, ReadExpr(u.Operand));
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.BitwiseNotExpression):
                return new UnaryExpr(Opcodes.Bnot, ReadExpr(u.Operand));
            case BinaryExpressionSyntax b:
                return Binary(b);
            case IdentifierNameSyntax id:
                return Name(id);
            case ElementAccessExpressionSyntax el:
                return Element(el);
            case InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "At" } } at:
                return Element(at);
            case InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Addr" }, ArgumentList.Arguments: [ArgumentSyntax a] }
                when a.RefKindKeyword.IsKind(SyntaxKind.RefKeyword): {
                Expr element = ReadExpr(a.Expression);
                return element is ElemExpr or Elem2Expr ? new AddrExpr(element) : throw Error(a, "Addr takes an array element");
            }
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax } call: {
                (ushort id, IReadOnlyList<Expr> args) = Native(call);
                return new CallExpr(id, args);
            }
            default:
                throw Error(syntax, $"not an expression Atel compiles yet: {e.Kind()}");
        }
    }

    private static Expr Number(object? value, SyntaxNode at, bool negate) {
        switch (value) {
            case float f:
                uint bits = BitConverter.SingleToUInt32Bits(f);
                return new FloatLit(negate ? bits ^ 0x8000_0000 : bits);
            case double:
                throw Error(at, "a float literal needs its f (double is not an ATEL type)");
            case int or uint or long or ulong:
                long v = value is ulong u ? (u > long.MaxValue ? throw Error(at, "out of range") : (long)u) : System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
                if (negate) v = -v;
                return v is >= int.MinValue and <= uint.MaxValue ? new IntLit(unchecked((int)v)) : throw Error(at, $"{v} does not fit 32 bits");
            default:
                throw Error(at, "not a number ATEL has");
        }
    }

    private Expr Binary(BinaryExpressionSyntax b) {
        bool logical = IsBool(b.Left) && IsBool(b.Right);
        byte op = b.Kind() switch {
            SyntaxKind.AddExpression => Opcodes.Add,
            SyntaxKind.SubtractExpression => Opcodes.Sub,
            SyntaxKind.MultiplyExpression => Opcodes.Mul,
            SyntaxKind.DivideExpression => Opcodes.Div,
            SyntaxKind.ModuloExpression => Opcodes.Mod,
            SyntaxKind.LeftShiftExpression => Opcodes.Shl,
            SyntaxKind.RightShiftExpression => Opcodes.Sar,
            SyntaxKind.LessThanExpression => Opcodes.Lt,
            SyntaxKind.GreaterThanExpression => Opcodes.Gt,
            SyntaxKind.LessThanOrEqualExpression => Opcodes.Le,
            SyntaxKind.GreaterThanOrEqualExpression => Opcodes.Ge,
            SyntaxKind.EqualsExpression => Opcodes.Eq,
            SyntaxKind.NotEqualsExpression => Opcodes.Ne,
            SyntaxKind.BitwiseAndExpression => logical ? Opcodes.Land : Opcodes.And,
            SyntaxKind.BitwiseOrExpression => logical ? Opcodes.Lor : Opcodes.Or,
            SyntaxKind.ExclusiveOrExpression => Opcodes.Xor,
            SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression
                => throw Error(b, "&& and || short-circuit, which needs branches (Phase 4); & and | evaluate both sides"),
            _ => throw Error(b, $"no ATEL operation for {b.OperatorToken.ValueText}"),
        };
        return new BinaryExpr(op, ReadExpr(b.Left), ReadExpr(b.Right));
    }

    private Expr Name(IdentifierNameSyntax id) {
        string n = id.Identifier.ValueText;
        if (registers.TryGetValue(n, out RegExpr? reg)) return reg;
        if (variables.TryGetValue(n, out VariableSymbol? v)) {
            return v.Entry.IsArray ? throw Error(id, $"{n} is an array") : new VarExpr(v.Index);
        }
        return n switch {
            "RegA" => new RegExpr(RegKind.A),
            "RegB" => new RegExpr(RegKind.B),
            "LastResult" => new RegExpr(RegKind.Facc),
            _ => throw Error(id, $"{n} is not a variable, register or worker member"),
        };
    }

    private VariableSymbol ArrayVariable(ExpressionSyntax e) =>
        Strip(e) is IdentifierNameSyntax id && variables.TryGetValue(id.Identifier.ValueText, out VariableSymbol? v) && v.Entry.IsArray
            ? v
            : throw Error(e, "not an array variable");

    private Expr Element(ExpressionSyntax e) {
        switch (e) {
            case ElementAccessExpressionSyntax el: {
                VariableSymbol v = ArrayVariable(el.Expression);
                SeparatedSyntaxList<ArgumentSyntax> args = el.ArgumentList.Arguments;
                if (v.Entry.RowLength == 0) {
                    return args.Count == 1 ? new ElemExpr(v.Index, ReadExpr(args[0].Expression)) : throw Error(el, "a 1-D array takes one index");
                }
                return args.Count == 2
                    ? new Elem2Expr(v.Index, ReadExpr(args[0].Expression), ReadExpr(args[1].Expression), v.Entry.RowLength)
                    : throw Error(el, "a 2-D array takes [row, col], or At(array, index)");
            }
            case InvocationExpressionSyntax { ArgumentList.Arguments: [ArgumentSyntax array, ArgumentSyntax index] } at: {
                VariableSymbol v = ArrayVariable(array.Expression);
                return v.Entry.RowLength > 0 ? new ElemExpr(v.Index, ReadExpr(index.Expression)) : throw Error(at, "At(…) is for 2-D arrays");
            }
            default:
                throw Error(e, "not an array element");
        }
    }

    private (ushort Id, IReadOnlyList<Expr> Args) Native(InvocationExpressionSyntax call) {
        string name = call.Expression is MemberAccessExpressionSyntax m
            ? $"{TypeNameOf(m.Expression as TypeSyntax ?? SyntaxFactory.ParseTypeName(m.Expression.ToString()))}.{m.Name.Identifier.ValueText}"
            : call.Expression.ToString();
        if (!natives.TryGetValue(name, out NativeSignature? sig)) throw Error(call.Expression, $"{name} is not a native");
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;
        if (args.Count != sig.Argc) throw Error(call, $"{name} takes {sig.Argc} arguments");
        if (args.Any(a => a.NameColon is not null)) throw Error(call, "native arguments are positional (for now)");
        return (sig.Id, [.. args.Select(a => ReadExpr(a.Expression))]);
    }
}
