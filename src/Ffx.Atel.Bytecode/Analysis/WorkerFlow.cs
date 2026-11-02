// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Bytecode.Analysis;

/// <summary>Something the stack simulation of <see cref="WorkerFlow"/> found wrong at an instruction.</summary>
public enum StackIssueKind {
    /// <summary>The instruction pops more than the stack holds.</summary>
    Underflow,
    /// <summary>Two paths reach the instruction with different depths; the first one is kept.</summary>
    Merge,
    /// <summary>A <c>func_facc</c> (a call statement) leaves values on the stack.</summary>
    Leftover,
    /// <summary>A native that is not in the signature table: the depth after it is unknown.</summary>
    UnknownNative,
}

/// <param name="Index">Instruction index in <see cref="WorkerFlow.Code"/>.</param>
/// <param name="Have">Depth before the instruction (the first path's, for a merge).</param>
/// <param name="Other">For a merge, the other path's depth; for an underflow, what was popped.</param>
public readonly record struct StackIssue(StackIssueKind Kind, int Index, int Have, int Other);

/// <summary>
///     One worker's code as a control flow graph: instructions, where labels and entries point, the
///     successors of every instruction, what is reachable from the entries, and the value stack depth
///     before every instruction.
/// </summary>
/// <remarks>
///     The stack follows <see cref="StackEffects"/>, with the argument counts of <see cref="NativeSignatures"/>.
///     Every entry starts with an empty stack (a request starts a fresh level). After a <c>func_facc</c>
///     the depth is put back to 0, recording what was left as <see cref="StackIssueKind.Leftover"/>, so one
///     wrong argument count does not shift the rest of the entry. <c>call</c> is taken as balanced (the
///     callee's own code is checked in its worker).
/// </remarks>
public sealed class WorkerFlow {
    public ScriptBlock Block { get; }
    public int WorkerIndex { get; }
    public Worker Worker { get; }
    public List<Instr> Code { get; }

    /// <summary>Instruction index of every label; -1 for a label not at an instruction (never in the corpus).</summary>
    public int[] LabelAt { get; }

    /// <summary>Instruction index of every entry.</summary>
    public int[] EntryAt { get; }

    /// <summary>Labels placed at each instruction (empty for most).</summary>
    public List<int>[] LabelsHere { get; }

    /// <summary>Entries placed at each instruction.</summary>
    public List<int>[] EntriesHere { get; }

    /// <summary>
    ///     Depth before each instruction. Code no entry reaches (<see cref="Reachable"/>) is simulated too,
    ///     each run from an empty stack.
    /// </summary>
    public int[] Depth { get; }

    /// <summary>Whether an entry reaches the instruction.</summary>
    public bool[] Reachable { get; }

    /// <summary>For every instruction, the entry that reached it first (lowest entry number), or -1.</summary>
    public int[] ReachedFrom { get; }

    /// <summary>For every instruction, how many distinct entries reach it.</summary>
    public int[] EntryReach { get; }

    public List<StackIssue> Issues { get; } = [];

    private readonly Dictionary<uint, int> _indexOf;
    private readonly NativeSignatures _natives;

    public WorkerFlow(ScriptBlock block, int worker, NativeSignatures natives) {
        Block = block;
        WorkerIndex = worker;
        Worker = block.Workers[worker];
        _natives = natives;
        Code = block.Decode(Worker);
        _indexOf = new Dictionary<uint, int>(Code.Count);
        for (int i = 0; i < Code.Count; i++) _indexOf[Code[i].Offset] = i;
        LabelAt = Worker.Labels.Select(o => _indexOf.GetValueOrDefault(o, -1)).ToArray();
        EntryAt = Worker.Entries.Select(o => _indexOf.GetValueOrDefault(o, -1)).ToArray();
        LabelsHere = new List<int>[Code.Count];
        EntriesHere = new List<int>[Code.Count];
        for (int i = 0; i < Code.Count; i++) {
            LabelsHere[i] = [];
            EntriesHere[i] = [];
        }
        for (int l = 0; l < LabelAt.Length; l++) {
            if (LabelAt[l] >= 0) LabelsHere[LabelAt[l]].Add(l);
        }
        for (int e = 0; e < EntryAt.Length; e++) {
            if (EntryAt[e] >= 0) EntriesHere[EntryAt[e]].Add(e);
        }
        Depth = new int[Code.Count];
        ReachedFrom = new int[Code.Count];
        EntryReach = new int[Code.Count];
        Reachable = new bool[Code.Count];
        Array.Fill(Depth, -1);
        Array.Fill(ReachedFrom, -1);
        Reach();
        Simulate();
    }

    /// <summary>The instruction index of a jump's target, or -1.</summary>
    public int Target(int i) => Code[i].Operand is ushort l && l < LabelAt.Length ? LabelAt[l] : -1;

    /// <summary>The instructions that can run after instruction <paramref name="i"/>.</summary>
    public IEnumerable<int> Successors(int i) {
        byte op = Code[i].Code;
        if (StackEffects.Jumps(op) && Target(i) is int t and >= 0) yield return t;
        if (StackEffects.FallsThrough(op) && i + 1 < Code.Count) yield return i + 1;
    }

    /// <summary>(pops, pushes) of instruction <paramref name="i"/>, or null for a native with no known signature.</summary>
    public (int Pops, int Pushes)? Effect(int i) {
        Instr ins = Code[i];
        if (ins.Code is Opcodes.Func or Opcodes.FuncFacc) {
            return _natives[ins.Operand!.Value] is NativeSignature sig ? StackEffects.Native(ins.Code, sig.Argc) : null;
        }
        return StackEffects.Of(ins.Code);
    }

    private void Simulate() {
        Stack<(int Index, int Depth)> work = [];
        for (int e = EntryAt.Length - 1; e >= 0; e--) {
            if (EntryAt[e] >= 0) work.Push((EntryAt[e], 0));
        }
        Run(work);
        // Then the dead code, in code order, each run from an empty stack.
        for (int i = 0; i < Code.Count; i++) {
            if (Depth[i] >= 0) continue;
            work.Push((i, 0));
            Run(work);
        }
    }

    private void Run(Stack<(int Index, int Depth)> work) {
        while (work.Count > 0) {
            (int i, int d) = work.Pop();
            while (true) {
                if (Depth[i] >= 0) {
                    if (Depth[i] != d) Issues.Add(new StackIssue(StackIssueKind.Merge, i, Depth[i], d));
                    break;
                }
                Depth[i] = d;
                Instr ins = Code[i];
                if (Effect(i) is not (int pops, int pushes)) {
                    Issues.Add(new StackIssue(StackIssueKind.UnknownNative, i, d, 0));
                    break;
                }
                int after = d - pops;
                if (after < 0) {
                    Issues.Add(new StackIssue(StackIssueKind.Underflow, i, d, pops));
                    after = 0;
                }
                after += pushes;
                if (ins.Code == Opcodes.FuncFacc && after != 0) {
                    Issues.Add(new StackIssue(StackIssueKind.Leftover, i, d, after));
                    after = 0;
                }
                byte op = ins.Code;
                if (StackEffects.Jumps(op) && Target(i) is int t and >= 0) {
                    if (StackEffects.FallsThrough(op)) work.Push((t, after));
                    else {
                        i = t;
                        d = after;
                        continue;
                    }
                }
                if (!StackEffects.FallsThrough(op) || i + 1 >= Code.Count) break;
                i++;
                d = after;
            }
        }
    }

    private void Reach() {
        bool[] seen = new bool[Code.Count];
        for (int e = 0; e < EntryAt.Length; e++) {
            if (EntryAt[e] < 0) continue;
            Array.Clear(seen);
            Stack<int> work = new([EntryAt[e]]);
            while (work.Count > 0) {
                int i = work.Pop();
                if (seen[i]) continue;
                seen[i] = true;
                EntryReach[i]++;
                Reachable[i] = true;
                if (ReachedFrom[i] < 0) ReachedFrom[i] = e;
                foreach (int s in Successors(i)) {
                    if (!seen[s]) work.Push(s);
                }
            }
        }
    }

    private int[][]? _operands;

    /// <summary>
    ///     For every instruction, where each value it pops starts: the index of the first instruction of
    ///     the expression that pushed it, in push order (the first pushed first). Empty for an instruction
    ///     that pops nothing.
    /// </summary>
    /// <remarks>
    ///     A linear pass: the stack is empty at every label, entry and jump in every shipped script (the
    ///     survey's stack section), so no expression spans one. <c>op_2b</c>'s two copies both start where
    ///     the duplicated value did.
    /// </remarks>
    public int[][] Operands {
        get {
            if (_operands is null) ComputeOperands();
            return _operands!;
        }
    }

    private int[]? _consumer;

    /// <summary>
    ///     For every instruction that pushes, the instruction that pops its (last) value; -1 if none does
    ///     before the next label (never in the corpus).
    /// </summary>
    public int[] Consumer {
        get {
            if (_consumer is null) ComputeOperands();
            return _consumer!;
        }
    }

    private void ComputeOperands() {
        int[][] result = new int[Code.Count][];
        int[] consumer = new int[Code.Count];
        Array.Fill(consumer, -1);
        List<(int Start, int Last)> stack = [];
        for (int i = 0; i < Code.Count; i++) {
            if (LabelsHere[i].Count > 0 || EntriesHere[i].Count > 0) stack.Clear();
            (int pops, int pushes) = Effect(i) ?? (0, 1);
            int n = Math.Min(pops, stack.Count);
            int[] taken = new int[pops];
            for (int k = 0; k < pops - n; k++) taken[k] = -1;
            for (int k = 0; k < n; k++) {
                (int start, int last) = stack[stack.Count - n + k];
                taken[pops - n + k] = start;
                consumer[last] = i;
            }
            stack.RemoveRange(stack.Count - n, n);
            result[i] = taken;
            int first = n > 0 ? taken[pops - n] : i;
            for (int k = 0; k < pushes; k++) stack.Add((first, i));
            if (!StackEffects.FallsThrough(Code[i].Code) || StackEffects.Jumps(Code[i].Code)) stack.Clear();
        }
        _operands = result;
        _consumer = consumer;
    }

    /// <summary>
    ///     The single instruction that pushed operand <paramref name="k"/> of instruction <paramref name="i"/>,
    ///     or -1 when the operand is a longer expression.
    /// </summary>
    public int SingleOperand(int i, int k) {
        int[] ops = Operands[i];
        int start = ops[k], end = k + 1 < ops.Length ? ops[k + 1] : i;
        return start >= 0 && start == end - 1 ? start : -1;
    }

    /// <summary>The value of a constant push (<c>pushimm</c>, <c>pushc</c>), or null.</summary>
    public int? IntValue(int i) => Code[i] switch {
        { Code: Opcodes.PushImm, Operand: ushort v } => (short)v,
        { Code: Opcodes.PushC, Operand: ushort c } => Block.IntConst(WorkerIndex, c),
        _ => null,
    };

    /// <summary>The entry whose code region holds instruction <paramref name="i"/>: the last entry at or before it in code order.</summary>
    public int RegionOf(int i) {
        int best = -1;
        uint bestAt = 0;
        uint at = Code[i].Offset;
        for (int e = 0; e < Worker.Entries.Length; e++) {
            uint o = Worker.Entries[e];
            if (o <= at && (best < 0 || o >= bestAt)) {
                best = e;
                bestAt = o;
            }
        }
        return best;
    }
}
