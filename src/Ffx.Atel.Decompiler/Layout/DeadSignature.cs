// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler.Layout;

/// <summary>
///     The code of a dead <c>if (false)</c> block as a short hash, the key of its name in
///     <c>data/cut-names.toml</c> (<see cref="CutNames"/>; docs/research/dead-code.md).
/// </summary>
/// <remarks>
///     As <see cref="WorkerSignature"/>, but over the instructions the block's <c>jmp</c> skips: constants by
///     value, variables by location and length, natives and requests by their numbers, and jumps by their
///     target's offset from the block's first byte (label numbers depend on the worker the block is in).
///     So the same dead code hashes the same in every worker and script, and the hash does not change when
///     the decompiler's C# for it does.
/// </remarks>
public static class DeadSignature {
    /// <summary>The number of hex digits in a signature (64 bits).</summary>
    public const int Length = 16;

    /// <summary>The signature of instructions <paramref name="from"/> to <paramref name="to"/> (exclusive) of <paramref name="code"/>, worker <paramref name="w"/>'s decoded code.</summary>
    public static string Of(ScriptBlock block, int w, IReadOnlyList<Instr> code, int from, int to) {
        Bytecode.Script.Worker worker = block.Workers[w];
        uint start = from < code.Count ? code[from].Offset : worker.End;
        StringBuilder sb = new("dead\n");
        for (int i = from; i < to; i++) {
            Instr instr = code[i];
            sb.Append(instr.Code.ToString("x2", CultureInfo.InvariantCulture));
            if (instr.Operand is ushort v) {
                sb.Append(' ');
                if (instr.Opcode.Operand == OperandKind.Label && v < worker.Labels.Length) {
                    long rel = (long)worker.Labels[v] - start;
                    sb.Append('@').Append(rel.ToString(CultureInfo.InvariantCulture));
                }
                else sb.Append(WorkerSignature.Operand(block, w, instr.Opcode.Operand, v));
            }
            sb.Append('\n');
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash)[..Length];
    }
}
