// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Bytecode.Analysis;

public sealed partial class Survey {
    /// <summary>
    ///     A <c>switch</c>: <c>popb</c>, <c>jmp chain</c>, the case bodies, then the compare chain, which ends in
    ///     <c>jmp default</c> or falls through to the break label (<paramref name="FinalLabel"/> -1).
    /// </summary>
    private sealed record SwitchShape(int PopB, int Chain, int ChainEnd, List<(int Value, int Label, string Shape)> Cases, int FinalLabel, int BreakLabel, List<int> SkipLabels, List<int> CaseJumps);

    private static bool IsPushConst(Instr i) => i.Code is Opcodes.PushImm or Opcodes.PushC;

    private static bool IsCompare(byte op) => op is >= Opcodes.Eq and <= Opcodes.Le;

    /// <summary>
    ///     The compare chain at instruction <paramref name="at"/>: one compare per case
    ///     (<c>push k; pushb; eq; popa_jnz case</c>, other compares, or a range
    ///     <c>pushb; push lo; ge; popa_jz skip; pushb; push hi; le; popa_jnz case; skip:</c>).
    /// </summary>
    private static (List<(int Value, int Label, string Shape)> Cases, int End, List<int> Skips, List<int> Jumps) ReadChain(WorkerFlow f, int at) {
        List<(int, int, string)> cases = [];
        List<int> skips = [], jumps = [];
        int i = at;
        int? pendingSkip = null;
        while (i + 3 < f.Code.Count) {
            if (i > at && f.LabelsHere[i].Count > 0 && !(pendingSkip is int ps && f.LabelsHere[i].Contains(ps))) break;
            pendingSkip = null;
            Instr a = f.Code[i], b = f.Code[i + 1], c = f.Code[i + 2], d = f.Code[i + 3];
            bool valueFirst = b.Code == Opcodes.PushB && IsPushConst(a);
            bool bFirst = a.Code == Opcodes.PushB && IsPushConst(b);
            if (!(valueFirst || bFirst) || !IsCompare(c.Code)) break;
            int value = f.IntValue(valueFirst ? i : i + 1) ?? 0;
            string order = valueFirst ? $"{a.Opcode.Name}; pushb" : $"pushb; {b.Opcode.Name}";
            if (d.Code == Opcodes.PopAJnz) {
                cases.Add((value, d.Operand!.Value, $"`{order}; {c.Opcode.Name}; popa_jnz case`"));
                jumps.Add(i + 3);
                i += 4;
                continue;
            }
            // A range: the low compare skips to the next element unless it holds, then the high one.
            if (d.Code == Opcodes.PopAJz && i + 7 < f.Code.Count && f.Target(i + 3) == i + 8
                && f.Code[i + 4].Code == Opcodes.PushB && IsPushConst(f.Code[i + 5]) && IsCompare(f.Code[i + 6].Code) && f.Code[i + 7].Code == Opcodes.PopAJnz) {
                cases.Add((value, f.Code[i + 7].Operand!.Value, $"`pushb; push lo; {c.Opcode.Name}; popa_jz next; pushb; push hi; {f.Code[i + 6].Opcode.Name}; popa_jnz case` (range)"));
                skips.Add(d.Operand!.Value);
                jumps.Add(i + 7);
                pendingSkip = d.Operand!.Value;
                i += 8;
                continue;
            }
            break;
        }
        return (cases, i, skips, jumps);
    }

    private List<(SurveyScript Script, WorkerFlow Flow, SwitchShape Shape)>? _switches;

    private List<(SurveyScript Script, WorkerFlow Flow, SwitchShape Shape)> FindSwitches() {
        if (_switches is not null) return _switches;
        _switches = [];
        foreach ((SurveyScript s, WorkerFlow f) in AllFlows()) {
            for (int i = 0; i + 1 < f.Code.Count; i++) {
                if (f.Code[i].Code != Opcodes.PopB || f.Code[i + 1].Code != Opcodes.Jmp) continue;
                int chain = f.Target(i + 1);
                if (chain <= i) continue;
                (List<(int, int, string)> cases, int end, List<int> skips, List<int> jumps) = ReadChain(f, chain);
                int final = -1, breakAt = end;
                if (end < f.Code.Count && f.Code[end].Code == Opcodes.Jmp && f.LabelsHere[end].All(skips.Contains)) {
                    final = f.Code[end].Operand!.Value;
                    breakAt = end + 1;
                }
                if (cases.Count == 0 && final < 0) continue;
                int breakLabel = breakAt < f.Code.Count && f.LabelsHere[breakAt].Count > 0 ? f.LabelsHere[breakAt].Max() : -1;
                _switches.Add((s, f, new SwitchShape(i, chain, end, cases, final, breakLabel, skips, jumps)));
            }
        }
        return _switches;
    }

    private void MeasureSwitches() {
        Section("Switch");
        List<(SurveyScript Script, WorkerFlow Flow, SwitchShape Shape)> all = FindSwitches();
        long popbs = AllFlows().Sum(x => x.Flow.Code.Count(c => c.Code == Opcodes.PopB));
        long pushbs = AllFlows().Sum(x => x.Flow.Code.Count(c => c.Code == Opcodes.PushB));
        long chainPushb = all.Sum(x => x.Shape.Cases.Count);
        long withDefault = 0, bodiesInChainOrder = 0, fallsToBreak = 0, rangeSkipsAfter = 0, plainBodies = 0, plainRule = 0, emptyDefault = 0, defaultIsBreak = 0, noBreakLabel = 0, emptyChain = 0;
        long numberedChainBreak = 0, casesInOrder = 0, defaultAfterCases = 0, sharedTargets = 0, caseBodiesFallThrough = 0, caseBodies = 0;
        long bodiesBeforeChain = 0;
        Dictionary<string, long> shapes = [];
        Dictionary<int, long> caseCounts = [];
        (SurveyScript, WorkerFlow, SwitchShape)? noDefaultExample = null;
        foreach ((SurveyScript s, WorkerFlow f, SwitchShape sw) in all) {
            foreach ((_, _, string shape) in sw.Cases) Bump(shapes, shape);
            Bump(caseCounts, Math.Min(sw.Cases.Count, 10));
            if (sw.Cases.Count == 0) emptyChain++;
            int brk = sw.BreakLabel;
            if (brk < 0) noBreakLabel++;
            if (sw.FinalLabel < 0) {
                fallsToBreak++;
                noDefaultExample ??= (s, f, sw);
            }
            else if (sw.FinalLabel == brk) defaultIsBreak++;
            else {
                withDefault++;
                int d = f.LabelAt[sw.FinalLabel];
                if (f.Code[d].Code == Opcodes.Jmp && f.Code[d].Operand == brk) emptyDefault++;
            }
            int chainLabel = f.Code[sw.PopB + 1].Operand!.Value;
            if (brk == chainLabel + 1) numberedChainBreak++;
            // Case labels numbered from chain + 2 in chain order, the default after them.
            List<int> caseLabels = [.. sw.Cases.Select(c => c.Label).Distinct()];
            sharedTargets += sw.Cases.Count - caseLabels.Count;
            bool rule = brk == chainLabel + 1 && caseLabels.Select((l, k) => l == chainLabel + 2 + k).All(b => b);
            if (caseLabels.Select((l, k) => l == chainLabel + 2 + k).All(b => b)) casesInOrder++;
            // Bodies without labels of their own: no nested construct numbers labels between the cases.
            HashSet<int> own = [chainLabel, .. caseLabels, .. sw.SkipLabels];
            if (brk >= 0) own.Add(brk);
            if (sw.FinalLabel >= 0) own.Add(sw.FinalLabel);
            bool plain = Enumerable.Range(sw.PopB, sw.ChainEnd - sw.PopB).All(i => f.LabelsHere[i].All(own.Contains));
            if (plain) {
                plainBodies++;
                if (rule && (sw.FinalLabel < 0 || sw.FinalLabel == brk || sw.FinalLabel == chainLabel + 2 + caseLabels.Count)) plainRule++;
            }
            if (sw.FinalLabel >= 0 && sw.FinalLabel != brk && sw.FinalLabel == chainLabel + 2 + caseLabels.Count) defaultAfterCases++;
            int lastNumbered = chainLabel + 1 + caseLabels.Count + (sw.FinalLabel >= 0 && sw.FinalLabel != brk ? 1 : 0);
            if (sw.SkipLabels.Count > 0 && sw.SkipLabels.Select((l, k) => l == lastNumbered + 1 + k).All(b => b)) rangeSkipsAfter++;
            // Case bodies lie between the jmp and the chain, in label order.
            if (sw.Cases.All(c => f.LabelAt[c.Label] > sw.PopB && f.LabelAt[c.Label] < sw.Chain)) bodiesBeforeChain++;
            List<int> starts = [.. caseLabels.Select(l => f.LabelAt[l])];
            if (starts.Zip(starts.Skip(1)).All(p => p.First < p.Second)
                && (sw.FinalLabel < 0 || sw.FinalLabel == brk || f.LabelAt[sw.FinalLabel] > starts.DefaultIfEmpty(-1).Max())) bodiesInChainOrder++;
            List<int> bodyStarts = [.. caseLabels.Append(sw.FinalLabel).Where(l => l >= 0 && l != brk).Select(l => f.LabelAt[l]).Distinct().Order()];
            for (int k = 0; k < bodyStarts.Count; k++) {
                int next = k + 1 < bodyStarts.Count ? bodyStarts[k + 1] : sw.Chain;
                caseBodies++;
                if (next - 1 >= 0 && !(f.Code[next - 1].Code == Opcodes.Jmp)) caseBodiesFallThrough++;
            }
        }
        Line($"{N(all.Count)} switches: `popb` then `jmp chain`; the case bodies; at `chain` one compare per case, then either a final `jmp default` "
             + $"or nothing, the chain falling through to the break label. {N(popbs)} `popb` in all, {N(pushbs)} `pushb`, {N(chainPushb)} of them in compare chains.");
        Line();
        Table(["", "Switches"], [
            ["no final `jmp`: the chain falls through to the break label (no `default`)", fallsToBreak],
            ["final `jmp` to the break label", defaultIsBreak],
            ["final `jmp` elsewhere (`default`)", withDefault],
            ["… of which the default body is only `jmp break` (`default: break;`)", emptyDefault],
            ["no label after the chain (nothing breaks, or the switch ends the entry)", noBreakLabel],
            ["no case at all (only the final `jmp`)", emptyChain],
            ["with range cases", all.Count(x => x.Shape.SkipLabels.Count > 0)],
            ["… whose skip labels are numbered after the case and default labels, in chain order", rangeSkipsAfter],
            ["break label = chain label + 1", numberedChainBreak],
            ["case labels = chain + 2, + 3, … in chain order", casesInOrder],
            ["default label right after the case labels", defaultAfterCases],
            ["switches whose bodies place no labels but the switch's own", plainBodies],
            ["… numbered chain, break, cases in chain order, default", plainRule],
            ["every case body between the `jmp chain` and the chain", bodiesBeforeChain],
            ["case bodies in chain order, the default body last", bodiesInChainOrder],
            ["cases sharing a body (`case 1: case 2:`)", sharedTargets],
        ]);
        Line();
        Line($"{N(popbs - all.Count)} `popb` are not a switch. Labels: chain, break, then a case label per case as the chain lists them, then the default; range skip labels come later.");
        Line();
        Line($"Case bodies: {N(caseBodies)}, of which {N(caseBodiesFallThrough)} do not end in a `jmp` (fall through into the next body, or end the entry).");
        Line();
        Table(["Compare", "Cases"], shapes.OrderByDescending(k => k.Value).Select(k => new object?[] { k.Key, k.Value }));
        Line();
        Table(["Cases", "Switches"], caseCounts.OrderBy(k => k.Key).Select(k => new object?[] { k.Key == 10 ? "10 or more" : k.Key, k.Value }));
        if (noDefaultExample is (SurveyScript es, WorkerFlow ef, SwitchShape esw) && esw.ChainEnd - esw.PopB < 30) {
            Line();
            Line(Snippet(es, ef, esw.PopB - 1, esw.ChainEnd + 1, "a switch without default"));
        }
    }

    private void MeasureLoops() {
        Section("Loops");
        HashSet<(WorkerFlow, int)> caseJumps = [], defaultJumps = [], caseLabelSet = [];
        foreach ((_, WorkerFlow f, SwitchShape sw) in FindSwitches()) {
            foreach (int j in sw.CaseJumps) caseJumps.Add((f, j));
            if (sw.FinalLabel >= 0) defaultJumps.Add((f, sw.ChainEnd));
            foreach ((_, int label, _) in sw.Cases) caseLabelSet.Add((f, label));
            if (sw.FinalLabel >= 0) caseLabelSet.Add((f, sw.FinalLabel));
        }
        long whileLoops = 0, whileExitNumbered = 0, infinite = 0, doWhile = 0, breaks = 0, continues = 0, otherBack = 0, forContinue = 0;
        long condBackNotCase = 0, condBackCase = 0, defaultBack = 0, toCase = 0, infiniteBreak = 0, infiniteEnd = 0, infiniteShared = 0;
        (SurveyScript, WorkerFlow, int, int)? gotoCaseExample = null, gotoExample = null;
        (SurveyScript, WorkerFlow, int, int)? whileExample = null, infiniteExample = null, doExample = null;
        foreach ((SurveyScript s, WorkerFlow f) in AllFlows()) {
            for (int i = 0; i < f.Code.Count; i++) {
                Instr ins = f.Code[i];
                if (!StackEffects.Jumps(ins.Code)) continue;
                int top = f.Target(i);
                if (top > i) continue;
                if (ins.Code == Opcodes.PopAJnz) {
                    if (caseJumps.Contains((f, i))) {
                        condBackCase++;
                        continue;
                    }
                    condBackNotCase++;
                    doWhile++;
                    if (doExample is null && i - top < 12) doExample = (s, f, top, i);
                    continue;
                }
                if (ins.Code != Opcodes.Jmp) {
                    otherBack++;
                    continue;
                }
                if (defaultJumps.Contains((f, i))) {
                    defaultBack++;
                    continue;
                }
                if (caseLabelSet.Contains((f, ins.Operand!.Value))) {
                    toCase++;
                    gotoCaseExample ??= (s, f, top, i);
                    continue;
                }
                int topLabel = ins.Operand!.Value;
                // The exit: a forward conditional jump between top and here to just after this jmp.
                int exitAt = -1;
                for (int j = top; j < i; j++) {
                    if (f.Code[j].Code == Opcodes.PopAJz && f.Target(j) == i + 1) {
                        exitAt = j;
                        break;
                    }
                }
                bool firstCondIsExit = false;
                for (int j = top; j < i; j++) {
                    if (IsCond(f.Code[j].Code) || f.Code[j].Code == Opcodes.Jmp) {
                        firstCondIsExit = j == exitAt;
                        break;
                    }
                }
                if (exitAt >= 0 && firstCondIsExit) {
                    whileLoops++;
                    if (f.Code[exitAt].Operand == topLabel - 1) whileExitNumbered++;
                    if (whileExample is null && i - top < 14) whileExample = (s, f, top, i + 1);
                }
                else {
                    infinite++;
                    bool hasBreak = false, hasEnd = false;
                    for (int j = top; j < i; j++) {
                        byte o = f.Code[j].Code;
                        if (StackEffects.Jumps(o) && f.Target(j) > i) hasBreak = true;
                        if (o is Opcodes.End or Opcodes.EndP or Opcodes.End1 or Opcodes.EndP1 or Opcodes.Op54 or Opcodes.Ret) hasEnd = true;
                    }
                    if (hasBreak) infiniteBreak++;
                    else if (hasEnd) infiniteEnd++;
                    if (f.LabelsHere[top].Count > 1) {
                        infiniteShared++;
                        gotoExample ??= (s, f, top, i);
                    }
                    if (infiniteExample is null && hasBreak && f.LabelsHere[top].Count == 1 && i - top is > 4 and < 16) infiniteExample = (s, f, top, i + 1);
                }
                // break and continue: jumps out of / back to the head from inside the body.
                int exit = i + 1 < f.Code.Count ? i + 1 : -1;
                for (int j = top; j < i; j++) {
                    if (f.Code[j].Code != Opcodes.Jmp) continue;
                    int t = f.Target(j);
                    if (t == exit) {
                        breaks++;
                    }
                    else if (t == top) continues++;
                    else if (t > j && t < i && t > top && f.Code.Skip(t).Take(i - t).All(c => !IsCond(c.Code) && !(c.Code == Opcodes.Jmp))) forContinue++;
                }
            }
        }
        Line("Backward jumps are loops or switch cases: the compare chain comes after the bodies, so every case jump goes back.");
        Line();
        Table(["Backward jump", "Count"], [
            ["`popa_jnz` from a switch's compare chain", condBackCase],
            ["`popa_jnz` elsewhere (`do … while`)", condBackNotCase],
            ["`jmp default` at the end of a switch's chain (the default body comes before the chain)", defaultBack],
            ["`jmp` to a case or default label (`goto case`, or a case reusing another's tail)", toCase],
            ["`jmp` with a `popa_jz` exit to just after it as the loop's first jump (`while`, `for`)", whileLoops],
            ["… whose exit label is the head label − 1", whileExitNumbered],
            ["`jmp` without such an exit (`while (1)`, or a condition that does not lead)", infinite],
            ["… with a jump out past the loop inside", infiniteBreak],
            ["… without one, but with an end or `ret` inside", infiniteEnd],
            ["… whose head label shares its instruction with another label", infiniteShared],
            ["other", otherBack],
        ]);
        Line();
        Line("A backward `jmp` without a leading exit is `while (1)` with a `break`, a loop the entry ends in, or a `goto` back (labels the source named: "
             + "a second label at the same place, as `l24: l25:` below, where one comes from a construct and the other is jumped to from elsewhere).");
        if (gotoExample is (SurveyScript xs, WorkerFlow xf, int xt, int xi)) {
            Line();
            Line(Snippet(xs, xf, xt - 1, xt + 1, "a backward jump to a label that shares its place"));
            Line();
            Line(Snippet(xs, xf, xi - 2, xi, "… and the jump"));
        }
        Line();
        Line($"Inside those `jmp` loops: {N(breaks)} `jmp` to just after the loop (`break`), {N(continues)} back to the head (`continue`), "
             + $"{N(forContinue)} forward to a label from which the code runs straight to the backward `jmp` (`continue` in a `for`, to the step).");
        if (whileExample is (SurveyScript ws, WorkerFlow wf, int wt, int wi)) {
            Line();
            Line(Snippet(ws, wf, wt - 1, wi, "a while loop"));
        }
        if (gotoCaseExample is (SurveyScript gs, WorkerFlow gf, int gt, int gi)) {
            Line();
            Line(Snippet(gs, gf, gt - 1, gt + 2, "the case label"));
            Line();
            Line(Snippet(gs, gf, gi - 3, gi + 1, "the jump back to it, from a later case body"));
        }
        if (infiniteExample is (SurveyScript fs, WorkerFlow ff, int ft, int fi)) {
            Line();
            Line(Snippet(fs, ff, ft - 1, fi, "a loop without a leading exit, left by a jump out"));
        }
        if (doExample is (SurveyScript ds, WorkerFlow df, int dt, int di)) {
            Line();
            Line(Snippet(ds, df, dt - 1, di + 1, "a do-while loop"));
        }
    }
}
