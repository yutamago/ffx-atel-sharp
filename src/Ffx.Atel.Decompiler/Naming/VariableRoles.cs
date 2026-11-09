// SPDX-License-Identifier: MIT

using System.Globalization;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler.Naming;

/// <summary>
///     Names for a script's own variables (storage 2, 3, 4 and 6: the worker's data, its private data, the
///     script's shared data, the event data) that still have their number (<c>V12</c>), by what the code does
///     with them (docs/source-repo.md, "Names"):
///     <list type="bullet">
///         <item>
///             only ever the table of <c>SetTriggerEntry(Addr(ref X[0]), kind, entry)</c> (<c>op_79</c>, which
///             fills it with the type's default trigger entries and points the worker at it):
///             <c>TriggerEntries</c>;
///         </item>
///         <item>
///             a plain variable every store to which is the result of one native, and which is read: named after
///             the native without <c>Get</c> (<c>MsgChoiceResult</c>, <c>MotionFrame</c> for
///             <c>Chr.GetMotionFrame</c>).
///         </item>
///     </list>
///     Save variables (storage 0) and the variables every script shares (storage 1) keep names that do not
///     depend on one script's code: <see cref="Ffx.Save"/> and <c>data/variable-names.toml</c>. A name taken
///     twice gets <c>_2</c>, <c>_3</c>, … in table order, as variables declared twice do.
/// </summary>
internal static class VariableRoles {
    /// <summary>Names the variables in place: <paramref name="names"/> and <paramref name="vars"/> (which the printers hold).</summary>
    public static void Assign(WorkerDecompiler[] workers, IReadOnlyList<VarEntry> table, string[] names, VarInfo[] vars, ISet<string> reserved) {
        int n = table.Count;
        bool[] candidate = new bool[n];
        for (int i = 0; i < n; i++) {
            candidate[i] = table[i].Storage is 2 or 3 or 4 or 6 && names[i] == string.Create(CultureInfo.InvariantCulture, $"V{i}");
        }
        if (!candidate.Any(c => c)) return;
        bool[] tableUse = new bool[n], otherUse = new bool[n], badWrite = new bool[n];
        int[] reads = new int[n];
        HashSet<ushort>?[] writers = new HashSet<ushort>?[n];
        HashSet<Expr> skip = new(ReferenceEqualityComparer.Instance);
        foreach (WorkerDecompiler worker in workers) {
            foreach (StructuredMethod m in worker.Bodies) {
                Inline.Walk(m.Body, e => {
                    if (skip.Contains(e)) return;
                    int v = e switch {
                        VarExpr x => x.Var,
                        ElemExpr x => x.Var,
                        Elem2Expr x => x.Var,
                        _ => -1,
                    };
                    if (v < 0 || v >= n) return;
                    otherUse[v] = true;
                    reads[v]++;
                }, s => {
                    switch (s) {
                        case OpArgsStmt { Op: Opcodes.Op79, Args: [AddrExpr { Element: ElemExpr { Var: int t } el } a, ..] } when t < n:
                            tableUse[t] = true;
                            skip.Add(a);
                            skip.Add(el);
                            break;
                        case AssignStmt { Target: VarExpr { Var: int v } target, Value: CallExpr c } when v < n:
                            (writers[v] ??= []).Add(c.Native);
                            skip.Add(target);
                            break;
                        case AssignStmt { Target: VarExpr { Var: int v } target } when v < n:
                            badWrite[v] = true;
                            skip.Add(target);
                            break;
                        case CompoundStmt { Target: VarExpr { Var: int v } } when v < n:
                            badWrite[v] = true;
                            break;
                    }
                });
            }
        }
        string?[] roles = new string?[n];
        for (int i = 0; i < n; i++) {
            if (!candidate[i]) continue;
            if (tableUse[i] && !otherUse[i]) {
                roles[i] = "TriggerEntries";
                continue;
            }
            if (table[i].IsArray || badWrite[i] || writers[i] is not { Count: 1 } w || reads[i] == 0 || tableUse[i]) continue;
            string? native = ScriptDecompiler.Natives[w.First()]?.Name;
            if (native is null) continue;
            string method = native[(native.IndexOf('.', StringComparison.Ordinal) + 1)..];
            if (method.StartsWith("Get", StringComparison.Ordinal) && method.Length > 3 && char.IsAsciiLetterUpper(method[3])) method = method[3..];
            roles[i] = method;
        }
        HashSet<string> taken = new(names, StringComparer.Ordinal);
        for (int i = 0; i < n; i++) {
            if (roles[i] is not string role || reserved.Contains(role)) continue;
            string name = role;
            for (int k = 2; taken.Contains(name) || reserved.Contains(name); k++) name = string.Create(CultureInfo.InvariantCulture, $"{role}_{k}");
            taken.Add(name);
            names[i] = name;
            vars[i] = vars[i] with { Name = name };
        }
    }
}
