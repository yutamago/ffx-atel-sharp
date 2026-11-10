// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Compiler;
using Ffx.Atel.Decompiler.Naming;

namespace Ffx.Atel.Decompiler;

// The object-oriented surface (docs/language.md, "Members"): a native that is a member is written as the member,
// `Pos = new Vector3(…);`, `Turn.Start(…);`, `Tidus.Pos.X`, `Wait.Frames(95);`, where it reads back as the same
// call (StatementReader.Members); everything else stays the native's call.
internal sealed partial class StatementPrinter {
    private HashSet<string>? _declared;

    /// <summary>Whether a root names nothing the script declares (a variable or worker would hide it, or be hidden).</summary>
    private bool Free(string name) {
        _declared ??= new HashSet<string>([.. vars.Select(v => v.Name), .. workerNames], StringComparer.Ordinal);
        return !_declared.Contains(name);
    }

    /// <summary>
    ///     What stands before the member: nothing for this worker or a static class, <c>Tidus.</c> or
    ///     <c>Workers[n].</c> for another worker, <c>Sprites[n].</c> for an indexed group; null where the member cannot
    ///     say it (this worker, where a native of its own takes the member: <see cref="MemberNative.SelfTwin"/>).
    /// </summary>
    private string? Receiver(MemberNative m, IReadOnlyList<Expr> args) {
        switch (m.Receiver) {
            case MemberReceiver.None:
                return "";
            case MemberReceiver.Index:
                // Typed as the native's argument (Actors[Target.AllPly]).
                return $"{m.Root}[{(GameNames.NativeParameters.GetValueOrDefault(m.Id)?[0] is Domain d ? Typed(args[0], d) : ToInt(Expr(args[0]))).Text}].";
            default:
                if (args[0] is CallExpr { Native: KnownNatives.GetWorkerId, Args: [IntLit { Value: -1 }] }) {
                    if (m.SelfTwin) return null;
                    Count("member-self");
                    return "";
                }
                string? other = args[0] is IntLit { Value: int w } && w >= 0 && w < workerNames.Length ? workerNames[w] + "."
                    : Free("Workers") ? $"Workers[{ToInt(Expr(args[0])).Text}]." : null;
                if (other is not null) Count("member-worker");
                return other;
        }
    }

    private string? MemberTarget(MemberNative m, IReadOnlyList<Expr> args, NativeSignature sig) {
        if (sig.Argc != args.Count || !Free(m.Root)) return null;
        string? prefix = Receiver(m, args);
        if (prefix is null) return null;
        return prefix + (m.Receiver == MemberReceiver.Index ? m.Name : m.Path);
    }

    /// <summary>A native's argument <paramref name="k"/> as the member's: typed as for the native, a bool as <c>true</c>/<c>false</c>.</summary>
    private string MemberArg(ushort id, NativeSignature sig, IReadOnlyList<Expr> args, int k, bool asBool = false) {
        Domain?[]? domains = GameNames.NativeParameters.GetValueOrDefault(id);
        if (domains?[k] is Domain d) return Typed(args[k], d).Text;
        if (asBool) return args[k] is IntLit { Value: 0 or 1 } b ? (b.Value == 1 ? "true" : "false") : ToBool(Expr(args[k])).Text;
        return (sig.ArgKinds[k] == 'f' ? ToNum(Expr(args[k])) : ToInt(Expr(args[k]))).Text;
    }

    /// <summary>A native call as a statement written as its member (<c>Turn.Start(0, 4099, 0);</c>, <c>Yaw = 1.5f;</c>); null where it is none.</summary>
    private string? MemberStatement(CallStmt c) {
        if (NativeMembers.Default.Of(c.Native, c.Args) is not MemberNative m || m.Use == MemberUse.Get || natives[c.Native] is not NativeSignature sig) return null;
        int first = (m.Receiver == MemberReceiver.None ? 0 : 1) + (m.Constant is null ? 0 : 1);
        string? text = null;
        if (MemberTarget(m, c.Args, sig) is string target) {
            if (m.Use == MemberUse.Call) {
                IEnumerable<string> args = Enumerable.Range(first, sig.Argc - first).Select(k => MemberArg(c.Native, sig, c.Args, k));
                // A choice member names its native by its first argument: Camera.Eye.Shake(ShakeKind.SineFadeOut, …).
                if (m.ChoiceText is string choice) args = args.Prepend(choice);
                text = $"{target}({string.Join(", ", args)});";
            }
            else if (m.Vector) {
                text = $"{target} = new Vector3({string.Join(", ", Enumerable.Range(first, 3).Select(k => MemberArg(c.Native, sig, c.Args, k)))});";
            }
            else {
                text = $"{target} = {MemberArg(c.Native, sig, c.Args, first, m.Bool)};";
            }
        }
        if (text is not null) Count(m.Constant is null ? "member" : "member-property");
        return text;
    }

    /// <summary>A native call as a value written as its member (<c>Turn.GetKind()</c>, <c>Tidus.Pos.X</c>); null where it is none.</summary>
    private E? MemberValue(CallExpr c) {
        if (NativeMembers.Default.Of(c.Native, c.Args) is not MemberNative m || m.Use == MemberUse.Set || m.Void || natives[c.Native] is not NativeSignature sig) return null;
        if (MemberTarget(m, c.Args, sig) is not string target) return null;
        int first = (m.Receiver == MemberReceiver.None ? 0 : 1) + (m.Constant is null ? 0 : 1);
        IEnumerable<string> args = Enumerable.Range(first, sig.Argc - first).Select(k => MemberArg(c.Native, sig, c.Args, k));
        if (m.ChoiceText is string choice) args = args.Prepend(choice);
        string text = m.Use == MemberUse.Get ? target : $"{target}({string.Join(", ", args)})";
        Count(m.Constant is null ? "member" : "member-property");
        return new E(text, sig.Result switch { "float" => CType.Float, "bool" => CType.Bool, _ => CType.Int }, PPrimary);
    }
}

/// <summary>Native numbers the printer recognises.</summary>
internal static class KnownNatives {
    /// <summary><c>Common.GetWorkerId</c>: with -1, this worker's number.</summary>
    public const ushort GetWorkerId = 0x0033;
}
