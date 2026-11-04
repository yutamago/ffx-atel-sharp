// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

// What new code needs beyond one expression tree per statement (docs/encoding.md, "New code"): locals,
// and the expressions that need statements of their own because the stack is empty at every jump —
// `?:`, `&&`/`||` as values, `x++` as a value, unary minus on any value. Such an expression puts the
// statements it needs in `_pre`, which the statement that holds it runs first, and stands for its result
// (usually a temporary). Operands are evaluated left to right as in C#: when a later operand needs
// statements, the earlier ones are evaluated into temporaries before them.
internal sealed partial class StatementReader {
    /// <summary>Statements the statement being read needs before it.</summary>
    private List<Stmt> _pre = [];

    /// <summary>The locals in scope, innermost last: slots, or constants (<c>const int K = 3;</c>).</summary>
    private readonly List<Dictionary<string, Expr>> _scopes = [];

    private int _temps;

    private Dictionary<int, VarEntry>? _varTypes;
    private Dictionary<ushort, NativeSignature>? _nativesById;

    /// <summary>Makes a temporary.</summary>
    private Slot Temp(bool isFloat) => new($"t{_temps++}", isFloat) { Temp = true };

    private static AssignStmt Set(Slot s, Expr value) => new(new LocalExpr(s), value);

    /// <summary>Reads <paramref name="read"/> with statements of its own: (those statements, the result).</summary>
    private (List<Stmt> Pre, T Result) Apart<T>(Func<T> read) {
        List<Stmt> saved = _pre;
        _pre = [];
        try {
            T result = read();
            return (_pre, result);
        }
        finally {
            _pre = saved;
        }
    }

    /// <summary>A value that reads the same wherever it is evaluated: a constant, or a temporary.</summary>
    private static bool Stable(Expr e) => e is IntLit or FloatLit or PoolExpr or SymbolExpr || e is LocalExpr { Slot: Slot { Temp: true } };

    /// <summary>Evaluates <paramref name="e"/> into a temporary at <paramref name="at"/> in <see cref="_pre"/>, unless it is <see cref="Stable"/>.</summary>
    private Expr SpillAt(Expr e, int at) {
        if (Stable(e)) return e;
        Slot t = Temp(IsFloat(e));
        _pre.Insert(at, Set(t, e));
        return new LocalExpr(t);
    }

    /// <summary>An array element with its index in a temporary (so that it can be read and written apart).</summary>
    private Expr SpillIndex(Expr element, int at) {
        if (element is not (ElemExpr or Elem2Expr)) return element;
        (int var, Expr index) = Lowering.Flat(element);
        return new ElemExpr(var, SpillAt(index, at));
    }

    /// <summary>Reads expressions left to right; where one needs statements, the ones before it are evaluated first, into temporaries.</summary>
    private List<Expr> ReadArgs(IEnumerable<ExpressionSyntax> args) {
        List<Expr> list = [];
        foreach (ExpressionSyntax a in args) {
            int mark = _pre.Count;
            Expr x = ReadExpr(a);
            if (_pre.Count > mark) {
                for (int j = 0, at = mark; j < list.Count; j++) {
                    int before = _pre.Count;
                    list[j] = SpillAt(list[j], at);
                    at += _pre.Count - before;
                }
            }
            list.Add(x);
        }
        return list;
    }

    // ----- Types -------------------------------------------------------------------------------

    /// <summary>
    ///     Whether a value is a float on the stack. ATEL's stack slots carry their type: arithmetic with a
    ///     float operand gives a float, <c>neg</c> takes a float, a float register holds one. The
    ///     compiler needs it for <c>-x</c> (<c>neg</c> or <c>0 - x</c>) and for the register a temporary
    ///     takes.
    /// </summary>
    private bool IsFloat(Expr e) {
        _varTypes ??= variables.Values.ToDictionary(v => v.Index, v => v.Entry);
        _nativesById ??= natives.Values.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        return e switch {
            FloatLit => true,
            PoolExpr p => p.Code == Opcodes.PushFC,
            RegExpr r => r.Kind == RegKind.Float,
            VarExpr v => _varTypes.TryGetValue(v.Var, out VarEntry t) && t.Type == 6,
            ElemExpr x => _varTypes.TryGetValue(x.Var, out VarEntry t) && t.Type == 6,
            Elem2Expr x => _varTypes.TryGetValue(x.Var, out VarEntry t) && t.Type == 6,
            LocalExpr { Slot: Slot s } => s.Float,
            CallExpr c => _nativesById.TryGetValue(c.Native, out NativeSignature? sig) && sig.Result == "float",
            UnaryExpr u => u.Op == Opcodes.Neg,
            BinaryExpr { Op: Opcodes.Add or Opcodes.Sub or Opcodes.Mul or Opcodes.Div } b => IsFloat(b.Left) || IsFloat(b.Right),
            _ => false,
        };
    }

    // ----- Locals ------------------------------------------------------------------------------

    private void PushScope() => _scopes.Add(new Dictionary<string, Expr>(StringComparer.Ordinal));

    private void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

    /// <summary>A local in scope by name.</summary>
    private Expr? Local(string name) {
        for (int k = _scopes.Count - 1; k >= 0; k--) {
            if (_scopes[k].TryGetValue(name, out Expr? e)) return e;
        }
        return null;
    }

    /// <summary>Declares a local (a slot) in the innermost scope.</summary>
    private void Bind(string name, Expr value, SyntaxToken at) {
        if (_scopes.Count == 0) PushScope();
        if (Local(name) is not null) throw CompileException.At(at.GetLocation(), $"{name} is already a local");
        _scopes[^1][name] = value;
    }

    /// <summary>Whether a type is <c>float</c>; <c>double</c> and the other types ATEL has not are errors.</summary>
    private static bool FloatType(TypeSyntax type) => type switch {
        PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.FloatKeyword } => true,
        PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.IntKeyword or (int)SyntaxKind.UIntKeyword or (int)SyntaxKind.ShortKeyword
            or (int)SyntaxKind.UShortKeyword or (int)SyntaxKind.ByteKeyword or (int)SyntaxKind.SByteKeyword or (int)SyntaxKind.BoolKeyword } => false,
        PredefinedTypeSyntax p => throw Error(type, $"a local cannot be {p.Keyword.ValueText}"),
        ArrayTypeSyntax => throw Error(type, "a local cannot be an array (arrays are variables: fields of the script class)"),
        // Enums and the reference assembly's value types (Target, Priority, TextId): integers.
        _ => false,
    };

    private static bool BoolType(TypeSyntax type) => type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.BoolKeyword };

    /// <summary><c>int n = e, m;</c>, <c>var x = e;</c>, <c>const int K = 3;</c>: a slot per local, the initializers as assignments.</summary>
    private List<Stmt> Declare(VariableDeclarationSyntax d, bool constant) {
        List<Stmt> list = [];
        foreach (VariableDeclaratorSyntax v in d.Variables) {
            string name = v.Identifier.ValueText;
            if (constant) {
                Expr k = v.Initializer is null ? throw Error(v, "a constant needs its value") : ReadExpr(v.Initializer.Value);
                Bind(name, k is IntLit or FloatLit ? k : throw Error(v.Initializer.Value, "a constant local is a number"), v.Identifier);
                continue;
            }
            if (v.ArgumentList is not null) throw Error(v, "a local cannot be an array");
            bool declaredFloat = !d.Type.IsVar && FloatType(d.Type);
            // An inline method's local in a register of the caller ([InlineLocal("kind", 3)]).
            if (RegisterLocals?.TryGetValue(name, out int register) == true) {
                if (d.Type.IsVar) throw Error(d.Type, $"{name} lives in a register: give its type, int or float");
                RegExpr reg = new(declaredFloat ? RegKind.Float : RegKind.Int, register);
                if (register < 0 || register >= (declaredFloat ? 10 : 4)) throw Error(v, $"{name} lives in register {register}, which an {(declaredFloat ? "float" : "int")} local does not have");
                (List<Stmt> rpre, Expr? rvalue) = v.Initializer is null ? ([], null) : Apart(() => ReadExpr(v.Initializer.Value));
                list.AddRange(rpre);
                if (rvalue is not null) list.Add(new AssignStmt(reg, rvalue));
                Bind(name, reg, v.Identifier);
                RegisterLocalsDeclared.Add(name);
                continue;
            }
            (List<Stmt> pre, Expr? value) = v.Initializer is null ? ([], null) : Apart(() => ReadExpr(v.Initializer.Value));
            bool isFloat = d.Type.IsVar
                ? value is null ? throw Error(v, "var needs an initializer") : IsFloat(value)
                : declaredFloat;
            Slot s = new(name, isFloat) { Bool = !d.Type.IsVar && BoolType(d.Type) };
            list.AddRange(pre);
            if (value is not null) list.Add(Set(s, value));
            // In scope from its declarator on (C#: `int a = 1, b = a;`).
            Bind(name, new LocalExpr(s), v.Identifier);
        }
        return list;
    }

    // ----- Conditions and the expressions that branch -----------------------------------------------

    /// <summary>
    ///     A condition: as <see cref="ReadExpr"/>, but <c>&amp;&amp;</c> and <c>||</c> (also under <c>!</c>)
    ///     stay short-circuit forms, which the statement that tests the condition lowers to jumps.
    /// </summary>
    public Expr ReadCond(ExpressionSyntax syntax) {
        ExpressionSyntax e = Strip(syntax, conversions: false);
        return e switch {
            PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression) => new UnaryExpr(Opcodes.Not, ReadCond(u.Operand)),
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) => ShortCircuit(b, and: true),
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression) => ShortCircuit(b, and: false),
            _ => ReadExpr(syntax),
        };
    }

    /// <summary>
    ///     <c>a &amp;&amp; b</c>, <c>a || b</c>: the short-circuit form; where <c>b</c> needs statements of
    ///     its own (which must run only when <c>a</c> does not decide), a temporary set by branches instead.
    /// </summary>
    private Expr ShortCircuit(BinaryExpressionSyntax b, bool and) {
        Expr left = ReadCond(b.Left);
        (List<Stmt> rightPre, Expr right) = Apart(() => ReadCond(b.Right));
        if (rightPre.Count == 0) return and ? new AndAlsoExpr(left, right) : new OrElseExpr(left, right);
        Slot t = Temp(isFloat: false);
        List<Stmt> testRight = [.. rightPre, new IfStmt(right, [Set(t, new IntLit(1))], null)];
        _pre.Add(Set(t, new IntLit(0)));
        _pre.Add(and ? new IfStmt(left, testRight, null) : new IfStmt(left, [Set(t, new IntLit(1))], testRight));
        return new LocalExpr(t);
    }

    /// <summary>A condition as a value (0 or 1): a short-circuit one through a temporary.</summary>
    private Expr Value(Expr condition) {
        if (!FlowEmitter.Branches(condition)) return condition;
        Slot t = Temp(isFloat: false);
        _pre.Add(Set(t, new IntLit(0)));
        _pre.Add(new IfStmt(condition, [Set(t, new IntLit(1))], null));
        return new LocalExpr(t);
    }

    /// <summary><c>c ? a : b</c>: a temporary set in the branches of an <c>if</c>.</summary>
    private Expr Conditional(ConditionalExpressionSyntax c) {
        Expr condition = ReadCond(c.Condition);
        (List<Stmt> whenTruePre, Expr whenTrue) = Apart(() => ReadExpr(c.WhenTrue));
        (List<Stmt> whenFalsePre, Expr whenFalse) = Apart(() => ReadExpr(c.WhenFalse));
        Slot t = Temp(IsFloat(whenTrue) || IsFloat(whenFalse));
        _pre.Add(new IfStmt(condition, [.. whenTruePre, Set(t, whenTrue)], [.. whenFalsePre, Set(t, whenFalse)]));
        return new LocalExpr(t);
    }

    /// <summary><c>x = c ? a : b;</c> into a variable, register or local: the branches assign it directly.</summary>
    private IfStmt? ConditionalInto(Expr target, ExpressionSyntax value) {
        if (target is not (VarExpr or RegExpr or LocalExpr) || Strip(value, conversions: true) is not ConditionalExpressionSyntax c) return null;
        Expr condition = ReadCond(c.Condition);
        (List<Stmt> whenTruePre, Expr whenTrue) = Apart(() => ReadExpr(c.WhenTrue));
        (List<Stmt> whenFalsePre, Expr whenFalse) = Apart(() => ReadExpr(c.WhenFalse));
        return new IfStmt(condition, [.. whenTruePre, new AssignStmt(target, whenTrue)], [.. whenFalsePre, new AssignStmt(target, whenFalse)]);
    }

    /// <summary><c>-x</c>: <c>neg</c> for a float (it works on floats only), <c>0 - x</c> for an integer.</summary>
    private Expr Negate(Expr x) => IsFloat(x) ? new UnaryExpr(Opcodes.Neg, x) : new BinaryExpr(Opcodes.Sub, new IntLit(0), x);

    // ----- ++ and -- ----------------------------------------------------------------------------

    private static bool IsIncDec(SyntaxKind k) => k is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression
        or SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression;

    /// <summary><c>x++;</c> as a statement: the same code as <c>x += 1;</c> (the <c>op_2b</c> form for an element).</summary>
    private Stmt IncDecStatement(ExpressionSyntax operand, bool increment) {
        Expr target = Target(operand);
        byte op = increment ? Opcodes.Add : Opcodes.Sub;
        return target is ElemExpr or Elem2Expr ? new CompoundStmt(target, op, new IntLit(1)) : new AssignStmt(target, new BinaryExpr(op, target, new IntLit(1)));
    }

    /// <summary>
    ///     <c>++x</c> as a value: the increment first, then <c>x</c>; <c>x++</c>: the old value into a
    ///     temporary, then the increment. An element's index is evaluated once, into a temporary.
    /// </summary>
    private Expr IncDecValue(ExpressionSyntax operand, bool increment, bool postfix) {
        Expr target = Target(operand);
        target = SpillIndex(target, _pre.Count);
        byte op = increment ? Opcodes.Add : Opcodes.Sub;
        if (!postfix) {
            _pre.Add(new AssignStmt(target, new BinaryExpr(op, target, new IntLit(1))));
            return target;
        }
        Slot old = Temp(IsFloat(target));
        _pre.Add(Set(old, target));
        _pre.Add(new AssignStmt(target, new BinaryExpr(op, new LocalExpr(old), new IntLit(1))));
        return new LocalExpr(old);
    }
}
