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
internal sealed partial class StatementReader(
    IReadOnlyDictionary<string, VariableSymbol> variables,
    IReadOnlyDictionary<string, RegExpr> registers,
    IReadOnlyDictionary<string, NativeSignature> natives,
    IReadOnlyDictionary<string, int> workers,
    bool functionWorker) {
    private static CompileException Error(SyntaxNode node, string message) => CompileException.At(node, message);

    // ----- Statements ----------------------------------------------------------------------------

    private readonly Dictionary<object, Location> _locations = new(ReferenceEqualityComparer.Instance);

    /// <summary>Where a statement read here came from, for errors in lowering.</summary>
    public object? LocationOf(Stmt s) => _locations.GetValueOrDefault(s);

    private T At<T>(T s, SyntaxNode node) where T : Stmt {
        _locations[s] = node.GetLocation();
        return s;
    }

    /// <summary>A block's statements; labels become <see cref="LabelStmt"/>s before the statement they label.</summary>
    public List<Stmt> Block(IEnumerable<StatementSyntax> statements) {
        List<Stmt> list = [];
        foreach (StatementSyntax s in statements) Add(s, list);
        return list;
    }

    private List<Stmt> Body(StatementSyntax s) => s is BlockSyntax b ? Block(b.Statements) : Block([s]);

    private void Add(StatementSyntax s, List<Stmt> list) {
        switch (s) {
            case LabeledStatementSyntax labeled:
                list.Add(At(new LabelStmt(LabelNumber(labeled.Identifier.ValueText) ?? throw Error(labeled, "labels are lN")), labeled));
                Add(labeled.Statement, list);
                return;
            case EmptyStatementSyntax:
                return;
            case BlockSyntax b:
                list.AddRange(Block(b.Statements));
                return;
            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Asm" } } call } e:
                list.Add(At(ScriptCompiler.AsmLines(e, call), s));
                return;
            default:
                list.Add(At(Read(s), s));
                return;
        }
    }

    public Stmt Read(StatementSyntax s) {
        switch (s) {
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax a } when IsDiscard(a.Left) && a.IsKind(SyntaxKind.SimpleAssignmentExpression):
                return Discarded(a);
            case ExpressionStatementSyntax { Expression: AwaitExpressionSyntax aw }:
                return Awaited(aw);
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax a }:
                return Assignment(a);
            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call }:
                return CallStatement(call);
            case ReturnStatementSyntax { Expression: null }:
                return new OpStmt(functionWorker ? Opcodes.Ret : Opcodes.End);
            case GotoStatementSyntax g when g.IsKind(SyntaxKind.GotoStatement):
                return new GotoStmt(Label(g.Expression!));
            case GotoStatementSyntax g when g.IsKind(SyntaxKind.GotoCaseStatement):
                return ReadExpr(g.Expression!) is IntLit k ? new GotoCaseStmt(k.Value) : throw Error(g, "goto case takes an integer constant");
            case GotoStatementSyntax g when g.IsKind(SyntaxKind.GotoDefaultStatement):
                return new GotoCaseStmt(null);
            case BreakStatementSyntax:
                return new BreakStmt();
            case ContinueStatementSyntax:
                return new ContinueStmt();
            // `if (c) goto lN;` without braces is the jump itself: popa_jnz, or popa_jz for `if (!c)`.
            case IfStatementSyntax { Else: null, Statement: GotoStatementSyntax g } i when g.IsKind(SyntaxKind.GotoStatement): {
                ExpressionSyntax c = Strip(i.Condition, conversions: false);
                return c is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not
                    ? new CondGotoStmt(Label(g.Expression!), ReadExpr(not.Operand), false)
                    : new CondGotoStmt(Label(g.Expression!), ReadExpr(c), true);
            }
            case IfStatementSyntax i:
                return new IfStmt(Constant(i.Condition) == false ? null : Cond(i.Condition), Body(i.Statement), i.Else is null ? null : Body(i.Else.Statement));
            case WhileStatementSyntax w:
                return new WhileStmt(Constant(w.Condition) == true ? null : Cond(w.Condition), Body(w.Statement));
            case DoStatementSyntax d:
                return new DoStmt(Body(d.Statement), Cond(d.Condition));
            case ForStatementSyntax f: {
                if (f.Declaration is not null) throw Error(f.Declaration, "a for loop declares no variables (registers and variables are fields)");
                List<Stmt> init = [.. f.Initializers.Select(e => At(Read(SyntaxFactory.ExpressionStatement(e)), e))];
                List<Stmt> step = [.. f.Incrementors.Select(e => At(Read(SyntaxFactory.ExpressionStatement(e)), e))];
                return new ForStmt(init, f.Condition is null ? null : Cond(f.Condition), step, Body(f.Statement));
            }
            case SwitchStatementSyntax sw:
                return Switch(sw);
            default:
                throw Error(s, "not a statement Atel compiles");
        }
    }

    /// <summary>The value of a <c>true</c> or <c>false</c> literal; null for anything else.</summary>
    private static bool? Constant(ExpressionSyntax e) => Strip(e, conversions: false) switch {
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.TrueLiteralExpression) => true,
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.FalseLiteralExpression) => false,
        _ => null,
    };

    private Expr Cond(ExpressionSyntax e) =>
        Constant(e) is not null ? throw Error(e, "a constant condition is only `if (false)` (dead code) and `while (true)`") : ReadExpr(e);

    private SwitchStmt Switch(SwitchStatementSyntax sw) {
        List<SwitchSection> sections = [];
        foreach (SwitchSectionSyntax section in sw.Sections) {
            if (section.Labels.Count != 1) throw Error(section, "one label per switch section (the original had no shared case bodies)");
            CaseLabel? label = section.Labels[0] switch {
                DefaultSwitchLabelSyntax => null,
                CaseSwitchLabelSyntax c => new CaseLabel(CaseKind.Equal, CaseValue(c.Value)),
                CasePatternSwitchLabelSyntax { WhenClause: null } p => Pattern(p.Pattern),
                var other => throw Error(other, "a case is a constant, a relational pattern or `>= lo and <= hi`"),
            };
            sections.Add(new SwitchSection(label, Block(section.Statements)));
        }
        return new SwitchStmt(ReadExpr(sw.Expression), sections);
    }

    private int CaseValue(ExpressionSyntax e) => ReadExpr(e) is IntLit k ? k.Value : throw Error(e, "a case value is an integer constant");

    private CaseLabel Pattern(PatternSyntax p) {
        switch (p) {
            case ConstantPatternSyntax c:
                return new CaseLabel(CaseKind.Equal, CaseValue(c.Expression));
            case RelationalPatternSyntax r:
                return new CaseLabel(r.OperatorToken.Kind() switch {
                    SyntaxKind.LessThanToken => CaseKind.Less,
                    SyntaxKind.LessThanEqualsToken => CaseKind.LessOrEqual,
                    SyntaxKind.GreaterThanToken => CaseKind.Greater,
                    _ => CaseKind.GreaterOrEqual,
                }, CaseValue(r.Expression));
            case ParenthesizedPatternSyntax pp:
                return Pattern(pp.Pattern);
            case BinaryPatternSyntax { RawKind: (int)SyntaxKind.AndPattern, Left: RelationalPatternSyntax lo, Right: RelationalPatternSyntax hi }
                when lo.OperatorToken.IsKind(SyntaxKind.GreaterThanEqualsToken) && hi.OperatorToken.IsKind(SyntaxKind.LessThanEqualsToken):
                return new CaseLabel(CaseKind.Range, CaseValue(lo.Expression), CaseValue(hi.Expression));
            default:
                throw Error(p, "a case is a constant, a relational pattern or `>= lo and <= hi`");
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
            case IdentifierNameSyntax { Identifier.ValueText: "End" } when args.Count is 1 or 2: {
                bool reset = args.Count == 2 && (Constant(args[1].Expression) ?? throw Error(args[1], "reset is true or false"));
                return new EndWithStmt(reset ? Opcodes.EndP1 : Opcodes.EndP, ReadExpr(args[0].Expression));
            }
            case IdentifierNameSyntax { Identifier.ValueText: "RequestParty" } when args.Count == 3:
                return new RequestStmt(Opcodes.Op39, ReadExpr(args[0].Expression), ReadExpr(args[1].Expression), ReadExpr(args[2].Expression));
            case IdentifierNameSyntax { Identifier.ValueText: "Nop" } when args.Count == 1:
                return ReadExpr(args[0].Expression) is IntLit { Value: >= 0 and <= 0xffff } n ? new NopStmt((ushort)n.Value) : throw Error(args[0], "Nop takes a constant 0-65535");
            case IdentifierNameSyntax { Identifier.ValueText: "Entry" } when args.Count == 1:
                return ReadExpr(args[0].Expression) is IntLit { Value: >= 0 } en ? new EntryStmt(en.Value) : throw Error(args[0], "Entry takes an entry number");
            case IdentifierNameSyntax { Identifier.ValueText: "StoreClamped" } when args.Count == 2 && args[0].RefKindKeyword.IsKind(SyntaxKind.RefKeyword): {
                Expr target = Target(args[0].Expression);
                return target is RegExpr ? throw Error(args[0], "a register has no clamped store") : new AssignStmt(target, ReadExpr(args[1].Expression), true);
            }
            // `W48.Run();`: a function worker's entry, run on this worker (`call`).
            case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Run", Expression: IdentifierNameSyntax fw }
                when args.Count == 0 && workers.TryGetValue(fw.Identifier.ValueText, out int callee):
                return new CallWorkerStmt(callee);
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
    private bool IsBool(ExpressionSyntax e) {
        e = Strip(e, conversions: false);
        return e switch {
            InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "AsBool" } } => true,
            // A native typed bool (data/native-types.json, "result").
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax } call => natives.TryGetValue(NativeName(call), out NativeSignature? sig) && sig.Result == "bool",
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
            // `false` and `true` inside a condition (`false & V3 > 2`): 0 and 1.
            case LiteralExpressionSyntax l when l.IsKind(SyntaxKind.FalseLiteralExpression) || l.IsKind(SyntaxKind.TrueLiteralExpression):
                return new IntLit(l.IsKind(SyntaxKind.TrueLiteralExpression) ? 1 : 0);
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
            case MemberAccessExpressionSyntax m when GameConstants.Lookup(m, enums: true) is int constant:
                return new IntLit(constant);
            // A message by index: Text[12] is the index itself (Ffx.TextTable).
            case ElementAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Text" }, ArgumentList.Arguments: [ArgumentSyntax index] }:
                return ReadExpr(index.Expression);
            case ElementAccessExpressionSyntax el:
                return Element(el);
            case InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Pooled" }, ArgumentList.Arguments: [ArgumentSyntax index, ArgumentSyntax value] }: {
                if (ReadExpr(index.Expression) is not IntLit { Value: >= 0 and <= 0xffff } k) throw Error(index, "the pool index is a constant 0-65535");
                return ReadExpr(value.Expression) switch {
                    IntLit i => new PoolExpr(Opcodes.PushC, (ushort)k.Value, (uint)i.Value),
                    FloatLit f => new PoolExpr(Opcodes.PushFC, (ushort)k.Value, f.Bits),
                    _ => throw Error(value, "the value is a constant"),
                };
            }
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

    private static string NativeName(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax m
        ? $"{TypeNameOf(m.Expression as TypeSyntax ?? SyntaxFactory.ParseTypeName(m.Expression.ToString()))}.{m.Name.Identifier.ValueText}"
        : call.Expression.ToString();

    private (ushort Id, IReadOnlyList<Expr> Args) Native(InvocationExpressionSyntax call) {
        string name = NativeName(call);
        if (!natives.TryGetValue(name, out NativeSignature? sig)) throw Error(call.Expression, $"{name} is not a native");
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;
        if (args.Count != sig.Argc) throw Error(call, $"{name} takes {sig.Argc} arguments");
        if (args.Any(a => a.NameColon is not null)) throw Error(call, "native arguments are positional (for now)");
        return (sig.Id, [.. args.Select(a => ReadExpr(a.Expression))]);
    }
}
