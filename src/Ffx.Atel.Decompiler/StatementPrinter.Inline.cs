// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Decompiler.Naming;

namespace Ffx.Atel.Decompiler;

// A call of an inline method (docs/language.md, "Inline methods"): Dialogue.Say(Text[71], flags: 8194);
internal sealed partial class StatementPrinter {
    /// <summary>
    ///     The call: arguments in order while each differs from its parameter's default, named after the first
    ///     one left out. An argument is written as the native argument its parameter is first passed to would
    ///     be (<c>Text[12]</c> for a message).
    /// </summary>
    private string InlineCall(InlineStmt call) {
        InlineMacro m = call.Macro;
        List<string> args = [];
        bool named = false;
        for (int k = 0; k < m.Params.Count; k++) {
            InlineParam p = m.Params[k];
            Expr a = call.Args[k];
            if (p.Default is not null && Inline.Same(a, p.Default)) {
                named = true;
                continue;
            }
            string text = InlineArg(m, k, a);
            args.Add(named ? $"{p.Name}: {text}" : text);
        }
        // A worker named like the method's class would hide it; a helper of Worker is every worker's own.
        string cls = m.Name[..m.Name.IndexOf('.', StringComparison.Ordinal)];
        string name = m.CallName ?? (cls == "Worker" ? m.Name[(cls.Length + 1)..] : workerNames.Contains(cls, StringComparer.Ordinal) ? "Ffx." + m.Name : m.Name);
        return $"{(AsyncInlines.Contains(m) ? "await " : "")}{name}({string.Join(", ", args)});";
    }

    /// <summary>The inline methods that are <c>async Task</c> (their body awaits a request): a call is <c>await Name(…);</c>.</summary>
    public HashSet<InlineMacro> AsyncInlines { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Printing an inline method's body: its parameters, which <see cref="ParamExpr"/> names.</summary>
    public IReadOnlyList<InlineParam>? Params { get; set; }

    private E Param(ParamExpr p) {
        InlineParam x = Params is { } ps && p.Index < ps.Count ? ps[p.Index] : throw No("a parameter outside an inline method");
        return new E(x.Name, x.Float ? CType.Float : CType.Int, PPrimary);
    }

    private string InlineArg(InlineMacro m, int k, Expr a) {
        // A worker's own method types a parameter only as its type says (an int parameter takes a number).
        if (m.CallName is not null && m.Params[k].Type is null) return (m.Params[k].Float ? ToNum(Expr(a)) : ToInt(Expr(a))).Text;
        if (m.FirstUse[k] is (ushort native, int arg) && natives[native] is { } sig && arg < sig.Argc) {
            Domain?[]? domains = GameNames.NativeParameters.GetValueOrDefault(native);
            return (domains?[arg] is Domain d ? Typed(a, d) : sig.ArgKinds[arg] == 'f' ? ToNum(Expr(a)) : ToInt(Expr(a))).Text;
        }
        return (m.Params[k].Float ? ToNum(Expr(a)) : ToInt(Expr(a))).Text;
    }
}
