// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Bytecode.Code;

/// <summary>
///     The order of the constant pools, by the rules of docs/corpus.md ("Constant and variable
///     tables"): the compiler lays the pools out this way unless the source pins them
///     (<c>[Ints(…)]</c>, <c>[Floats(…)]</c>), and the decompiler pins exactly where the original
///     differs.
/// </summary>
/// <remarks>
///     The workers' code is walked in code order (worker order).
///     <list type="bullet">
///         <item>Integers: in the order of first use.</item>
///         <item>Floats: per statement (empty stack to empty stack), the non-negative values where they
///         are pushed and the negative ones at the end of the statement; a negative value also numbers
///         its absolute value where it is pushed, if that value is in the pool at all (a parser that
///         numbers the literal <c>28.2</c> and then folds <c>-28.2</c> into a new constant).</item>
///     </list>
/// </remarks>
public static class ConstantPools {
    /// <summary>The integer pool the rule gives for this code.</summary>
    public static List<uint> Ints(IEnumerable<IReadOnlyList<CodeOp>> workers) {
        List<uint> order = [];
        HashSet<uint> placed = [];
        foreach (IReadOnlyList<CodeOp> code in workers) {
            foreach (CodeOp op in code) {
                if (op is { Pooled: true, Code: Opcodes.PushC } && placed.Add(op.Value)) order.Add(op.Value);
            }
        }
        return order;
    }

    /// <summary>The float pool the rule gives for this code; <paramref name="argc"/> gives a native's argument count.</summary>
    public static List<uint> Floats(IReadOnlyList<IReadOnlyList<CodeOp>> workers, Func<ushort, int?> argc) {
        HashSet<uint> used = [];
        foreach (IReadOnlyList<CodeOp> code in workers) {
            foreach (CodeOp op in code) {
                if (op is { Pooled: true, Code: Opcodes.PushFC }) used.Add(op.Value);
            }
        }
        List<uint> order = [];
        HashSet<uint> placed = [];
        void Place(uint v) {
            if (used.Contains(v) && placed.Add(v)) order.Add(v);
        }
        foreach (IReadOnlyList<CodeOp> code in workers) {
            List<uint> negatives = [];
            int depth = 0;
            for (int i = 0; i < code.Count; i++) {
                CodeOp op = code[i];
                if (op is { Pooled: true, Code: Opcodes.PushFC }) {
                    uint v = op.Value;
                    if ((v & 0x8000_0000) == 0 || (v & 0x7fff_ffff) == 0) Place(v);
                    else {
                        Place(v & 0x7fff_ffff);
                        negatives.Add(v);
                    }
                }
                depth = DepthAfter(op, depth, argc);
                if (depth == 0 || i + 1 == code.Count) {
                    foreach (uint v in negatives) Place(v);
                    negatives.Clear();
                }
            }
        }
        return order;
    }

    /// <summary>
    ///     The stack depth after an instruction, in a linear pass: the stack is empty at every label,
    ///     jump and end (docs/corpus.md), so code order is enough. After a jump, an end and a call
    ///     statement the depth is 0.
    /// </summary>
    public static int DepthAfter(CodeOp op, int depth, Func<ushort, int?> argc) {
        (int pops, int pushes) = op.Code is Opcodes.Func or Opcodes.FuncFacc
            ? StackEffects.Native(op.Code, argc(op.Operand ?? 0) ?? 0)
            : StackEffects.Of(op.Code);
        int after = Math.Max(0, depth - pops) + pushes;
        return op.Code == Opcodes.FuncFacc || StackEffects.Jumps(op.Code) || !StackEffects.FallsThrough(op.Code) ? 0 : after;
    }

    /// <summary>The argument count lookup of a signature table.</summary>
    public static Func<ushort, int?> Argc(NativeSignatures natives) => id => natives[id]?.Argc;
}
