// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler;

// Inline methods (docs/encoding.md, "Inline methods"): once a worker is structured and checked, runs of
// statements that are exactly an inline method's template, with arguments in place of its parameters,
// become a call of it (Dialogue.Say(Text[71]);). The call lowers to the template with the arguments
// substituted, the same statements, so the worker is checked again with the calls in place, and keeps
// them only if it still gives the original back. Code in dead `if (false)` blocks is left as it is.
internal sealed partial class WorkerDecompiler {
    /// <summary>The native that opens a message window (<c>MsgOpen</c>): one per line of dialogue.</summary>
    internal const ushort MsgOpen = 0x006a;

    /// <summary>Inline calls by method, and the dialogue numbers (<c>dialogue-*</c>), for the stats.</summary>
    public Dictionary<string, int> InlineCounts { get; } = new(StringComparer.Ordinal);

    private void CountInline(string key, int n = 1) {
        if (n != 0) InlineCounts[key] = InlineCounts.GetValueOrDefault(key) + n;
    }

    /// <summary>Folds the runs of statements that are calls of <paramref name="macros"/> (tried in order, at each statement).</summary>
    public void FoldInline(IReadOnlyList<InlineMacro> macros) {
        CountInline("dialogue-lines", _code.Count(i => i.Code is Opcodes.Func or Opcodes.FuncFacc && i.Operand == MsgOpen));
        if (macros.Count == 0) return;
        List<List<Stmt>> before = [.. _methods.Select(m => m.Body)];
        List<InlineStmt> folded = [];
        int dead = 0;
        foreach (Method m in _methods) m.Body = Fold(m.Body, macros, folded, ref dead);
        CountInline("dialogue-dead", dead);
        if (folded.Count == 0) return;
        if (!Verify()) {
            for (int k = 0; k < _methods.Count; k++) _methods[k].Body = before[k];
            CountInline("inline-fallback-verify", folded.Count);
            return;
        }
        foreach (InlineStmt i in folded) {
            CountInline("inline " + i.Macro.Name);
            CountInline("dialogue-one-call", OpensMessage(i.Body));
        }
    }

    private static int OpensMessage(IReadOnlyList<Stmt> body) {
        int n = 0;
        Inline.Walk(body, e => {
            if (e is CallExpr { Native: MsgOpen }) n++;
        }, s => {
            if (s is CallStmt { Native: MsgOpen }) n++;
        });
        return n;
    }

    private List<Stmt> Fold(IReadOnlyList<Stmt> list, IReadOnlyList<InlineMacro> macros, List<InlineStmt> folded, ref int dead) {
        List<Stmt> result = [];
        for (int k = 0; k < list.Count; k++) {
            Stmt s = list[k];
            if (TryFold(list, k, macros) is (InlineStmt call, int length)) {
                result.Add(call);
                folded.Add(call);
                k += length - 1;
                continue;
            }
            // The dead code of an `if (false)` stays as the original had it.
            result.Add(s switch {
                IfStmt { Condition: null } i => i with { Then = Dead(i.Then, ref dead), Else = i.Else is null ? null : Fold(i.Else, macros, folded, ref dead) },
                IfStmt i => i with { Then = Fold(i.Then, macros, folded, ref dead), Else = i.Else is null ? null : Fold(i.Else, macros, folded, ref dead) },
                WhileStmt w => w with { Body = Fold(w.Body, macros, folded, ref dead) },
                DoStmt d => d with { Body = Fold(d.Body, macros, folded, ref dead) },
                ForStmt f => f with { Body = Fold(f.Body, macros, folded, ref dead) },
                SwitchStmt sw => sw with { Sections = FoldSections(sw.Sections, macros, folded, ref dead) },
                _ => s,
            });
        }
        return result;
    }

    private List<SwitchSection> FoldSections(IReadOnlyList<SwitchSection> sections, IReadOnlyList<InlineMacro> macros, List<InlineStmt> folded, ref int dead) {
        List<SwitchSection> result = [];
        foreach (SwitchSection x in sections) result.Add(x with { Body = Fold(x.Body, macros, folded, ref dead) });
        return result;
    }

    private static IReadOnlyList<Stmt> Dead(IReadOnlyList<Stmt> body, ref int dead) {
        dead += OpensMessage(body);
        return body;
    }

    private (InlineStmt, int)? TryFold(IReadOnlyList<Stmt> list, int at, IReadOnlyList<InlineMacro> macros) {
        foreach (InlineMacro m in macros) {
            if (m.Body.Count == 0 || !FirstMatches(m.Body[0], list[at])) continue;
            if (Inline.Match(m, list, at) is not Expr[] args) continue;
            InlineStmt call = new(m, args, Inline.Expand(m, args));
            try {
                _printer.Statement(call);
            }
            catch (NotPrintableException) {
                CountInline("inline-fallback-print");
                continue;
            }
            return (call, m.Body.Count);
        }
        return null;
    }

    /// <summary>A quick test before matching: the same kind of statement, and for a call the same native.</summary>
    private static bool FirstMatches(Stmt template, Stmt s) =>
        template.GetType() == s.GetType() && (template is not CallStmt t || ((CallStmt)s).Native == t.Native);
}
