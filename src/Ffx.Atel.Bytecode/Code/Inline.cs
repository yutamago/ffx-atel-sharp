// SPDX-License-Identifier: MIT

using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace Ffx.Atel.Bytecode.Code;

// Inline methods (docs/encoding.md, "Inline methods"): a method whose code is written in place of each
// call, with its parameters replaced by the call's arguments. The compiler reads the method's body once
// into a template (statements with ParamExpr where a parameter is used) and expands a call by
// substitution; the decompiler finds the same template in lifted code by unification and prints the
// call. Both use the statements the compiler reads, so a call lowers to exactly the code it replaces:
// the same statements, so the same labels in the same order and the same pool constants.

/// <summary>A parameter of an inline method inside its template: replaced by the argument when a call is expanded.</summary>
public sealed record ParamExpr(int Index) : Expr;

/// <summary>
///     A parameter of an inline method: its name, whether it is a <c>float</c>, and its constant default.
///     <paramref name="Eager"/>: used once, in the method's first statement, before anything else there runs
///     (<c>[Eager]</c>), so its argument may be a call: substituted, it is evaluated once, first.
/// </summary>
public sealed record InlineParam(string Name, bool Float, Expr? Default, bool Eager = false);

/// <summary>An inline method: its name (<c>Dialogue.Say</c>), parameters and template.</summary>
public sealed class InlineMacro {
    public InlineMacro(string name, IReadOnlyList<InlineParam> parameters, IReadOnlyList<Stmt> body, bool waits) {
        Name = name;
        Params = parameters;
        Body = body;
        Waits = waits;
        (ushort, int)?[] uses = new (ushort, int)?[parameters.Count];
        Inline.Walk(body, e => {
            if (e is CallExpr c) Note(c.Native, c.Args);
        }, s => {
            if (s is CallStmt c) Note(c.Native, c.Args);
        });
        FirstUse = uses;
        Size = Inline.Count(body);

        void Note(ushort native, IReadOnlyList<Expr> args) {
            for (int k = 0; k < args.Count; k++) {
                if (args[k] is ParamExpr p && uses[p.Index] is null) uses[p.Index] = (native, k);
            }
        }
    }

    /// <summary>The C# name, <c>Class.Method</c>.</summary>
    public string Name { get; }

    public IReadOnlyList<InlineParam> Params { get; }

    /// <summary>The method's code, with a <see cref="ParamExpr"/> wherever a parameter is used.</summary>
    public IReadOnlyList<Stmt> Body { get; }

    /// <summary>Whether the code may wait (calls a native that polls).</summary>
    public bool Waits { get; }

    /// <summary>
    ///     Per parameter, the first native argument it is passed to as it is (native and argument index), so
    ///     that an argument is written as that native's argument would be (<c>Text[12]</c> for a message);
    ///     null for a parameter used only inside expressions.
    /// </summary>
    public IReadOnlyList<(ushort Native, int Arg)?> FirstUse { get; }

    /// <summary>Statements in the template, nested ones included: larger templates are tried first.</summary>
    public int Size { get; }

    public override string ToString() => Name;
}

/// <summary>
///     A call of an inline method, as the decompiler found it: <see cref="Body"/> is the method's template
///     with the arguments substituted, which is what the call lowers to (<see cref="FlowEmitter"/> lowers the
///     body in place).
/// </summary>
public sealed record InlineStmt(InlineMacro Macro, IReadOnlyList<Expr> Args, IReadOnlyList<Stmt> Body) : Stmt;

/// <summary>Expanding and finding inline method calls.</summary>
public static class Inline {
    /// <summary>
    ///     Whether an expression may be an argument: substituted where the parameter is used, it must give the
    ///     same value each time and have no effect, so no native call (and not <c>LastResult</c>, which the
    ///     method's own calls change), no short-circuit condition.
    /// </summary>
    public static bool IsArgument(Expr e) => e switch {
        // (A parameter of an inline method passed on to another, inside its template.)
        IntLit or FloatLit or PoolExpr or SymbolExpr or VarExpr or LocalExpr or ParamExpr => true,
        RegExpr r => r.Kind != RegKind.Facc,
        ElemExpr x => IsArgument(x.Index),
        Elem2Expr x => IsArgument(x.Row) && IsArgument(x.Col),
        AddrExpr x => IsArgument(x.Element),
        UnaryExpr x => IsArgument(x.Operand),
        BinaryExpr x => IsArgument(x.Left) && IsArgument(x.Right),
        _ => false,
    };

    /// <summary>The method's code for a call: the template with each parameter replaced by its argument (fresh statements).</summary>
    public static List<Stmt> Expand(InlineMacro macro, IReadOnlyList<Expr> args) {
        if (args.Count != macro.Params.Count) throw new ArgumentException($"{macro.Name} takes {macro.Params.Count} arguments");
        return [.. macro.Body.Select(s => Sub(s, args))];
    }

    /// <summary>
    ///     Whether an expression may be the argument of an <see cref="InlineParam.Eager"/> parameter: evaluated
    ///     once, at the start, it may call natives; it must not read <c>LastResult</c>.
    /// </summary>
    public static bool IsEagerArgument(Expr e) {
        bool ok = true;
        WalkExpr(e, x => ok &= x is not (RegExpr { Kind: RegKind.Facc } or ParamExpr));
        return ok;
    }

    /// <summary>
    ///     Whether parameter <paramref name="index"/> can be eager in <paramref name="body"/>: it is used once,
    ///     in the first statement (a native call or an assignment), where every native called holds the use.
    /// </summary>
    public static bool CanBeEager(IReadOnlyList<Stmt> body, int index) {
        int uses = 0;
        Walk(body, e => {
            if (e is ParamExpr p && p.Index == index) uses++;
        }, _ => { });
        if (uses != 1 || body.Count == 0 || body[0] is not (CallStmt or AssignStmt or CompoundStmt)) return false;
        int inFirst = 0;
        bool callsAside = false;
        Walk([body[0]], e => {
            if (e is ParamExpr p && p.Index == index) inFirst++;
            if (e is CallExpr c && !Holds(c, index)) callsAside = true;
        }, _ => { });
        return inFirst == 1 && !callsAside;

        static bool Holds(Expr e, int index) {
            bool found = false;
            WalkExpr(e, x => found |= x is ParamExpr p && p.Index == index);
            return found;
        }
    }

    /// <summary>
    ///     The arguments of a call of <paramref name="macro"/> that gives exactly the statements of
    ///     <paramref name="list"/> from <paramref name="at"/> on (as many as the template has), or null.
    ///     A parameter used twice must have the same argument both times.
    /// </summary>
    public static Expr[]? Match(InlineMacro macro, IReadOnlyList<Stmt> list, int at) {
        if (at + macro.Body.Count > list.Count) return null;
        Expr?[] bind = new Expr?[macro.Params.Count];
        bool[] eager = [.. macro.Params.Select(p => p.Eager)];
        for (int k = 0; k < macro.Body.Count; k++) {
            if (!Unify(macro.Body[k], list[at + k], bind, eager)) return null;
        }
        // A parameter the template never uses (none of ours) takes its default.
        for (int k = 0; k < bind.Length; k++) bind[k] ??= macro.Params[k].Default;
        return bind.Any(b => b is null) ? null : [.. bind.Select(b => b!)];
    }

    /// <summary>Whether two statements or expressions are the same tree (no parameters).</summary>
    public static bool Same(object? a, object? b) => Unify(a, b, [], []);

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> s_props = new();

    private static PropertyInfo[] Props(Type t) => s_props.GetOrAdd(t, x => [.. x.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0 && !(x == typeof(FloatLit) && p.Name == nameof(FloatLit.Value)))]);

    private static bool Unify(object? t, object? v, Expr?[] bind, bool[] eager) {
        if (t is ParamExpr p) {
            if (v is not Expr e) return false;
            if (bind[p.Index] is Expr had) return Unify(had, e, bind, eager);
            if (!IsArgument(e) && !(eager[p.Index] && IsEagerArgument(e))) return false;
            bind[p.Index] = e;
            return true;
        }
        if (t is null || v is null) return t is null && v is null;
        if (t is IList lt) {
            if (v is not IList lv || lt.Count != lv.Count) return false;
            for (int k = 0; k < lt.Count; k++) {
                if (!Unify(lt[k], lv[k], bind, eager)) return false;
            }
            return true;
        }
        if (t.GetType() != v.GetType()) return false;
        if (t is Stmt or Expr or SwitchSection or CaseLabel) {
            // An inline call inside a template (a helper that calls another): the same method, its arguments unified.
            if (t is InlineStmt ti) return ReferenceEquals(ti.Macro, ((InlineStmt)v).Macro) && Unify(ti.Args, ((InlineStmt)v).Args, bind, eager);
            foreach (PropertyInfo prop in Props(t.GetType())) {
                if (!Unify(prop.GetValue(t), prop.GetValue(v), bind, eager)) return false;
            }
            return true;
        }
        return Equals(t, v);
    }

    private static Expr Sub(Expr e, IReadOnlyList<Expr> a) => e switch {
        ParamExpr p => a[p.Index],
        ElemExpr x => new ElemExpr(x.Var, Sub(x.Index, a)),
        Elem2Expr x => new Elem2Expr(x.Var, Sub(x.Row, a), Sub(x.Col, a), x.RowLength),
        AddrExpr x => new AddrExpr(Sub(x.Element, a)),
        UnaryExpr x => new UnaryExpr(x.Op, Sub(x.Operand, a)),
        BinaryExpr x => new BinaryExpr(x.Op, Sub(x.Left, a), Sub(x.Right, a)),
        CallExpr x => new CallExpr(x.Native, Subs(x.Args, a)),
        AndAlsoExpr x => new AndAlsoExpr(Sub(x.Left, a), Sub(x.Right, a)),
        OrElseExpr x => new OrElseExpr(Sub(x.Left, a), Sub(x.Right, a)),
        IntLit or FloatLit or PoolExpr or SymbolExpr or VarExpr or RegExpr or LocalExpr => e,
        _ => throw new ArgumentException($"an inline method cannot hold {e.GetType().Name}"),
    };

    private static List<Expr> Subs(IReadOnlyList<Expr> list, IReadOnlyList<Expr> a) => [.. list.Select(x => Sub(x, a))];

    private static List<Stmt> Subs(IReadOnlyList<Stmt> list, IReadOnlyList<Expr> a) => [.. list.Select(x => Sub(x, a))];

    private static Stmt Sub(Stmt s, IReadOnlyList<Expr> a) => s switch {
        AssignStmt x => new AssignStmt(Sub(x.Target, a), Sub(x.Value, a), x.Clamped),
        CompoundStmt x => new CompoundStmt(Sub(x.Target, a), x.Op, Sub(x.Value, a)),
        CallStmt x => new CallStmt(x.Native, Subs(x.Args, a)),
        RequestStmt x => new RequestStmt(x.Op, Sub(x.Level, a), Sub(x.Worker, a), Sub(x.Entry, a)),
        CondGotoStmt x => new CondGotoStmt(x.Label, Sub(x.Condition, a), x.IfTrue),
        OpArgsStmt x => new OpArgsStmt(x.Op, Subs(x.Args, a)),
        EndWithStmt x => new EndWithStmt(x.Op, Sub(x.Value, a)),
        IfStmt x => new IfStmt(x.Condition is null ? null : Sub(x.Condition, a), Subs(x.Then, a), x.Else is null ? null : Subs(x.Else, a)),
        WhileStmt x => new WhileStmt(x.Condition is null ? null : Sub(x.Condition, a), Subs(x.Body, a)),
        DoStmt x => new DoStmt(Subs(x.Body, a), Sub(x.Condition, a)),
        ForStmt x => new ForStmt(Subs(x.Init, a), x.Condition is null ? null : Sub(x.Condition, a), Subs(x.Step, a), Subs(x.Body, a)),
        SwitchStmt x => new SwitchStmt(Sub(x.Selector, a), [.. x.Sections.Select(c => new SwitchSection(c.Label, Subs(c.Body, a)))]),
        CallWorkerStmt x => x with { },
        GotoStmt x => x with { },
        OpStmt x => x with { },
        NopStmt x => x with { },
        BreakStmt x => x with { },
        ContinueStmt x => x with { },
        GotoCaseStmt x => x with { },
        InlineStmt x => Nested(x.Macro, Subs(x.Args, a)),
        _ => throw new ArgumentException($"an inline method cannot hold {s.GetType().Name}"),
    };

    private static InlineStmt Nested(InlineMacro m, List<Expr> args) => new(m, args, Expand(m, args));

    /// <summary>Visits every expression and statement of a tree, nested ones included.</summary>
    public static void Walk(IReadOnlyList<Stmt> list, Action<Expr> expr, Action<Stmt> stmt) {
        foreach (Stmt s in list) Walk(s, expr, stmt);
    }

    private static void Walk(Stmt s, Action<Expr> expr, Action<Stmt> stmt) {
        stmt(s);
        void E(Expr? e) {
            if (e is not null) WalkExpr(e, expr);
        }
        void B(IReadOnlyList<Stmt>? b) {
            if (b is not null) Walk(b, expr, stmt);
        }
        switch (s) {
            case AssignStmt x: E(x.Target); E(x.Value); break;
            case CompoundStmt x: E(x.Target); E(x.Value); break;
            case CallStmt x: foreach (Expr e in x.Args) E(e); break;
            case RequestStmt x: E(x.Level); E(x.Worker); E(x.Entry); break;
            case CondGotoStmt x: E(x.Condition); break;
            case OpArgsStmt x: foreach (Expr e in x.Args) E(e); break;
            case EndWithStmt x: E(x.Value); break;
            case IfStmt x: E(x.Condition); B(x.Then); B(x.Else); break;
            case WhileStmt x: E(x.Condition); B(x.Body); break;
            case DoStmt x: B(x.Body); E(x.Condition); break;
            case ForStmt x: B(x.Init); E(x.Condition); B(x.Step); B(x.Body); break;
            case SwitchStmt x: E(x.Selector); foreach (SwitchSection c in x.Sections) B(c.Body); break;
            case InlineStmt x: B(x.Body); break;
        }
    }

    /// <summary>Visits an expression and every expression in it.</summary>
    public static void WalkExpr(Expr e, Action<Expr> visit) {
        visit(e);
        switch (e) {
            case ElemExpr x: WalkExpr(x.Index, visit); break;
            case Elem2Expr x: WalkExpr(x.Row, visit); WalkExpr(x.Col, visit); break;
            case AddrExpr x: WalkExpr(x.Element, visit); break;
            case UnaryExpr x: WalkExpr(x.Operand, visit); break;
            case BinaryExpr x: WalkExpr(x.Left, visit); WalkExpr(x.Right, visit); break;
            case CallExpr x: foreach (Expr a in x.Args) WalkExpr(a, visit); break;
            case AndAlsoExpr x: WalkExpr(x.Left, visit); WalkExpr(x.Right, visit); break;
            case OrElseExpr x: WalkExpr(x.Left, visit); WalkExpr(x.Right, visit); break;
        }
    }

    /// <summary>The statements of a tree, nested ones included.</summary>
    public static int Count(IReadOnlyList<Stmt> list) {
        int n = 0;
        Walk(list, _ => { }, _ => n++);
        return n;
    }
}
