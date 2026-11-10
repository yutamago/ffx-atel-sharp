// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler;

/// <summary>
///     Variables the script uses as flags are declared <c>bool</c> (docs/plan.md, Phase 5, "Fewer <c>AsBool</c>"): a
///     scalar integer variable whose every write is 0, 1 or a condition and every read a condition (or a value passed
///     on as it is) reads <c>if (V4)</c>, <c>V4 = true;</c> instead of <c>if (AsBool(V4))</c>, <c>V4 = 1;</c>. The
///     bytes are the same: <c>true</c> is <c>pushimm 1</c>, and a <c>bool</c> variable is a condition to the compiler
///     (<c>V0 &amp; !V4</c> is <c>land</c>), kept in its integer type (<c>[Var(…, Stored = typeof(byte))]</c>).
/// </summary>
/// <remarks>
///     A read passed on as a value (<c>Btl.DebugPrint(V4)</c>, an argument) is written <c>AsInt(V4)</c>, so a variable
///     becomes <c>bool</c> only where more <c>AsBool</c> go than <c>AsInt</c> come. Variables assigned from each other
///     are typed together. Anything else (arithmetic, a comparison with a number, a compound or clamped store, a
///     selector) keeps the variable an integer.
/// </remarks>
internal sealed class FlagVariables {
    private readonly NativeSignatures _natives;
    private readonly Func<Expr, int?> _slot;
    private readonly bool[] _out;
    private readonly int[] _conditions, _values, _parent;

    /// <summary>The code calls a function worker (<c>W48.Run()</c>).</summary>
    private bool _callsWorker;

    /// <summary>Inside a body that is not printed: every slot read or written is a number.</summary>
    private bool _numbers;

    /// <param name="count">The number of slots (variables or registers).</param>
    /// <param name="slot">The slot an expression is (<c>V4</c>, <c>I3</c>), if it is one.</param>
    /// <param name="fixedOut">Slots that cannot be flags whatever their uses.</param>
    private FlagVariables(int count, Func<Expr, int?> slot, Func<int, bool> fixedOut, NativeSignatures natives) {
        _slot = slot;
        _natives = natives;
        _out = new bool[count];
        _conditions = new int[count];
        _values = new int[count];
        _parent = [.. Enumerable.Range(0, count)];
        for (int i = 0; i < count; i++) _out[i] = fixedOut(i);
    }

    /// <summary>Types the flag variables of <paramref name="vars"/> <c>bool</c>, from every statement the source prints; the indices typed.</summary>
    public static List<int> Apply(VarInfo[] vars, IEnumerable<IReadOnlyList<Stmt>> bodies, NativeSignatures natives) {
        List<int> typed = Flags(vars.Length, e => e is VarExpr v ? v.Var : null,
            i => vars[i] is { IsArray: true } or { Domain: not null } or { Bits: not null } || vars[i].Type is not (CType.Byte or CType.SByte or CType.UShort or CType.Short or CType.Int),
            bodies, natives);
        foreach (int i in typed) vars[i] = vars[i] with { Type = CType.Bool, Stored = vars[i].Type };
        return typed;
    }

    /// <summary>
    ///     The integer registers (<c>I0</c>-<c>I3</c>) a worker's code uses as flags, by number: declared <c>bool</c>. None
    ///     in a function worker or a worker that calls one: they share registers, so a register may hold what the other
    ///     left in it.
    /// </summary>
    public static List<int> Registers(IEnumerable<IReadOnlyList<Stmt>> bodies, NativeSignatures natives, bool functionWorker) {
        if (functionWorker) return [];
        List<IReadOnlyList<Stmt>> all = [.. bodies];
        FlagVariables probe = new(0, _ => null, _ => true, natives);
        foreach (IReadOnlyList<Stmt> body in all) probe.Block(body);
        if (probe._callsWorker) return [];
        return Flags(4, e => e is RegExpr { Kind: RegKind.Int, Number: >= 0 and < 4 } r ? r.Number : null, _ => false, all, natives);
    }

    private static List<int> Flags(int count, Func<Expr, int?> slot, Func<int, bool> fixedOut, IEnumerable<IReadOnlyList<Stmt>> bodies, NativeSignatures natives) {
        FlagVariables f = new(count, slot, fixedOut, natives);
        foreach (IReadOnlyList<Stmt> body in bodies) f.Block(body);
        // Slots assigned from each other go together: all bool or none.
        Dictionary<int, (bool Out, int Conditions, int Values)> groups = [];
        for (int i = 0; i < count; i++) {
            int r = f.Find(i);
            (bool o, int c, int v) = groups.GetValueOrDefault(r);
            groups[r] = (o || f._out[i], c + f._conditions[i], v + f._values[i]);
        }
        List<int> typed = [];
        for (int i = 0; i < count; i++) {
            (bool o, int c, int v) = groups[f.Find(i)];
            if (!o && c > v) typed.Add(i);
        }
        return typed;
    }

    private int Find(int i) {
        while (_parent[i] != i) i = _parent[i] = _parent[_parent[i]];
        return i;
    }

    private void Block(IReadOnlyList<Stmt> body) {
        foreach (Stmt s in body) Statement(s);
    }

    private void Statement(Stmt s) {
        switch (s) {
            case AssignStmt a when _slot(a.Target) is int t:
                if (a.Clamped || a.Value == a.Target) _out[t] = true;
                else Assigned(t, a.Value);
                break;
            case AssignStmt a:
                Index(a.Target);
                Value(a.Value);
                break;
            case CompoundStmt c:
                if (_slot(c.Target) is int ct) _out[ct] = true;
                Index(c.Target);
                Other(c.Value);
                break;
            case CallStmt c:
                Arguments(c.Native, c.Args);
                break;
            case RequestStmt r:
                Value(r.Level);
                Value(r.Worker);
                Value(r.Entry);
                break;
            case CallWorkerStmt:
                _callsWorker = true;
                break;
            case CondGotoStmt g:
                Condition(g.Condition);
                break;
            case OpArgsStmt o:
                foreach (Expr a in o.Args) Value(a);
                break;
            case EndWithStmt e:
                Value(e.Value);
                break;
            case InlineStmt i:
                // The call is printed, not the body it stands for; a reference-assembly method's body may keep a
                // local in a register (`[InlineLocal]`, Turn.To's I3), which is then a number.
                foreach (Expr a in i.Args) Value(a);
                if (i.Macro.CallName is null) {
                    bool was = _numbers;
                    _numbers = true;
                    Block(i.Body);
                    _numbers = was;
                }
                break;
            case IfStmt i:
                if (i.Condition is not null) Condition(i.Condition);
                Block(i.Then);
                if (i.Else is not null) Block(i.Else);
                break;
            case WhileStmt w:
                if (w.Condition is not null) Condition(w.Condition);
                Block(w.Body);
                break;
            case DoStmt d:
                Block(d.Body);
                Condition(d.Condition);
                break;
            case ForStmt f:
                Block(f.Init);
                if (f.Condition is not null) Condition(f.Condition);
                Block(f.Step);
                Block(f.Body);
                break;
            case SwitchStmt sw:
                Other(sw.Selector);
                foreach (SwitchSection section in sw.Sections) Block(section.Body);
                break;
        }
    }

    /// <summary><c>v = value</c>: 0, 1, a condition or another flag keep it a flag.</summary>
    private void Assigned(int v, Expr value) {
        if (_numbers) {
            _out[v] = true;
            Other(value);
            return;
        }
        switch (value) {
            case IntLit { Value: 0 or 1 }:
                return;
            case Expr w when _slot(w) is int k:
                _parent[Find(v)] = Find(k);
                return;
            case VarExpr or RegExpr:
                // A variable from a register or the other way: a number.
                _out[v] = true;
                return;
            case BinaryExpr { Op: Opcodes.Lt or Opcodes.Gt or Opcodes.Le or Opcodes.Ge or Opcodes.Eq or Opcodes.Ne } b:
                Other(b.Left);
                Other(b.Right);
                return;
            case BinaryExpr { Op: Opcodes.Land or Opcodes.Lor } or UnaryExpr { Op: Opcodes.Not } or AndAlsoExpr or OrElseExpr:
                Condition(value);
                return;
            case CallExpr c when _natives[c.Native]?.Result == "bool":
                Arguments(c.Native, c.Args);
                return;
            default:
                _out[v] = true;
                Other(value);
                return;
        }
    }

    /// <summary>A condition: a flag read here needs no <c>AsBool</c>.</summary>
    private void Condition(Expr e) {
        if (_slot(e) is int k && !_numbers) {
            _conditions[k]++;
            return;
        }
        switch (e) {
            case UnaryExpr { Op: Opcodes.Not } u:
                Condition(u.Operand);
                return;
            case BinaryExpr { Op: Opcodes.Land or Opcodes.Lor } b:
                Condition(b.Left);
                Condition(b.Right);
                return;
            case AndAlsoExpr a:
                Condition(a.Left);
                Condition(a.Right);
                return;
            case OrElseExpr o:
                Condition(o.Left);
                Condition(o.Right);
                return;
            default:
                Other(e);
                return;
        }
    }

    /// <summary>A value passed on as it is (an argument): a flag read here is <c>AsInt(v)</c>.</summary>
    private void Value(Expr e) {
        if (_slot(e) is int k && !_numbers) _values[k]++;
        else Other(e);
    }

    /// <summary>Anything else: a variable read here is a number.</summary>
    private void Other(Expr e) {
        if (_slot(e) is int k) {
            _out[k] = true;
            return;
        }
        switch (e) {
            case ElemExpr x:
                Other(x.Index);
                return;
            case Elem2Expr x:
                Other(x.Row);
                Other(x.Col);
                return;
            case AddrExpr a:
                Other(a.Element);
                return;
            case UnaryExpr { Op: Opcodes.Not } u:
                Condition(u.Operand);
                return;
            case UnaryExpr u:
                Other(u.Operand);
                return;
            case BinaryExpr { Op: Opcodes.Land or Opcodes.Lor } b:
                Condition(b.Left);
                Condition(b.Right);
                return;
            case BinaryExpr b:
                Other(b.Left);
                Other(b.Right);
                return;
            case AndAlsoExpr or OrElseExpr:
                Condition(e);
                return;
            case CallExpr c:
                Arguments(c.Native, c.Args);
                return;
        }
    }

    /// <summary>A native's arguments: values, except where an overload takes a <c>bool</c> (<c>Btl.DebugPrint(V4)</c>), which takes a flag as it is.</summary>
    private void Arguments(ushort native, IReadOnlyList<Expr> args) {
        for (int k = 0; k < args.Count; k++) {
            if (_slot(args[k]) is not null && Naming.GameNames.BoolArguments.Contains((native, k))) continue;
            Value(args[k]);
        }
    }

    /// <summary>The indices of an assignment's target.</summary>
    private void Index(Expr target) {
        switch (target) {
            case ElemExpr x:
                Other(x.Index);
                break;
            case Elem2Expr x:
                Other(x.Row);
                Other(x.Col);
                break;
        }
    }
}
