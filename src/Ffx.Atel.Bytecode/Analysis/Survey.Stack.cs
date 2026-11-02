// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Bytecode.Analysis;

public sealed partial class Survey {
    /// <summary>Per native: call sites, and those where the stack does not fit its argument count.</summary>
    private sealed class NativeStackStats {
        public long Sites, Underflow, Leftover, LeftoverAlone;
        public Dictionary<int, long> DepthBeforeFacc = [];
    }

    private void MeasureStack() {
        Section("Stack");
        long reached = 0, unreached = 0, labels = 0;
        Dictionary<int, long> atLabels = [], atJumps = [], atEnds = [];
        Dictionary<string, long> nonEmptyJumps = [];
        Dictionary<StackIssueKind, long> issues = [];
        Dictionary<StackIssueKind, HashSet<string>> issueScripts = [];
        Dictionary<ushort, NativeStackStats> perNative = [];
        (SurveyScript, WorkerFlow, int)? ternaryExample = null, mergeExample = null;
        long workersClean = 0, workers = 0;

        foreach ((SurveyScript s, WorkerFlow f) in AllFlows()) {
            workers++;
            HashSet<(StackIssueKind, int)> seen = [];
            foreach (StackIssue issue in f.Issues) {
                if (!seen.Add((issue.Kind, issue.Index))) continue;
                issues[issue.Kind] = issues.GetValueOrDefault(issue.Kind) + 1;
                if (!issueScripts.TryGetValue(issue.Kind, out HashSet<string>? set)) issueScripts[issue.Kind] = set = [];
                set.Add(s.Name);
                if (issue.Kind == StackIssueKind.Merge && mergeExample is null) mergeExample = (s, f, issue.Index);
            }
            if (seen.Count == 0) workersClean++;
            for (int i = 0; i < f.Code.Count; i++) {
                Instr ins = f.Code[i];
                if (!f.Reachable[i]) unreached++;
                else reached++;
                int d = f.Depth[i];
                if (f.LabelsHere[i].Count > 0) {
                    labels++;
                    Bump(atLabels, d);
                }
                byte op = ins.Code;
                if (StackEffects.Jumps(op)) {
                    int carried = d - StackEffects.Of(op).Pops;
                    Bump(atJumps, carried);
                    if (carried != 0) {
                        nonEmptyJumps[ins.Opcode.Name] = nonEmptyJumps.GetValueOrDefault(ins.Opcode.Name) + 1;
                        ternaryExample ??= (s, f, i);
                    }
                }
                if (op is Opcodes.End or Opcodes.End1 or Opcodes.EndP or Opcodes.EndP1 or Opcodes.Op54 or Opcodes.Ret) {
                    Bump(atEnds, op switch { Opcodes.EndP or Opcodes.EndP1 => d - 1, _ => d });
                }
                if (op is Opcodes.Func or Opcodes.FuncFacc) {
                    ushort id = ins.Operand!.Value;
                    if (!perNative.TryGetValue(id, out NativeStackStats? st)) perNative[id] = st = new NativeStackStats();
                    st.Sites++;
                    if (op == Opcodes.FuncFacc) Bump(st.DepthBeforeFacc, d);
                }
            }
            foreach (StackIssue issue in f.Issues) {
                Instr ins = f.Code[issue.Index];
                if (ins.Code is not (Opcodes.Func or Opcodes.FuncFacc)) continue;
                NativeStackStats st = perNative[ins.Operand!.Value];
                if (issue.Kind == StackIssueKind.Underflow) st.Underflow++;
                if (issue.Kind == StackIssueKind.Leftover) {
                    st.Leftover++;
                    // The statement's other natives: back to the last empty stack.
                    bool alone = true;
                    for (int j = issue.Index - 1; j >= 0 && f.Depth[j] > 0; j--) {
                        if (f.Code[j].Code == Opcodes.Func) alone = false;
                    }
                    if (alone) st.LeftoverAlone++;
                }
            }
        }

        Line($"The stack simulation (`WorkerFlow`) runs every entry from an empty stack, with the argument counts of `data/natives.json`, "
             + $"then every run of code no entry reaches, also from an empty stack. {N(reached)} instructions are reached from an entry, {N(unreached)} are not (see \"Unreachable code\").");
        Line();
        Line("Issues (underflow: popping more than is there; merge: two depths at one instruction; leftover: a call statement leaving values):");
        Line();
        Table(["Issue", "Instructions", "Scripts"], issues.OrderBy(k => k.Key).Select(k => new object?[] { k.Key, k.Value, issueScripts[k.Key].Count }));
        Line();
        Line($"{N(workersClean)} of {N(workers)} workers simulate without any issue.");

        Sub("Depth at labels and jumps");
        Table(["Depth", "at a label", "carried by a jump", "left at an end (after `endp`'s pop)"],
            atLabels.Keys.Union(atJumps.Keys).Union(atEnds.Keys).Order().Select(d => new object?[] { d, atLabels.GetValueOrDefault(d), atJumps.GetValueOrDefault(d), atEnds.GetValueOrDefault(d) }));
        Line();
        Line($"Non-empty stack across a jump, by opcode: {Counts(nonEmptyJumps)}.{(nonEmptyJumps.Count == 0 ? " No expression spans a branch: no `?:` and no short-circuit `&&`/`||` inside an expression." : "")}");
        if (ternaryExample is (SurveyScript ts, WorkerFlow tf, int ti)) {
            Line();
            Line(Snippet(ts, tf, ti - 6, ti + 6, "the depth before each instruction in brackets"));
        }
        if (mergeExample is (SurveyScript ms, WorkerFlow mf, int mi)) {
            Line();
            Line("First merge with two depths:");
            Line();
            Line(Snippet(ms, mf, mi - 8, mi + 2));
        }

        Sub("Native argument counts against the stack");
        long sites = perNative.Values.Sum(v => v.Sites);
        Line($"{N(perNative.Count)} natives are called, at {N(sites)} sites. A site underflows when its arguments are not on the stack; "
             + "a `func_facc` leaves values when the statement pushed more than the count takes (\"alone\": no other native in the statement to blame).");
        Line();
        List<(ushort Id, NativeStackStats St)> bad = [.. perNative
            .Where(k => k.Value.Underflow + k.Value.LeftoverAlone > 0)
            .OrderByDescending(k => (double)(k.Value.Underflow + k.Value.LeftoverAlone) / k.Value.Sites)
            .Select(k => (k.Key, k.Value))];
        Line($"{bad.Count} natives have such sites:");
        Line();
        Table(["Native", "Name", "argc", "source", "sites", "underflow", "leftover (alone)", "depth before `func_facc`"],
            bad.Select(b => {
                NativeSignature? sig = _natives[b.Id];
                return new object?[] {
                    $"`0x{b.Id:x4}`", sig?.Name, sig?.Argc, sig?.ArgcSource, b.St.Sites, b.St.Underflow, $"{N(b.St.Leftover)} ({N(b.St.LeftoverAlone)})",
                    string.Join(", ", b.St.DepthBeforeFacc.OrderByDescending(k => k.Value).Take(4).Select(k => $"{k.Key}: {N(k.Value)}")),
                };
            }));
    }

    private static void Bump<TKey>(Dictionary<TKey, long> d, TKey k, long by = 1) where TKey : notnull => d[k] = d.GetValueOrDefault(k) + by;
}
