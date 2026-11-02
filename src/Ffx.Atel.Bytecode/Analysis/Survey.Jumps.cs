// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Bytecode.Analysis;

public sealed partial class Survey {
    private static bool IsCond(byte op) => op is Opcodes.Jz or Opcodes.Jnz or Opcodes.PopAJz or Opcodes.PopAJnz;

    private void MeasureJumps() {
        Section("Jumps and labels");
        Dictionary<string, long> byOp = [];
        long labels = 0, unreferenced = 0, unrefDead = 0, sharedOffset = 0, crossEntry = 0, crossEntryDead = 0, multiEntry = 0;
        long ifOnly = 0, ifElse = 0, workersWithLabels = 0, defOrder = 0, firstSeen = 0, firstRef = 0;
        Dictionary<string, long> plainCondBefore = [], unrefContext = [];
        (SurveyScript, WorkerFlow, int)? crossExample = null, unrefExample = null;
        string crossNote = "";

        foreach ((SurveyScript s, WorkerFlow f) in AllFlows()) {
            int[] refs = new int[f.LabelAt.Length];
            for (int i = 0; i < f.Code.Count; i++) {
                Instr ins = f.Code[i];
                if (!StackEffects.Jumps(ins.Code)) continue;
                int t = f.Target(i);
                refs[ins.Operand!.Value]++;
                string dir = t > i ? "forward" : "backward";
                Bump(byOp, $"`{ins.Opcode.Name}` {dir}");
                if (ins.Code is Opcodes.Jz or Opcodes.Jnz) Bump(plainCondBefore, i > 0 ? f.Code[i - 1].Opcode.Name : "-");
                if (f.RegionOf(t) != f.RegionOf(i)) {
                    if (f.Reachable[i]) {
                        crossEntry++;
                        if (crossExample is null) {
                            crossExample = (s, f, i);
                            crossNote = $"a jump from entry {f.RegionOf(i)}'s code to entry {f.RegionOf(t)}'s";
                        }
                    }
                    else crossEntryDead++;
                }
                // if: a forward conditional jump; with an else when the instruction before its target is a forward jmp.
                if (IsCond(ins.Code) && t > i) {
                    if (t > 0 && f.Code[t - 1].Code == Opcodes.Jmp && f.Target(t - 1) >= t && t - 1 > i) ifElse++;
                    else ifOnly++;
                }
            }
            for (int i = 0; i < f.Code.Count; i++) {
                if (f.EntryReach[i] > 1) multiEntry++;
                if (f.LabelsHere[i].Count > 1) sharedOffset += f.LabelsHere[i].Count - 1;
            }
            labels += f.LabelAt.Length;
            for (int l = 0; l < f.LabelAt.Length; l++) {
                if (refs[l] > 0) continue;
                unreferenced++;
                int at = f.LabelAt[l];
                if (!f.Reachable[at]) unrefDead++;
                string before = at > 0 ? f.Code[at - 1].Opcode.Name : "-";
                Bump(unrefContext, $"after `{before}`, at `{f.Code[at].Opcode.Name}`");
                if (unrefExample is null && before != "jmp") unrefExample = (s, f, at);
            }

            // Label numbers against code order.
            int n = f.LabelAt.Length;
            if (n < 2) continue;
            workersWithLabels++;
            if (Enumerable.Range(0, n).OrderBy(l => f.LabelAt[l]).ThenBy(l => l).Select((l, k) => l == k).All(b => b)) defOrder++;
            List<int> seen = [], referenced = [];
            HashSet<int> seenSet = [], refSet = [];
            for (int i = 0; i < f.Code.Count; i++) {
                foreach (int l in f.LabelsHere[i]) {
                    if (seenSet.Add(l)) seen.Add(l);
                }
                if (StackEffects.Jumps(f.Code[i].Code)) {
                    int l = f.Code[i].Operand!.Value;
                    if (seenSet.Add(l)) seen.Add(l);
                    if (refSet.Add(l)) referenced.Add(l);
                }
            }
            if (seen.Select((l, k) => l == k).All(b => b)) firstSeen++;
            if (referenced.Zip(referenced.Skip(1)).All(p => p.First < p.Second)) firstRef++;
        }

        Table(["Jump", "Count"], byOp.OrderByDescending(k => k.Value).Select(k => new object?[] { k.Key, k.Value }));
        Line();
        Line($"Forward conditional jumps (`if`): {N(ifOnly + ifElse)}; {N(ifElse)} land just after a forward `jmp` (an `if`/`else`), {N(ifOnly)} do not.");
        Line($"`jz`/`jnz` without the `popa` (testing register A as it is): {N(plainCondBefore.Values.Sum())}{(plainCondBefore.Count > 0 ? $"; the instruction before: {Counts(plainCondBefore, 6)}" : "")}.");

        Sub("Labels");
        Line($"{N(labels)} labels; {N(unreferenced)} have no jump to them ({N(unrefDead)} of those in unreachable code); "
             + $"{N(sharedOffset)} share an instruction with another label.");
        if (unrefContext.Count > 0) {
            Line();
            Line("Where the labels without a jump are:");
            Line();
            Table(["Place", "Labels"], unrefContext.OrderByDescending(k => k.Value).Take(8).Select(k => new object?[] { k.Key, k.Value }));
        }
        if (unrefExample is (SurveyScript us, WorkerFlow uf, int ui)) {
            Line();
            Line(Snippet(us, uf, ui - 4, ui + 3, "a label without a jump"));
        }
        Line();
        Line($"Label numbers against code order, over the {N(workersWithLabels)} workers with two or more labels:");
        Line();
        Table(["Label numbers ascend in the order of", "Workers"], [
            ["their place in the code", $"{N(defOrder)} ({Pct(defOrder, workersWithLabels)})"],
            ["first appearance, as a jump operand or a placed label", $"{N(firstSeen)} ({Pct(firstSeen, workersWithLabels)})"],
            ["first jump to them (labels without a jump left out)", $"{N(firstRef)} ({Pct(firstRef, workersWithLabels)})"],
        ]);

        Sub("Entries");
        Line($"Jumps from one entry's code to another's (an entry's code runs from it to the next entry in code order): {N(crossEntry)} in reachable code, {N(crossEntryDead)} in dead code. "
             + $"{N(multiEntry)} instructions are reached from more than one entry.");
        if (crossExample is (SurveyScript cs, WorkerFlow cf, int ci)) {
            Line();
            Line(Snippet(cs, cf, ci - 3, ci + 1, crossNote));
        }

        MeasureDeadCode();
    }

    private void MeasureDeadCode() {
        Sub("Unreachable code");
        Dictionary<string, (long Runs, long Instrs)> kinds = [];
        long total = 0, nops = 0;
        (SurveyScript, WorkerFlow, int, int)? example = null;
        foreach ((SurveyScript s, WorkerFlow f) in AllFlows()) {
            for (int i = 0; i < f.Code.Count; i++) {
                if (f.Reachable[i] || (i > 0 && !f.Reachable[i - 1])) continue;
                int j = i;
                while (j < f.Code.Count && !f.Reachable[j]) j++;
                total += j - i;
                string before = i == 0 ? "the worker's start" : $"`{f.Code[i - 1].Opcode.Name}`";
                string kind;
                if (f.Code.Skip(i).Take(j - i).All(c => c.Code == Opcodes.Nop)) {
                    kind = "only `nop`";
                    nops += j - i;
                }
                else if (i > 0 && f.Code[i - 1].Code == Opcodes.Jmp && f.Target(i - 1) == j) {
                    kind = j - 1 > i && f.Code[j - 1].Code == Opcodes.Jmp && f.Target(j - 1) > j ? "jumped over, ends in a forward `jmp` (`if (0) … else`)" : "jumped over (`if (0)`)";
                    if (example is null && j - i is > 3 and < 9) example = (s, f, i, j);
                }
                else {
                    kind = $"after {before}, then {(j == f.Code.Count ? "the worker's end" : f.EntriesHere[j].Count > 0 ? "an entry" : f.LabelsHere[j].Count > 0 ? "a label" : "more code")}";
                }
                (long r, long n) = kinds.GetValueOrDefault(kind);
                kinds[kind] = (r + 1, n + j - i);
            }
        }
        Line($"{N(total)} instructions no entry reaches.");
        Line();
        Table(["Shape", "Runs", "Instructions"], kinds.OrderByDescending(k => k.Value.Instrs).Take(12).Select(k => new object?[] { k.Key, k.Value.Runs, k.Value.Instrs }));
        if (example is (SurveyScript es, WorkerFlow ef, int ei, int ej)) {
            Line();
            Line(Snippet(es, ef, ei - 2, ej + 1, "code jumped over"));
        }
    }
}
