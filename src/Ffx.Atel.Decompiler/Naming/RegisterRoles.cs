// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;

namespace Ffx.Atel.Decompiler.Naming;

/// <summary>
///     Names for a worker's registers (<c>[Register(1)] public static int I1;</c>, docs/source-repo.md, "Names") by
///     what the worker's code stores in them: a register every store to which, in every method of the worker
///     (inline methods' code included), is the result of one native, and which is read, is named after the native, a getter,
///     without <c>Get</c> (<c>I0 = Btl.GetTurnActor();</c> → <c>TurnActor</c>), as script variables are
///     (<see cref="VariableRoles"/>). The compiler places a register by its <c>[Register(n)]</c>, so the name is
///     only a name. A name the worker, its script or the reference assembly has is not taken; two registers of
///     one worker with the same name keep their numbers.
/// </summary>
internal static class RegisterRoles {
    public static void Assign(WorkerDecompiler[] workers, ISet<string> reserved) {
        foreach (WorkerDecompiler worker in workers) {
            Dictionary<(RegKind, int), HashSet<ushort>> writers = [];
            HashSet<(RegKind, int)> bad = [];
            Dictionary<(RegKind, int), int> reads = [];
            HashSet<Expr> targets = new(ReferenceEqualityComparer.Instance);
            foreach (StructuredMethod m in worker.Bodies) {
                Inline.Walk(m.Body, e => {
                    if (e is RegExpr { Kind: RegKind.Int or RegKind.Float } r && !targets.Contains(e)) reads[(r.Kind, r.Number)] = reads.GetValueOrDefault((r.Kind, r.Number)) + 1;
                }, s => {
                    switch (s) {
                        case AssignStmt { Target: RegExpr { Kind: RegKind.Int or RegKind.Float } r, Value: CallExpr c } a when !a.Clamped:
                            (writers.TryGetValue((r.Kind, r.Number), out HashSet<ushort>? w) ? w : writers[(r.Kind, r.Number)] = []).Add(c.Native);
                            targets.Add(a.Target);
                            break;
                        case AssignStmt { Target: RegExpr { Kind: RegKind.Int or RegKind.Float } r } a:
                            bad.Add((r.Kind, r.Number));
                            targets.Add(a.Target);
                            break;
                        case CompoundStmt { Target: RegExpr { Kind: RegKind.Int or RegKind.Float } r }:
                            bad.Add((r.Kind, r.Number));
                            break;
                    }
                });
            }
            HashSet<string> used = new(worker.EntryNames.Values, StringComparer.Ordinal);
            used.UnionWith(worker.OwnMethodNames);
            Dictionary<(RegKind, int), string> roles = [];
            foreach (((RegKind, int) reg, HashSet<ushort> natives) in writers) {
                if (bad.Contains(reg) || natives.Count != 1 || reads.GetValueOrDefault(reg) == 0) continue;
                if (ScriptDecompiler.Natives[natives.First()]?.Name is not string native) continue;
                // Only a getter names what it holds (GetTurnKind → TurnKind; ActorGetMotionInfo → ActorMotionInfo); an
                // action's result (ActorPlayMotion) does not.
                string method = native[(native.IndexOf('.', StringComparison.Ordinal) + 1)..];
                int get = method.IndexOf("Get", StringComparison.Ordinal);
                if (get < 0 || method.Length == get + 3 || !char.IsAsciiLetterUpper(method[get + 3])) continue;
                // A getter named after its address or offset (GetField44f) says nothing about the value.
                if (method.Any(char.IsAsciiDigit)) continue;
                roles[reg] = method[..get] + method[(get + 3)..];
            }
            foreach (IGrouping<string, (RegKind, int)> g in roles.Keys.GroupBy(k => roles[k])) {
                if (g.Count() != 1 || reserved.Contains(g.Key) || used.Contains(g.Key) || used.Contains(g.Key + "Async")) continue;
                worker.RegisterNames[g.First()] = g.Key;
                used.Add(g.Key);
            }
        }
    }
}
