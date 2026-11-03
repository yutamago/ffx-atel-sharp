// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler.Layout;

/// <summary>
///     A worker's code as a short hash that does not depend on the script it is in or on how the
///     decompiler writes it: the key of a library class's name (<see cref="LibraryNames"/>).
/// </summary>
/// <remarks>
///     The hash covers the worker type, its entry and label offsets (from the worker's first byte) and every
///     instruction, with operands that index a script's tables replaced by what they point at: an integer or
///     float constant by its value, a variable by its location word and length. Jumps keep their label
///     numbers and requests their worker numbers, which the bytes keep. So identical code hashes the same in
///     every script, and the hash stays the same when the decompiler's C# for it changes (new names, typed
///     constants), which a hash of the source would not.
/// </remarks>
public static class WorkerSignature {
    /// <summary>The number of hex digits in a signature (64 bits).</summary>
    public const int Length = 16;

    /// <summary>The signature of worker <paramref name="w"/> of <paramref name="block"/>: 16 lower-case hex digits.</summary>
    public static string Of(ScriptBlock block, int w) {
        Bytecode.Script.Worker worker = block.Workers[w];
        StringBuilder sb = new();
        sb.Append(CultureInfo.InvariantCulture, $"type {worker.Type}\n");
        sb.Append("entries");
        foreach (uint e in worker.Entries) sb.Append(' ').Append(Relative(e, worker));
        sb.Append("\nlabels");
        foreach (uint l in worker.Labels) sb.Append(' ').Append(Relative(l, worker));
        sb.Append('\n');
        foreach (Instr instr in block.Decode(worker)) {
            sb.Append(instr.Code.ToString("x2", CultureInfo.InvariantCulture));
            if (instr.Operand is ushort v) sb.Append(' ').Append(Operand(block, w, instr.Opcode.Operand, v));
            sb.Append('\n');
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash)[..Length];
    }

    private static string Relative(uint offset, Bytecode.Script.Worker worker) =>
        offset >= worker.Start ? (offset - worker.Start).ToString(CultureInfo.InvariantCulture) : "-" + (worker.Start - offset).ToString(CultureInfo.InvariantCulture);

    private static string Operand(ScriptBlock block, int w, OperandKind kind, ushort v) => kind switch {
        OperandKind.Int => block.IntConst(w, v) is int c ? "=" + c.ToString(CultureInfo.InvariantCulture) : "c" + v.ToString(CultureInfo.InvariantCulture),
        OperandKind.Float => block.FloatConst(w, v) is float f ? "=f" + BitConverter.SingleToUInt32Bits(f).ToString("x8", CultureInfo.InvariantCulture) : "f" + v.ToString(CultureInfo.InvariantCulture),
        OperandKind.Var => block.Variable(w, v) is var (location, length)
            ? "=v" + location.ToString("x8", CultureInfo.InvariantCulture) + ":" + length.ToString(CultureInfo.InvariantCulture)
            : "v" + v.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString(CultureInfo.InvariantCulture),
    };
}
