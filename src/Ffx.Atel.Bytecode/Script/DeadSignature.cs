// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ffx.Atel.Bytecode.Script;

/// <summary>
///     The code of a dead <c>if (false)</c> block as a short hash, the key of its name in
///     <c>data/cut-names.toml</c> (docs/research/dead-code.md). The decompiler names blocks by it, and the linker
///     finds the blocks a mod switches on by it (<see cref="DeadJumps"/>).
/// </summary>
/// <remarks>
///     As the decompiler's worker signature, but over the instructions the block's <c>jmp</c> skips: constants by
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
        Worker worker = block.Workers[w];
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
                else sb.Append(Operand(block, w, instr.Opcode.Operand, v));
            }
            sb.Append('\n');
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash)[..Length];
    }

    /// <summary>
    ///     An operand as the signatures read it: an integer or float constant by its value, a variable by its
    ///     location word and length, anything else by its number.
    /// </summary>
    public static string Operand(ScriptBlock block, int w, OperandKind kind, ushort v) => kind switch {
        OperandKind.Int => block.IntConst(w, v) is int c ? "=" + c.ToString(CultureInfo.InvariantCulture) : "c" + v.ToString(CultureInfo.InvariantCulture),
        OperandKind.Float => block.FloatConst(w, v) is float f ? "=f" + BitConverter.SingleToUInt32Bits(f).ToString("x8", CultureInfo.InvariantCulture) : "f" + v.ToString(CultureInfo.InvariantCulture),
        OperandKind.Var => block.Variable(w, v) is var (location, length)
            ? "=v" + location.ToString("x8", CultureInfo.InvariantCulture) + ":" + length.ToString(CultureInfo.InvariantCulture)
            : "v" + v.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString(CultureInfo.InvariantCulture),
    };
}
