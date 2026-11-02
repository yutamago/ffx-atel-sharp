// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Bytecode.Analysis;

public sealed partial class Survey {
    private static readonly string[] TypeNames = ["u8", "i8", "u16", "i16", "u32", "i32", "f32", "t7"];

    private static string VarType(ScriptBlock b, int w, ushort v) => b.Variable(w, v) is (uint loc, _) ? TypeNames[(int)(loc >> 28) & 7] : "?";

    private static bool IsEnd(byte op) => op is Opcodes.End or Opcodes.EndP or Opcodes.End1 or Opcodes.EndP1 or Opcodes.Op54;

    private void MeasureEncoding() {
        Section("Encoding choices");

        // Integer pushes.
        long imm = 0, poolInRange = 0, poolOutOfRange = 0;
        Dictionary<string, long> poolInRangeUse = [];
        (SurveyScript, WorkerFlow, int)? poolInRangeExample = null;
        // Floats.
        long fc = 0, fcIntegral = 0, floatArgInt = 0, floatArgFloat = 0, intArgFloat = 0, intArgInt = 0;
        // Natives.
        long funcs = 0, faccs = 0, pushfacc = 0, popfacc = 0, pollingFunc = 0, pollingFacc = 0, faccThenPush = 0, faccThenPushPolling = 0;
        Dictionary<string, long> funcConsumers = [], popfaccProducers = [], pushfaccBefore = [];
        HashSet<ushort> viaFunc = [], viaFacc = [];
        // Stores.
        Dictionary<(string Op, string Type), long> stores = [];
        // Ends.
        Dictionary<(string Kind, string Op), long> ends = [];
        Dictionary<string, long> endpValues = [], endsAtEntryEnd = [];
        // Conditions.
        Dictionary<string, long> conditionLast = [], ops = [];
        long nops = 0;
        Dictionary<string, long> nopPlaces = [];

        foreach ((SurveyScript s, WorkerFlow f) in AllFlows()) {
            int[] consumer = f.Consumer;
            for (int i = 0; i < f.Code.Count; i++) {
                Instr ins = f.Code[i];
                byte op = ins.Code;
                Bump(ops, ins.Opcode.Name);
                switch (op) {
                    case Opcodes.PushImm:
                        imm++;
                        break;
                    case Opcodes.PushC:
                        int c = f.IntValue(i) ?? 0;
                        if (c is >= short.MinValue and <= short.MaxValue) {
                            poolInRange++;
                            int u = consumer[i];
                            Bump(poolInRangeUse, u >= 0 ? f.Code[u].Opcode.Name : "-");
                            poolInRangeExample ??= (s, f, i);
                        }
                        else poolOutOfRange++;
                        break;
                    case Opcodes.PushFC:
                        fc++;
                        if (s.Block.FloatConst(f.WorkerIndex, ins.Operand!.Value) is float v && v == MathF.Round(v)) fcIntegral++;
                        break;
                    case Opcodes.Func or Opcodes.FuncFacc:
                        ushort id = ins.Operand!.Value;
                        bool polls = _natives[id]?.Polls == true;
                        if (op == Opcodes.Func) {
                            if (polls) pollingFunc++;
                            funcs++;
                            viaFunc.Add(id);
                            int u = consumer[i];
                            Bump(funcConsumers, u < 0 ? "-" : f.Code[u].Code is Opcodes.Func or Opcodes.FuncFacc ? "a native's argument" : $"`{f.Code[u].Opcode.Name}`");
                        }
                        else {
                            faccs++;
                            viaFacc.Add(id);
                            if (polls) pollingFacc++;
                            if (i + 1 < f.Code.Count && f.Code[i + 1].Code == Opcodes.PushFacc) {
                                faccThenPush++;
                                if (polls) faccThenPushPolling++;
                            }
                        }
                        if (_natives[id] is NativeSignature sig && sig.KindsFromExe) {
                            for (int k = 0; k < sig.Argc; k++) {
                                int p = f.SingleOperand(i, k);
                                if (p < 0) continue;
                                byte po = f.Code[p].Code;
                                bool isInt = po is Opcodes.PushImm or Opcodes.PushC;
                                bool isFloat = po == Opcodes.PushFC;
                                if (sig.ArgKinds[k] == 'f') {
                                    if (isInt) floatArgInt++;
                                    else if (isFloat) floatArgFloat++;
                                }
                                else {
                                    if (isFloat) intArgFloat++;
                                    else if (isInt) intArgInt++;
                                }
                            }
                        }
                        break;
                    case Opcodes.PopFacc:
                        popfacc++;
                        int[] o = f.Operands[i];
                        int last = i - 1;
                        Bump(popfaccProducers, o.Length > 0 && o[0] >= 0 && last >= 0 ? f.Code[last].Opcode.Name : "-");
                        break;
                    case Opcodes.PushFacc:
                        pushfacc++;
                        Bump(pushfaccBefore, i > 0 ? f.Code[i - 1].Opcode.Name : "-");
                        break;
                    case Opcodes.PopV or Opcodes.PopV1 or Opcodes.PopVA or Opcodes.PopVA1:
                        Bump(stores, (ins.Opcode.Name, VarType(s.Block, f.WorkerIndex, ins.Operand!.Value)));
                        break;
                    case Opcodes.Nop:
                        nops++;
                        Bump(nopPlaces, !f.Reachable[i] ? "unreachable" : i + 1 == f.Code.Count ? "last in the worker" : "reachable");
                        break;
                }
                if (IsEnd(op) || op == Opcodes.Ret) {
                    string entryNote = f.EntriesHere.Skip(i + 1).Take(1).Any(e => e.Count > 0) || i + 1 == f.Code.Count ? "before the next entry" : "inside";
                    Bump(ends, (s.Kind == "event" ? $"event type {f.Worker.Type}" : s.Kind, ins.Opcode.Name));
                    Bump(endsAtEntryEnd, $"`{ins.Opcode.Name}` {entryNote}");
                    if (op is Opcodes.EndP or Opcodes.EndP1) {
                        int p = f.SingleOperand(i, 0);
                        Bump(endpValues, p >= 0 && f.IntValue(p) is int ev ? $"`{f.Code[p].Opcode.Name} {ev}`" : p >= 0 ? $"`{f.Code[p].Opcode.Name}`" : "an expression");
                    }
                }
                if (op is Opcodes.PopAJz or Opcodes.PopAJnz && !(f.LabelsHere[i].Count > 0)) {
                    string last = i > 0 ? f.Code[i - 1].Opcode.Name : "-";
                    Bump(conditionLast, last switch {
                        "eq" or "ne" or "gt" or "lt" or "ge" or "le" or "gtu" or "ltu" or "geu" or "leu" => "a comparison",
                        "land" or "lor" => "`land`/`lor`",
                        "not" => "`not`",
                        "btst" or "btstn" => "`btst`/`btstn`",
                        _ => $"a value (`{last}`)",
                    });
                }
            }
        }

        Sub("Integer constants");
        Line($"`pushimm` {N(imm)}; `pushc` {N(poolInRange + poolOutOfRange)}, of which {N(poolOutOfRange)} push a value outside `i16` and {N(poolInRange)} one that `pushimm` could hold.");
        if (poolInRange > 0) {
            Line($"Those {N(poolInRange)} are used by: {string.Join(", ", poolInRangeUse.OrderByDescending(k => k.Value).Take(6).Select(k => $"`{k.Key}` {N(k.Value)}"))}.");
            if (poolInRangeExample is (SurveyScript ps, WorkerFlow pf, int pi)) {
                Line();
                Line(Snippet(ps, pf, pi - 2, pi + 2, "a pool constant that fits an immediate"));
            }
        }
        Sub("Floats");
        Line($"`pushfc` {N(fc)} ({N(fcIntegral)} of them integral values). Arguments of natives whose kinds come from the handler's pops: "
             + $"float parameters get `pushfc` {N(floatArgFloat)} times and an integer push {N(floatArgInt)} times; integer parameters get an integer push {N(intArgInt)} times and `pushfc` {N(intArgFloat)} times.");
        Sub("Native calls");
        Line($"`func` {N(funcs)}, `func_facc` {N(faccs)}. {N(viaFunc.Intersect(viaFacc).Count())} natives are called both ways, {N(viaFacc.Except(viaFunc).Count())} only as `func_facc`, {N(viaFunc.Except(viaFacc).Count())} only as `func`. "
             + "`func_facc` is a call whose result is not used (a statement); `func`'s result is always used:");
        Line();
        Table(["Consumer of a `func` result", "Calls"], funcConsumers.OrderByDescending(k => k.Value).Take(10).Select(k => new object?[] { k.Key, k.Value }));
        Line();
        Line($"`popfacc` {N(popfacc)} (discards a value; last instruction of what it pops: {string.Join(", ", popfaccProducers.OrderByDescending(k => k.Value).Take(6).Select(k => $"`{k.Key}` {N(k.Value)}"))}); "
             + $"`pushfacc` {N(pushfacc)}{(pushfacc > 0 ? $" (after {string.Join(", ", pushfaccBefore.OrderByDescending(k => k.Value).Take(4).Select(k => $"`{k.Key}` {N(k.Value)}"))})" : "")}.");
        Line();
        Line($"Natives that can wait (a poll handler): called as `func_facc` {N(pollingFacc)} times, as `func` {N(pollingFunc)}. "
             + $"`func_facc` directly followed by `pushfacc` (the call as a statement, then its result read from the float accumulator): {N(faccThenPush)}, "
             + $"{N(faccThenPushPolling)} of them for natives that can wait.");
        Sub("Store modes");
        Line("Store mode 1 (`popv_1`, `popva_1`) saturates to the variable's type; mode 0 truncates.");
        Line();
        string[] types = [.. stores.Keys.Select(k => k.Type).Distinct().OrderBy(t => Array.IndexOf(TypeNames, t))];
        Table(["Store", .. types], new[] { "popv", "popv_1", "popva", "popva_1" }.Select(o => new object?[] { $"`{o}`" }.Concat(types.Select(t => (object?)stores.GetValueOrDefault((o, t))))));
        Sub("Ends");
        string[] endOps = ["end", "endp", "end_1", "endp_1", "op_54", "ret"];
        string[] kinds = [.. ends.Keys.Select(k => k.Kind).Distinct().Order()];
        Table(["Worker", .. endOps.Select(e => $"`{e}`")], kinds.Select(k => new object?[] { k }.Concat(endOps.Select(e => (object?)ends.GetValueOrDefault((k, e))))));
        Line();
        Line($"Where: {string.Join(", ", endsAtEntryEnd.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {N(k.Value)}"))}.");
        Line($"What `endp` returns: {string.Join(", ", endpValues.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {N(k.Value)}"))}.");
        Sub("Conditions");
        Line("The last instruction before a `popa_jz`/`popa_jnz` that does not start a statement (`if`, loop and case conditions):");
        Line();
        Table(["Condition ends in", "Jumps"], conditionLast.OrderByDescending(k => k.Value).Take(10).Select(k => new object?[] { k.Key, k.Value }));
        Line();
        Line($"Logic and bit opcodes: {string.Join(", ", new[] { "land", "lor", "not", "and", "or", "xor", "bnot", "btst", "btstn", "shl", "sar" }.Select(o => $"`{o}` {N(ops.GetValueOrDefault(o))}"))}.");
        Sub("Padding");
        Line($"`nop`: {N(nops)}{(nops > 0 ? $" ({Counts(nopPlaces, code: false)})" : "")}; `nop_1d`, `nop_1e`, `nop_76`: {N(ops.GetValueOrDefault("nop_1d") + ops.GetValueOrDefault("nop_1e") + ops.GetValueOrDefault("nop_76"))}; "
             + $"breakpoints (`op_40`): {N(ops.GetValueOrDefault("op_40"))}.");
        Sub("Opcodes never used");
        Line(string.Join(", ", Opcodes.All.Where(o => !o.Doc.StartsWith("invalid", StringComparison.Ordinal) && ops.GetValueOrDefault(o.Name) == 0).Select(o => $"`{o.Name}`")) + ".");
    }

    /// <summary>How a model orders a block's pool: the order it allocates values in, compacted to the pool's values.</summary>
    private enum PoolModel {
        /// <summary>The order of first use in code order.</summary>
        FirstUse,
        /// <summary>Per statement (stack empty to empty): non-negative values first, then negative ones.</summary>
        PositiveFirst,
        /// <summary>As <see cref="PositiveFirst"/>, and a negative value also allocates its absolute value where it is used (the literal before the fold).</summary>
        LiteralThenFold,
    }

    /// <summary>Whether the block's pool (ints when <paramref name="floats"/> is false) is in the order <paramref name="model"/> gives.</summary>
    private static bool PoolMatches(SurveyScript s, bool floats, PoolModel model, bool reorder = false) {
        ScriptBlock b = s.Block;
        int n = b.U16(b.Workers[0].Header + (floats ? 6 : 4));
        uint Value(ushort k) => floats ? BitConverter.SingleToUInt32Bits(b.FloatConst(0, k) ?? 0) : (uint)(b.IntConst(0, k) ?? 0);
        bool Negative(uint v) => floats ? (v & 0x8000_0000) != 0 && (v & 0x7fff_ffff) != 0 : (int)v < 0;
        uint Abs(uint v) => floats ? v & 0x7fff_ffff : unchecked(0u - v);
        Dictionary<uint, int> index = [];
        for (int k = 0; k < n; k++) index.TryAdd(Value((ushort)k), k);
        List<int> order = [];
        HashSet<int> placed = [];
        void Place(uint v) {
            if (index.TryGetValue(v, out int k) && placed.Add(k)) order.Add(k);
        }
        byte push = floats ? Opcodes.PushFC : Opcodes.PushC;
        IEnumerable<WorkerFlow> workers = s.Flows.OrderBy(f => f.Worker.Start);
        if (reorder) {
            // The order of the workers' first pool entries: the order the compiler met them in.
            int First(WorkerFlow f) => f.Code.Where(c => c.Code == push).Select(c => (int)c.Operand!.Value).DefaultIfEmpty(int.MaxValue).Min();
            workers = workers.OrderBy(First);
        }
        foreach (WorkerFlow f in workers) {
            List<uint> negatives = [];
            for (int i = 0; i < f.Code.Count; i++) {
                if (f.Code[i].Code == push) {
                    uint v = Value(f.Code[i].Operand!.Value);
                    if (model == PoolModel.FirstUse || !Negative(v)) Place(v);
                    else {
                        if (model == PoolModel.LiteralThenFold) Place(Abs(v));
                        negatives.Add(v);
                    }
                }
                if (i + 1 >= f.Code.Count || f.Depth[i + 1] == 0) {
                    foreach (uint v in negatives) Place(v);
                    negatives.Clear();
                }
            }
        }
        return order.Count == n && order.Select((k, j) => k == j).All(t => t);
    }

    private void MeasurePools() {
        Section("Constant and variable tables");
        Line("The variable, integer and float tables are one set per block, shared by every worker (docs/formats/script-block.md). "
             + "The models walk the workers' code in code order and give each value an index when they first meet it:");
        Line();
        Line("- *first use*: where it is first pushed;");
        Line("- *non-negative first*: per statement (from an empty stack to the next), the non-negative values where they are pushed, then the negative ones;");
        Line("- *literal, then fold*: as before, but a negative value also gives its absolute value an index where it is pushed, as a parser that numbers the literal `48.0` and then folds `-48.0` into a new constant would; indices of values that end up unused are dropped.");
        Line();
        long intDup = 0, floatDup = 0, ints = 0, floats = 0, intBlocks = 0, floatBlocks = 0;
        long varBlocks = 0, varOrder = 0, varSorted = 0, varUnused = 0, vars = 0, varDup = 0, varStorageGrouped = 0;
        long[] intModel = new long[4], floatModel = new long[4];
        Dictionary<string, long> storageOrders = [];
        foreach (SurveyScript s in _scripts) {
            ScriptBlock b = s.Block;
            if (b.Workers.Count == 0) continue;
            int nInt = b.U16(b.Workers[0].Header + 4), nFloat = b.U16(b.Workers[0].Header + 6), nVar = b.U16(b.Workers[0].Header + 2);
            ints += nInt;
            floats += nFloat;
            vars += nVar;
            intDup += nInt - Enumerable.Range(0, nInt).Select(k => b.IntConst(0, (ushort)k)).Distinct().Count();
            floatDup += nFloat - Enumerable.Range(0, nFloat).Select(k => b.FloatConst(0, (ushort)k) is float v ? BitConverter.SingleToUInt32Bits(v) : 0).Distinct().Count();
            varDup += nVar - Enumerable.Range(0, nVar).Select(k => b.Variable(0, (ushort)k)).Distinct().Count();
            foreach (PoolModel m in Enum.GetValues<PoolModel>()) {
                if (nInt > 0 && PoolMatches(s, false, m)) intModel[(int)m]++;
                if (nFloat > 0 && PoolMatches(s, true, m)) floatModel[(int)m]++;
            }
            if (nInt > 0 && PoolMatches(s, false, PoolModel.LiteralThenFold, reorder: true)) intModel[3]++;
            if (nFloat > 0 && PoolMatches(s, true, PoolModel.LiteralThenFold, reorder: true)) floatModel[3]++;
            if (nInt > 0) intBlocks++;
            if (nFloat > 0) floatBlocks++;

            List<int> varUse = [];
            HashSet<int> varSeen = [];
            foreach (WorkerFlow f in s.Flows.OrderBy(f => f.Worker.Start)) {
                foreach (Instr ins in f.Code) {
                    if (ins.Code is Opcodes.PushV or Opcodes.PopV or Opcodes.PopV1 or Opcodes.PushVA or Opcodes.PopVA or Opcodes.PopVA1 or Opcodes.PushVRef && varSeen.Add(ins.Operand!.Value)) {
                        varUse.Add(ins.Operand!.Value);
                    }
                }
            }
            varUnused += nVar - varUse.Count;
            if (nVar > 0) {
                varBlocks++;
                if (varUse.Count == nVar && varUse.Select((x, k) => x == k).All(t => t)) varOrder++;
                List<(uint Loc, ushort Len)> table = [.. Enumerable.Range(0, nVar).Select(k => b.Variable(0, (ushort)k)!.Value)];
                if (table.Zip(table.Skip(1)).All(p => (p.First.Loc >> 25 & 7, p.First.Loc & 0xffffff).CompareTo((p.Second.Loc >> 25 & 7, p.Second.Loc & 0xffffff)) <= 0)) varSorted++;
                List<uint> storages = [.. table.Select(t => t.Loc >> 25 & 7)];
                List<uint> runs = [.. storages.Where((x, k) => k == 0 || storages[k - 1] != x)];
                if (runs.Distinct().Count() == runs.Count) varStorageGrouped++;
                Bump(storageOrders, string.Join(" ", runs));
            }
        }
        string Share(long v, long of) => $"{N(v)} ({Pct(v, of)})";
        Table(["Table", "Entries", "Duplicate values", "Blocks with entries", "first use", "non-negative first", "literal, then fold", "literal, then fold, workers in their pool order"], [
            ["integers", ints, intDup, intBlocks, Share(intModel[0], intBlocks), Share(intModel[1], intBlocks), Share(intModel[2], intBlocks), Share(intModel[3], intBlocks)],
            ["floats", floats, floatDup, floatBlocks, Share(floatModel[0], floatBlocks), Share(floatModel[1], floatBlocks), Share(floatModel[2], floatBlocks), Share(floatModel[3], floatBlocks)],
        ]);
        Line();
        Line("The last column takes the workers in the order of their lowest pool index instead of their code order: the pools keep the order the "
             + "compiler met the workers in, which is not the order they are laid out in (see \"Worker order\").");
        Line();
        Line("Every pool entry is used by some instruction.");
        Line();
        Line($"Variables: {N(vars)} entries in {N(varBlocks)} blocks, {N(varUnused)} not used by any instruction, {N(varDup)} repeat another entry's location and length. "
             + $"The table is in first-use order in {Share(varOrder, varBlocks)} blocks; sorted by (storage, offset) in {Share(varSorted, varBlocks)}; "
             + $"each storage's variables together in {Share(varStorageGrouped, varBlocks)}. Storage order, most common:");
        Line();
        Table(["Storages in table order", "Blocks"], storageOrders.OrderByDescending(k => k.Value).Take(8).Select(k => new object?[] { $"`{k.Key}`", k.Value }));
    }
}
