// SPDX-License-Identifier: MIT

namespace Ffx.Atel.Bytecode.Script;

/// <summary>
///     A dead block's <c>jmp</c>: the instruction a switched-on block turns into a no-op (docs/research/dead-code.md,
///     "Switching a block on").
/// </summary>
/// <param name="Worker">The worker's number.</param>
/// <param name="Offset">The <c>jmp</c>'s code offset (from the block's code start, as <see cref="Instr.Offset"/>).</param>
/// <param name="Label">The <c>jmp</c>'s operand, the label it jumps to.</param>
/// <param name="Target">That label's code offset: where the dead code ends (for <c>if (false) … else</c>, where the else starts).</param>
/// <param name="Signature">The <see cref="DeadSignature"/> of the dead code.</param>
/// <param name="On">
///     Whether the block is switched on: its <c>jmp</c> is already the <c>nop_76</c> with the same operand (found only
///     with <c>switchedOn</c>; a file a mod's <c>EnableCut</c> was linked into).
/// </param>
public sealed record DeadJump(int Worker, uint Offset, ushort Label, uint Target, string Signature, bool On = false);

/// <summary>
///     Finds dead blocks by the signature of their code in the bytecode alone, without the decompiler: what the
///     linker does to switch a named block on in the player's file (<c>Cut.*</c>, docs/research/dead-code.md).
/// </summary>
/// <remarks>
///     A dead block is a forward <c>jmp L</c> over code nothing else reaches: no jump from outside lands in it
///     and no entry starts in it. Its signature covers the instructions the <c>jmp</c> skips, or, for
///     <c>if (false) A else B</c> (<c>jmp L1; A; jmp L2; L1: B</c>), those of <c>A</c> without the
///     <c>jmp L2</c>: the same ranges the decompiler hashes for the <c>Dead</c> and <c>DeadElse</c> constructs.
/// </remarks>
public static class DeadJumps {
    /// <summary>The encoded <c>jmp</c> (<c>0x30</c> with its operand bit): the first byte of a dead block.</summary>
    public const byte JmpByte = Opcodes.Jmp | 0x80;

    /// <summary>The encoded <c>nop_76</c>: a no-op that takes an operand, so it replaces a <c>jmp</c> byte for byte.</summary>
    public const byte NopByte = Opcodes.Nop76 | 0x80;

    /// <summary>The dead blocks of <paramref name="block"/> whose code has one of <paramref name="signatures"/>, in worker and code order.</summary>
    /// <param name="switchedOn">
    ///     Also find blocks that are switched on, whose <c>jmp L</c> is a <c>nop_76 L</c> (<see cref="DeadJump.On"/>):
    ///     the same code behind the same operand. The in-game switch reads a loaded script with it, which a linked
    ///     mod may have switched blocks on in (<c>yuta.atel.cuts</c>, docs/research/dead-code.md).
    /// </param>
    public static List<DeadJump> Find(ScriptBlock block, IReadOnlySet<string> signatures, bool switchedOn = false) {
        List<DeadJump> found = [];
        if (signatures.Count == 0) return found;
        for (int w = 0; w < block.Workers.Count; w++) {
            Worker worker = block.Workers[w];
            List<Instr> code = block.Decode(worker);
            Dictionary<uint, int> indexOf = new(code.Count);
            for (int i = 0; i < code.Count; i++) indexOf[code[i].Offset] = i;
            for (int i = 0; i < code.Count; i++) {
                bool on = switchedOn && code[i].Code == Opcodes.Nop76;
                if (code[i].Code != Opcodes.Jmp && !on) continue;
                // nop_76's operand is raw: read it as the label the jmp had.
                uint? to = on ? (code[i].Operand is ushort l && l < worker.Labels.Length ? worker.Labels[l] : null) : Target(worker, code[i]);
                if (to is not uint target || target <= code[i].Offset || !indexOf.TryGetValue(target, out int end)) continue;
                string? signature = Match(i + 1, end);
                if (signature is null && end - 1 > i && Target(worker, code[end - 1]) is uint t && code[end - 1].Code == Opcodes.Jmp && t > code[end - 1].Offset) {
                    signature = Match(i + 1, end - 1);
                }
                if (signature is null || !Unreached(worker, code, i + 1, end)) continue;
                found.Add(new DeadJump(w, code[i].Offset, code[i].Operand!.Value, target, signature, on));
            }

            string? Match(int from, int to) {
                string s = DeadSignature.Of(block, w, code, from, to);
                return signatures.Contains(s) ? s : null;
            }
        }
        return found;
    }

    /// <summary>The code offset a jump goes to, or null for an instruction that is not one (or a label out of the table).</summary>
    private static uint? Target(Worker worker, Instr instr) =>
        instr.Opcode.Operand == OperandKind.Label && instr.Operand is ushort v && v < worker.Labels.Length ? worker.Labels[v] : null;

    /// <summary>Whether no jump outside instructions <paramref name="from"/> to <paramref name="to"/> lands in them, and no entry starts there.</summary>
    private static bool Unreached(Worker worker, List<Instr> code, int from, int to) {
        if (from >= to) return true;
        uint start = code[from].Offset, end = code[to - 1].Offset + code[to - 1].Length;
        if (worker.Entries.Any(e => e >= start && e < end)) return false;
        for (int k = 0; k < code.Count; k++) {
            if (k >= from && k < to) continue;
            if (Target(worker, code[k]) is uint t && t >= start && t < end) return false;
        }
        return true;
    }
}
