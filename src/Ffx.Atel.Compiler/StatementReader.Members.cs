// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

// The object-oriented surface (docs/language.md, "Members"; NativeMembers): a member of the worker, of a component,
// of an indexed group or of a static class is its native. A call is the native's call with the same arguments, a
// read the native's result, an assignment the native's call with the value; a Vector3 is its three components,
// and the worker a member acts on is the native's first argument.
internal sealed partial class StatementReader {
    /// <summary>GetWorkerId (<c>0x0033</c>): with -1, this worker's number.</summary>
    internal const ushort GetWorkerId = 0x0033;

    private static NativeMembers Members => NativeMembers.Default;

    /// <summary>A member as written: its path and what stands before it.</summary>
    /// <param name="Receiver">
    ///     The expression of the worker (<c>n</c> in <c>Workers[n]</c>) or index (<c>Sprites[n]</c>); null when the
    ///     member is written without one.
    /// </param>
    /// <param name="Worker">The worker's number when written as its class (<c>Tidus.…</c>).</param>
    private sealed record MemberSyntax(string Path, ExpressionSyntax? Receiver, int? Worker, bool Indexed, SyntaxNode Node);

    /// <summary>Whether a simple name is a local, register or variable (which the member does not hide).</summary>
    private bool IsDataName(string name) => Local(name) is not null || registers.ContainsKey(name) || variables.ContainsKey(name);

    /// <summary>The member an expression names, if it is one: <c>Pos</c>, <c>Turn.Yaw</c>, <c>Tidus.Pos.X</c>, <c>Workers[n].Turn</c>, <c>Sprites[3].Show</c>, <c>Camera.Eye.AimAt</c>.</summary>
    private MemberSyntax? MemberOf(ExpressionSyntax e) {
        switch (e) {
            case IdentifierNameSyntax id: {
                string n = id.Identifier.ValueText;
                return Members.WorkerRoots.Contains(n) && !Members.IndexedRoots.Contains(n) && !IsDataName(n) ? new MemberSyntax(n, null, null, false, e) : null;
            }
            case MemberAccessExpressionSyntax { Name: IdentifierNameSyntax name } m when m.IsKind(SyntaxKind.SimpleMemberAccessExpression): {
                string member = name.Identifier.ValueText;
                switch (m.Expression) {
                    // Wait.Frames, Turn.Yaw, Pos.X; Tidus.Yaw.
                    case IdentifierNameSyntax left: {
                        string l = left.Identifier.ValueText;
                        if (IsDataName(l)) return null;
                        if (Members.StaticRoots.Contains(l) || Members.WorkerRoots.Contains(l) && !Members.IndexedRoots.Contains(l)) {
                            return new MemberSyntax($"{l}.{member}", null, null, false, e);
                        }
                        if (Members.WorkerRoots.Contains(member) && workers.TryGetValue(l, out int w)) return new MemberSyntax(member, null, w, false, e);
                        return null;
                    }
                    // Workers[n].Yaw; Sprites[3].Show.
                    case ElementAccessExpressionSyntax { Expression: IdentifierNameSyntax list, ArgumentList.Arguments: [ArgumentSyntax index] }: {
                        string l = list.Identifier.ValueText;
                        if (IsDataName(l)) return null;
                        if (l == "Workers") return new MemberSyntax(member, index.Expression, null, false, e);
                        return Members.IndexedRoots.Contains(l) ? new MemberSyntax($"{l}.{member}", index.Expression, null, true, e) : null;
                    }
                    // Tidus.Turn.Stop, Workers[n].Pos.X.
                    case MemberAccessExpressionSyntax inner when MemberOf(inner) is { Path: string root } r && !root.Contains('.') && (r.Worker is not null || r.Receiver is not null):
                        return r with { Path = $"{root}.{member}", Node = e };
                    // Camera.Eye.AimAt: a member of a nested static group.
                    case MemberAccessExpressionSyntax inner when MemberOf(inner) is { Path: string group, Worker: null, Receiver: null } r && Members.NestedGroups.Contains(group):
                        return r with { Path = $"{group}.{member}", Node = e };
                    default:
                        return null;
                }
            }
            default:
                return null;
        }
    }

    /// <summary>The native for a member used so, and the receiver argument it needs (null for none).</summary>
    private (MemberNative Native, NativeSignature Sig, Expr? Fixed, ExpressionSyntax? Receiver)? Resolve(MemberSyntax m, MemberUse use, int? argc = null) {
        IReadOnlyList<MemberNative> all = Members.Find(m.Path, use);
        if (all.Count == 0) return null;
        NativeSignature Sig(MemberNative n) => ScriptCompiler.Natives[n.Id] ?? throw Error(m.Node, $"no native 0x{n.Id:x4}");
        IEnumerable<MemberNative> fits = all.Where(n => argc is null || NativeMembers.ValueCount(n, Sig(n)) == argc);
        MemberNative? chosen;
        if (m.Indexed) {
            chosen = fits.FirstOrDefault(n => n.Receiver == MemberReceiver.Index);
            return chosen is null ? null : (chosen, Sig(chosen), null, m.Receiver);
        }
        if (m.Receiver is not null || m.Worker is not null) {
            chosen = fits.FirstOrDefault(n => n.Receiver == MemberReceiver.Worker)
                ?? throw Error(m.Node, $"{m.Path} acts on this worker only (no native takes another worker)");
            return (chosen, Sig(chosen), m.Worker is int w ? new IntLit(w) : null, m.Receiver);
        }
        chosen = fits.FirstOrDefault(n => n.Receiver == MemberReceiver.None);
        if (chosen is not null) return (chosen, Sig(chosen), null, null);
        chosen = fits.FirstOrDefault(n => n.Receiver == MemberReceiver.Worker);
        // Written without a worker: this one, Common.GetWorkerId(-1).
        return chosen is null ? null : (chosen, Sig(chosen), new CallExpr(GetWorkerId, [new IntLit(-1)]), null);
    }

    /// <summary>A member call (<c>Turn.Start(0, 4099, 0)</c>, <c>Tidus.Turn.Stop()</c>): the native and its arguments; null if the call is no member.</summary>
    private (ushort Id, IReadOnlyList<Expr> Args)? MemberCall(InvocationExpressionSyntax call) {
        if (MemberOf(call.Expression) is not MemberSyntax m) return null;
        int argc = call.ArgumentList.Arguments.Count;
        if (Resolve(m, MemberUse.Call, argc) is not var (native, sig, fixedArg, receiver)) {
            if (Members.Find(m.Path, MemberUse.Call).Count > 0) throw Error(call, $"{m.Path} takes no {argc} arguments");
            return null;
        }
        return (native.Id, NativeArgs(sig, m.Path, call, call.ArgumentList.Arguments, fixedArg, receiver));
    }

    /// <summary>A member read (<c>Yaw</c>, <c>Tidus.Pos.X</c>, <c>Move.YawToTarget</c>): the native's result; null if it is no member.</summary>
    private Expr? MemberGet(ExpressionSyntax e) {
        if (MemberOf(e) is not MemberSyntax m) return null;
        if (Resolve(m, MemberUse.Get) is not var (native, sig, fixedArg, receiver)) {
            if (Members.Find(m.Path, MemberUse.Set).Count > 0 || Members.Find(m.Path, MemberUse.Call).Count > 0) {
                throw Error(e, $"{m.Path} is not read (no native returns it)");
            }
            return null;
        }
        return new CallExpr(native.Id, NativeArgs(sig, m.Path, e, default, fixedArg, receiver));
    }

    /// <summary>
    ///     An assignment to a member (<c>Yaw = …;</c>, <c>Pos = new Vector3(x, y, z);</c>, <c>Rikku.AutoTalk = true;</c>):
    ///     the native's call with the value; null if the target is no member.
    /// </summary>
    private Stmt? MemberSet(AssignmentExpressionSyntax a) {
        if (MemberOf(a.Left) is not MemberSyntax m) return null;
        if (Resolve(m, MemberUse.Set) is not var (native, sig, fixedArg, receiver)) {
            if (Members.Find(m.Path, MemberUse.Get).Count > 0) throw Error(a.Left, $"{m.Path} is read only (no native sets it)");
            return null;
        }
        if (!a.IsKind(SyntaxKind.SimpleAssignmentExpression)) throw Error(a, $"{m.Path} takes a plain assignment (`=`): it is a native call");
        List<ExpressionSyntax> values;
        if (native.Vector) {
            ExpressionSyntax v = Strip(a.Right, conversions: false);
            ArgumentListSyntax? list = v switch {
                ObjectCreationExpressionSyntax o when TypeNameOf(o.Type) == "Vector3" => o.ArgumentList,
                ImplicitObjectCreationExpressionSyntax io => io.ArgumentList,
                _ => null,
            };
            if (list is not { Arguments.Count: 3 } || list.Arguments.Any(x => x.NameColon is not null || !x.RefKindKeyword.IsKind(SyntaxKind.None))) {
                throw Error(a.Right, $"{m.Path} takes `new Vector3(x, y, z)`: its three values go to the native as they are");
            }
            values = [.. list.Arguments.Select(x => x.Expression)];
        }
        else {
            values = [a.Right];
        }
        SeparatedSyntaxList<ArgumentSyntax> args = SyntaxFactory.SeparatedList(values.Select(SyntaxFactory.Argument));
        return new CallStmt(native.Id, NativeArgs(sig, m.Path, a, args, fixedArg, receiver));
    }

    /// <summary>
    ///     A native's arguments: the receiver (a fixed value, or the expression before the member, evaluated first, as C#
    ///     does), then the member's arguments by position or name.
    /// </summary>
    private List<Expr> NativeArgs(NativeSignature sig, string name, SyntaxNode at, SeparatedSyntaxList<ArgumentSyntax> args, Expr? fixedArg, ExpressionSyntax? receiver) {
        int skip = fixedArg is not null || receiver is not null ? 1 : 0;
        int count = sig.Argc - skip;
        if (args.Count != count) throw Error(at, $"{name} takes {count} arguments");
        string[]? names = sig.ParamNames?.Skip(skip).ToArray();
        int[] position = new int[args.Count];
        bool[] given = new bool[count];
        for (int k = 0; k < args.Count; k++) {
            ArgumentSyntax a = args[k];
            if (!a.RefKindKeyword.IsKind(SyntaxKind.None)) throw Error(a, "natives take values (Addr(ref …) for an address)");
            int p = k;
            if (a.NameColon is not null) {
                string param = a.NameColon.Name.Identifier.ValueText;
                p = names is null ? -1 : Array.IndexOf(names, param);
                if (p < 0) throw Error(a.NameColon, $"{name} has no parameter {param}");
            }
            if (given[p]) throw Error(a, $"{name}'s parameter {p} is given twice");
            given[p] = true;
            position[k] = p;
        }
        List<Expr> values = ReadArgs(receiver is null ? args.Select(a => a.Expression) : [receiver, .. args.Select(a => a.Expression)]);
        Expr? first = fixedArg;
        if (receiver is not null) {
            first = values[0];
            values.RemoveAt(0);
        }
        if (!position.Select((p, k) => p == k).All(same => same)) {
            // Out of the parameters' order: evaluated as written, into temporaries, then pushed in order.
            int mark = _pre.Count;
            if (receiver is not null) {
                int before = _pre.Count;
                first = SpillAt(first!, mark);
                mark += _pre.Count - before;
            }
            for (int k = 0; k < values.Count; k++) {
                int before = _pre.Count;
                values[k] = SpillAt(values[k], mark);
                mark += _pre.Count - before;
            }
            Expr[] ordered = new Expr[count];
            for (int k = 0; k < values.Count; k++) ordered[position[k]] = values[k];
            values = [.. ordered];
        }
        return first is null ? values : [first, .. values];
    }

    /// <summary>Whether a member read or call gives a <c>bool</c> (a native typed so).</summary>
    private bool MemberIsBool(ExpressionSyntax e) {
        try {
            InvocationExpressionSyntax? call = e as InvocationExpressionSyntax;
            if (MemberOf(call?.Expression ?? e) is not MemberSyntax m) return false;
            return Resolve(m, call is null ? MemberUse.Get : MemberUse.Call, call?.ArgumentList.Arguments.Count) is { Sig.Result: "bool" };
        }
        catch (CompileException) {
            return false;
        }
    }
}
