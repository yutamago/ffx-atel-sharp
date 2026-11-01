// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Bytecode.Analysis;

public sealed partial class Survey {
    private static string GroupOf(SurveyScript s, WorkerFlow f) => s.Kind == "event" ? $"event type {f.Worker.Type}" : $"{s.Kind} type {f.Worker.Type}";

    /// <summary>Whether an entry's code is only an end (or <c>ret</c>).</summary>
    private static bool IsTrivialEntry(WorkerFlow f, int e) => f.EntryAt[e] is int at and >= 0 && (IsEnd(f.Code[at].Code) || f.Code[at].Code == Opcodes.Ret);

    private void MeasureWorkers() {
        Section("Workers");
        Dictionary<string, List<(SurveyScript S, WorkerFlow F)>> groups = [];
        foreach ((SurveyScript s, WorkerFlow f) in AllFlows()) {
            string g = GroupOf(s, f);
            if (!groups.TryGetValue(g, out var list)) groups[g] = list = [];
            list.Add((s, f));
        }
        Dictionary<ushort, long> allCalls = [];
        long allTotal = 0;
        foreach ((_, WorkerFlow f) in AllFlows()) {
            foreach (Instr i in f.Code) {
                if (i.Code is Opcodes.Func or Opcodes.FuncFacc) {
                    Bump(allCalls, i.Operand!.Value);
                    allTotal++;
                }
            }
        }
        Line("Per worker type, with the natives most typical of it (share of the type's calls against the share of all calls, among natives it calls at least 50 times):");
        Line();
        List<object?[]> rows = [];
        foreach ((string g, List<(SurveyScript S, WorkerFlow F)> list) in groups.OrderBy(k => k.Key, StringComparer.Ordinal)) {
            Dictionary<ushort, long> calls = [];
            long total = 0;
            foreach ((_, WorkerFlow f) in list) {
                foreach (Instr i in f.Code) {
                    if (i.Code is Opcodes.Func or Opcodes.FuncFacc) {
                        Bump(calls, i.Operand!.Value);
                        total++;
                    }
                }
            }
            string typical = string.Join(", ", calls.Where(k => k.Value >= 50)
                .OrderByDescending(k => (double)k.Value / total / ((double)allCalls[k.Key] / allTotal))
                .Take(5).Select(k => $"`{_natives[k.Key]?.Name ?? $"0x{k.Key:x4}"}`"));
            int[] entries = [.. list.Select(x => x.F.Worker.Entries.Length).Order()];
            long trivial = list.Sum(x => Enumerable.Range(0, x.F.EntryAt.Length).Count(e => IsTrivialEntry(x.F, e)));
            long entryCount = entries.Sum();
            string firstTrivial = Pct(list.Count(x => IsTrivialEntry(x.F, 0)), list.Count);
            rows.Add([g, list.Count, list.Select(x => x.S.Name).Distinct().Count(), $"{entries[0]}–{entries[^1]} (median {entries[entries.Length / 2]})", Pct(trivial, entryCount), firstTrivial, typical]);
        }
        Table(["Type", "Workers", "Scripts", "Entries", "entries that only end", "entry 0 only ends", "Typical natives"], rows);

        Sub("Worker order");
        Dictionary<string, long> eventOrders = [];
        long grouped = 0, eventBlocks = 0, functionLast = 0, withFunctions = 0;
        foreach (SurveyScript s in _scripts.Where(s => s.Kind == "event" && s.Block.Workers.Count > 0)) {
            eventBlocks++;
            List<ushort> types = [.. s.Block.Workers.Select(w => w.Type)];
            List<ushort> runs = [.. types.Where((t, k) => k == 0 || types[k - 1] != t)];
            if (runs.Distinct().Count() == runs.Count) grouped++;
            Bump(eventOrders, string.Join(" ", runs));
            if (types.Contains(0)) {
                withFunctions++;
                if (types.SkipWhile(t => t != 0).All(t => t == 0)) functionLast++;
            }
        }
        Line($"In {N(grouped)} of {N(eventBlocks)} event blocks each worker type forms one run; the function workers (type 0) come last in {N(functionLast)} of the {N(withFunctions)} that have any. Type runs, most common:");
        Line();
        Table(["Types in worker order", "Event blocks"], eventOrders.OrderByDescending(k => k.Value).Take(8).Select(k => new object?[] { $"`{k.Key}`", k.Value }));

        Sub("Function workers and calls");
        long calls0 = 0, callsOther = 0;
        Dictionary<int, long> funcEntries = [];
        Dictionary<string, long> funcEnds = [];
        foreach ((SurveyScript s, WorkerFlow f) in AllFlows()) {
            foreach (Instr i in f.Code) {
                if (i.Code != Opcodes.Call) continue;
                int target = i.Operand!.Value;
                if (target < s.Block.Workers.Count && s.Block.Workers[target].Type == 0) calls0++;
                else callsOther++;
            }
            if (f.Worker.Type == 0 && s.Kind == "event") {
                Bump(funcEntries, f.Worker.Entries.Length);
                Bump(funcEnds, f.Code[^1].Opcode.Name);
            }
        }
        Line($"`call`: {N(calls0)} to a type-0 worker, {N(callsOther)} to anything else. Type-0 workers by entry count: "
             + $"{string.Join(", ", funcEntries.OrderBy(k => k.Key).Select(k => $"{k.Key}: {N(k.Value)}"))}; their last instruction: {string.Join(", ", funcEnds.Select(k => $"`{k.Key}` {N(k.Value)}"))}.");
    }

    private void MeasureRequests() {
        Section("Requests");
        Dictionary<(string Op, string Level), long> byLevel = [];
        Dictionary<string, long> workerOperand = [], entryOperand = [], selfByLevel = [], selfFrom = [];
        HashSet<string> levels = [];
        long total = 0, self = 0;
        foreach ((SurveyScript s, WorkerFlow f) in AllFlows()) {
            for (int i = 0; i < f.Code.Count; i++) {
                byte op = f.Code[i].Code;
                if (op is not ((>= Opcodes.Req0 and <= Opcodes.Op3b) or (>= Opcodes.Req1 and <= 0x53))) continue;
                total++;
                int pl = f.SingleOperand(i, 0), pw = f.SingleOperand(i, 1), pe = f.SingleOperand(i, 2);
                int? level = pl >= 0 ? f.IntValue(pl) : null, worker = pw >= 0 ? f.IntValue(pw) : null, entry = pe >= 0 ? f.IntValue(pe) : null;
                string lv = level is int l ? $"{l}" : "computed";
                levels.Add(lv);
                Bump(byLevel, (f.Code[i].Opcode.Name, lv));
                string Kind(int p, int? v) => v is not null ? "constant" : p >= 0 ? $"`{f.Code[p].Opcode.Name}`" : "an expression";
                Bump(workerOperand, worker == f.WorkerIndex ? "constant: the worker itself" : Kind(pw, worker));
                Bump(entryOperand, Kind(pe, entry));
                if (worker == f.WorkerIndex && op is not (Opcodes.Op39 or Opcodes.Op3a or Opcodes.Op3b)) {
                    self++;
                    Bump(selfByLevel, $"`{f.Code[i].Opcode.Name}` at {lv}");
                    int from = f.RegionOf(i);
                    Bump(selfFrom, from switch { 0 => "entry 0", 1 => "entry 1", _ => "other entries" });
                }
            }
        }
        Line($"{N(total)} requests. Operands are pushed level, worker, entry. By opcode and level (the pushed value):");
        Line();
        string[] lvCols = [.. levels.OrderBy(l => int.TryParse(l, out int v) ? v : int.MaxValue)];
        string[] opRows = [.. byLevel.Keys.Select(k => k.Op).Distinct().OrderBy(o => Opcodes.ByName(o)!.Code)];
        Table(["Opcode", .. lvCols], opRows.Select(o => new object?[] { $"`{o}`" }.Concat(lvCols.Select(l => (object?)byLevel.GetValueOrDefault((o, l))))));
        Line();
        Line($"Worker operand: {string.Join(", ", workerOperand.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {N(k.Value)}"))}.");
        Line($"Entry operand: {string.Join(", ", entryOperand.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {N(k.Value)}"))}.");
        Line();
        Line($"Requests to the worker's own entries (a constant worker operand equal to its own index): {N(self)}; "
             + $"{string.Join(", ", selfByLevel.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key} {N(k.Value)}"))}; "
             + $"sent from {string.Join(", ", selfFrom.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {N(k.Value)}"))}.");
    }

    /// <summary>A worker's code with every table reference replaced by what it refers to, so copies in different scripts compare equal.</summary>
    private static string Normalized(SurveyScript s, WorkerFlow f, bool operands) {
        StringBuilder sb = new();
        sb.Append(f.Worker.Type).Append('|');
        foreach (uint e in f.Worker.Entries) sb.Append(e - f.Worker.Start).Append(',');
        sb.Append('|');
        foreach (Instr i in f.Code) {
            sb.Append(i.Code);
            if (i.Operand is ushort v) {
                sb.Append(':');
                switch (i.Opcode.Operand) {
                    case OperandKind.Label:
                        sb.Append(f.Worker.Labels[v] - f.Worker.Start);
                        break;
                    case OperandKind.Func:
                        sb.Append(v);
                        break;
                    case OperandKind.Int when operands:
                        sb.Append(s.Block.IntConst(f.WorkerIndex, v));
                        break;
                    case OperandKind.Float when operands:
                        sb.Append(s.Block.FloatConst(f.WorkerIndex, v) is float x ? BitConverter.SingleToUInt32Bits(x) : 0);
                        break;
                    case OperandKind.Var when operands:
                        sb.Append(s.Block.Variable(f.WorkerIndex, v) is (uint loc, ushort len) ? $"{loc:x}/{len}" : "?");
                        break;
                    default:
                        if (operands) sb.Append(v);
                        break;
                }
            }
            sb.Append(' ');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private void MeasureDuplicates() {
        Section("Duplicate workers");
        Line("Exact: the same type, entries, instructions and labels, with constants and variables compared by value and location. "
             + "Near: the same, ignoring every operand except labels and native IDs (same code shape, other numbers).");
        Line();
        List<object?[]> rows = [];
        foreach (bool operands in new[] { true, false }) {
            Dictionary<string, List<(SurveyScript S, WorkerFlow F)>> groups = [];
            foreach ((SurveyScript s, WorkerFlow f) in AllFlows()) {
                if (f.Code.Count <= 2) continue;
                string h = Normalized(s, f, operands);
                if (!groups.TryGetValue(h, out var list)) groups[h] = list = [];
                list.Add((s, f));
            }
            List<List<(SurveyScript S, WorkerFlow F)>> dup = [.. groups.Values.Where(g => g.Count > 1)];
            long copies = dup.Sum(g => g.Count - 1);
            long workers = groups.Values.Sum(g => g.Count);
            long crossScript = dup.Count(g => g.Select(x => x.S.Name).Distinct().Count() > 1);
            long instrs = dup.Sum(g => (long)(g.Count - 1) * g[0].F.Code.Count);
            var largest = dup.OrderByDescending(g => g.Count).First();
            rows.Add([operands ? "exact" : "near", workers, dup.Count, crossScript, copies, Pct(copies, workers), instrs,
                $"{largest.Count} × {largest[0].F.Code.Count} instructions ({GroupOf(largest[0].S, largest[0].F)}, e.g. {largest[0].S.Name} worker {largest[0].F.WorkerIndex})"]);
        }
        Table(["", "Workers over 2 instructions", "Groups", "… spanning scripts", "Copies (beyond the first of each group)", "Share", "Instructions in copies", "Largest group"], rows);
    }
}
