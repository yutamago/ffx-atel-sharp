// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler.Naming;

/// <summary>
///     Which entries are declared <c>async Task</c> (docs/plan.md, "Requests between workers"): every entry
///     another worker of the script requests by constants (<c>req0</c>, <c>reqw0</c>, <c>reqe0</c> with a
///     constant level, worker and entry), where the entry is the first of a method of its own. Those requests
///     are then written as calls of the method (<c>_ = Lulu.E5Async();</c>,
///     <c>await Lulu.E5Async();</c>, <c>await Lulu.E5Async().Started;</c>), the rest keep the fallback form.
/// </summary>
internal static class RequestNaming {
    /// <summary>Decides the <c>async Task</c> entries of every worker and tells each worker's printer the methods requests call.</summary>
    public static void Assign(WorkerDecompiler[] workers, string[] names) {
        HashSet<int>[] heads = [.. workers.Select(w => w.MethodEntries().ToHashSet())];
        for (int w = 0; w < workers.Length; w++) {
            foreach (RequestStmt r in workers[w].RequestsMade()) {
                if (r.Op is not (Opcodes.Req0 or Opcodes.ReqW0 or Opcodes.ReqE0)) continue;
                if (r is not { Level: IntLit { Value: >= 0 and <= 8 } level, Worker: IntLit { Value: int t }, Entry: IntLit { Value: int e } }) continue;
                if (t == w || t < 0 || t >= workers.Length || !heads[t].Contains(e)) continue;
                workers[t].AsyncEntries.Add(e);
                if (level.Value != StatementPrinter.DefaultPriority) workers[t].PriorityEntries.Add(e);
            }
        }
        string? Target(int t, int e) =>
            t >= 0 && t < workers.Length && workers[t].AsyncEntries.Contains(e) ? $"{names[t]}.{WorkerDecompiler.AsyncName(e)}" : null;
        HashSet<int> functions = [.. Enumerable.Range(0, workers.Length).Where(w => workers[w].IsRunFunction)];
        foreach (WorkerDecompiler w in workers) {
            w.RequestTarget = Target;
            w.RunFunctions = functions;
        }
    }
}
