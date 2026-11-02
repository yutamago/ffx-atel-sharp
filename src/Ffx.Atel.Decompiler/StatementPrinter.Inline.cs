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
        // A worker named like the method's class would hide it.
        string name = workerNames.Contains(m.Name[..m.Name.IndexOf('.', StringComparison.Ordinal)], StringComparer.Ordinal) ? "Ffx." + m.Name : m.Name;
        return $"{name}({string.Join(", ", args)});";
    }

    private string InlineArg(InlineMacro m, int k, Expr a) {
        if (m.FirstUse[k] is (ushort native, int arg) && natives[native] is { } sig && arg < sig.Argc) {
            Domain?[]? domains = GameNames.NativeParameters.GetValueOrDefault(native);
            return (domains?[arg] is Domain d ? Typed(a, d) : sig.ArgKinds[arg] == 'f' ? ToNum(Expr(a)) : ToInt(Expr(a))).Text;
        }
        return (m.Params[k].Float ? ToNum(Expr(a)) : ToInt(Expr(a))).Text;
    }
}
