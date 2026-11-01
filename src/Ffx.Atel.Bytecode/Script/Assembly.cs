// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace Ffx.Atel.Bytecode.Script;

/// <summary>A syntax or shape error in assembly text.</summary>
public sealed class AssemblyException(int line, string message)
    : Exception(line > 0 ? $"line {line}: {message}" : message) {
    public int Line { get; } = line;
}

/// <summary>
///     Readable assembly: the same text as ffx-rust's <c>ffx script disasm</c>
///     (<c>crates/ffx-script/src/text.rs</c>).
/// </summary>
/// <remarks>
///     <code>
///     worker 1 type 2
///     e0:
///         pushc c3        ; 1234
///         popv v0
///     l2:
///         pushv v0
///         popa_jz l5
///         func 0x6012
///     </code>
///     <list type="bullet">
///         <item><c>worker N</c> starts worker N's code; workers may appear in any order, each once.</item>
///         <item><c>eN:</c> and <c>lN:</c> place entry point N and jump label N of the current worker; every
///         original entry and label must be placed once.</item>
///         <item>Operands: <c>lN</c>, <c>cN</c>, <c>fN</c>, <c>vN</c>, <c>wN</c>, or a number (<c>12</c>, <c>-3</c>,
///         <c>0x6012</c>); any form is accepted for any opcode and stored as a <c>u16</c>.</item>
///         <item><c>;</c> starts a comment.</item>
///     </list>
///     Assembly applies onto the original script (<see cref="ScriptBlock.Rebuild"/>): constants,
///     variables and everything outside the code stay as they were.
/// </remarks>
public static class Assembly {
    /// <summary>Disassembles every worker; <paramref name="annotate"/> may add a comment to each native call.</summary>
    public static string Disassemble(ScriptBlock script, Func<ushort, string?>? annotate = null) {
        StringBuilder sb = new();
        sb.Append(CultureInfo.InvariantCulture, $"; {script.Workers.Count} workers\n");
        for (int w = 0; w < script.Workers.Count; w++) {
            sb.Append('\n');
            DisassembleWorker(sb, script, w, annotate);
        }
        return sb.ToString();
    }

    /// <summary>Disassembles one worker, starting with its <c>worker N type T</c> line.</summary>
    public static void DisassembleWorker(StringBuilder sb, ScriptBlock script, int w, Func<ushort, string?>? annotate = null) {
        Worker worker = script.Workers[w];
        sb.Append(CultureInfo.InvariantCulture, $"worker {w} type {worker.Type}\n");
        foreach (Instr instr in script.Decode(worker)) {
            for (int i = 0; i < worker.Entries.Length; i++) {
                if (worker.Entries[i] == instr.Offset) sb.Append(CultureInfo.InvariantCulture, $"e{i}:\n");
            }
            for (int i = 0; i < worker.Labels.Length; i++) {
                if (worker.Labels[i] == instr.Offset) sb.Append(CultureInfo.InvariantCulture, $"l{i}:\n");
            }
            string line = Render(script, w, instr);
            if (instr.Opcode.Operand == OperandKind.Func && instr.Operand is ushort id && annotate?.Invoke(id) is string note) {
                line = $"{line,-24} ; {note}";
            }
            sb.Append("    ").Append(line).Append('\n');
        }
    }

    /// <summary>One instruction as text, with the constant's value as a comment.</summary>
    public static string Render(ScriptBlock script, int worker, Instr instr) {
        Opcode op = instr.Opcode;
        if (instr.Operand is not ushort v) return op.Name;
        return op.Operand switch {
            OperandKind.Label  => $"{op.Name} l{v}",
            OperandKind.Int    => script.IntConst(worker, v) is int c ? $"{op.Name} {"c" + v,-9} ; {c}" : $"{op.Name} c{v}",
            OperandKind.Float  => script.FloatConst(worker, v) is float f ? $"{op.Name} {"f" + v,-9} ; {RustFloat.Format(f)}" : $"{op.Name} f{v}",
            OperandKind.Imm    => $"{op.Name} {(short)v}",
            OperandKind.Var    => $"{op.Name} v{v}",
            OperandKind.Worker => $"{op.Name} w{v}",
            _                  => $"{op.Name} 0x{v:x4}",
        };
    }

    private sealed class Assembled(int entries, int labels) {
        public bool Seen;
        public readonly List<byte> Code = [];
        public readonly uint?[] Entries = new uint?[entries];
        public readonly uint?[] Labels = new uint?[labels];
    }

    private static long? ParseNumber(string s) {
        bool negative = s.StartsWith('-');
        if (negative) s = s[1..];
        long v;
        if (s.StartsWith("0x", StringComparison.Ordinal)) {
            if (!long.TryParse(s.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v)) return null;
        }
        else if (s.Length == 0 || !s.All(char.IsAsciiDigit) || !long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v)) {
            return null;
        }
        return negative ? -v : v;
    }

    /// <summary>Parses an operand (<c>l3</c>, <c>c3</c>, <c>-3</c>, <c>0x6012</c>) to its <c>u16</c>.</summary>
    public static ushort? ParseOperand(string s) {
        long? v = s.Length > 1 && s[0] is 'l' or 'c' or 'f' or 'v' or 'w'
            ? (long.TryParse(s.AsSpan(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n) ? n : null)
            : ParseNumber(s);
        return v switch {
            >= -0x8000 and <= -1 => (ushort)(short)v,
            >= 0 and <= 0xffff   => (ushort)v,
            _                    => null,
        };
    }

    /// <summary>Assembles text onto the original script.</summary>
    /// <exception cref="AssemblyException">Unknown mnemonics or operands; missing or repeated workers, entries and labels.</exception>
    public static ScriptBlock Assemble(ScriptBlock original, string text) {
        IReadOnlyList<Worker> workers = original.Workers;
        Assembled[] output = workers.Select(w => new Assembled(w.Entries.Length, w.Labels.Length)).ToArray();
        int? current = null;
        string[] lines = text.Split('\n');
        for (int n = 0; n < lines.Length; n++) {
            int lineNo = n + 1;
            string line = lines[n];
            int semicolon = line.IndexOf(';');
            if (semicolon >= 0) line = line[..semicolon];
            line = line.Trim();
            if (line.Length == 0) continue;
            string[] words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string first = words[0];
            if (first == "worker") {
                if (words.Length < 2 || !int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out int w) || w >= output.Length) {
                    throw new AssemblyException(lineNo, $"expected a worker number below {output.Length}");
                }
                if (output[w].Seen) throw new AssemblyException(lineNo, $"worker {w} appears twice");
                output[w].Seen = true;
                current = w;
                continue;
            }
            if (current is not int cw) throw new AssemblyException(lineNo, "code before the first `worker` line");
            Assembled a = output[cw];
            uint here = (uint)a.Code.Count;
            if (first.EndsWith(':')) {
                string name = first[..^1];
                (uint?[] table, string kind) = name.StartsWith('e') ? (a.Entries, "entry")
                    : name.StartsWith('l') ? (a.Labels, "label")
                    : throw new AssemblyException(lineNo, $"unknown definition {first}");
                if (!int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int i) || i >= table.Length) {
                    throw new AssemblyException(lineNo, $"worker {cw} has no {kind} {name[1..]}");
                }
                if (table[i] is not null) throw new AssemblyException(lineNo, $"{kind} {i} placed twice");
                table[i] = here;
                continue;
            }
            Opcode op = Opcodes.ByName(first) ?? throw new AssemblyException(lineNo, $"unknown instruction {first}");
            ushort? operand = null;
            if (words.Length > 1) {
                operand = ParseOperand(words[1]) ?? throw new AssemblyException(lineNo, $"bad operand {words[1]}");
            }
            if (words.Length > 2) throw new AssemblyException(lineNo, $"unexpected {words[2]}");
            new Instr(here, op.Code, operand).Encode(a.Code);
        }

        List<byte[]> code = [];
        List<uint[]> entries = [];
        List<uint[]> labels = [];
        for (int w = 0; w < output.Length; w++) {
            Assembled a = output[w];
            if (!a.Seen) throw new AssemblyException(0, $"worker {w} is missing");
            int e = Array.FindIndex(a.Entries, x => x is null);
            if (e >= 0) throw new AssemblyException(0, $"worker {w}: entry {e} is not placed");
            int l = Array.FindIndex(a.Labels, x => x is null);
            if (l >= 0) throw new AssemblyException(0, $"worker {w}: label {l} is not placed");
            entries.Add(a.Entries.Select(x => x!.Value).ToArray());
            labels.Add(a.Labels.Select(x => x!.Value).ToArray());
            code.Add([.. a.Code]);
        }
        try {
            return original.Rebuild(code, entries, labels);
        }
        catch (ScriptFormatException ex) {
            throw new AssemblyException(0, ex.Message);
        }
    }
}

/// <summary>Formats an <c>f32</c> as Rust's <c>{:?}</c> does, so comments match ffx-rust's disassembly.</summary>
public static class RustFloat {
    public static string Format(float f) {
        if (float.IsNaN(f)) return "NaN";
        if (float.IsPositiveInfinity(f)) return "inf";
        if (float.IsNegativeInfinity(f)) return "-inf";
        string sign = f < 0 || (f == 0 && float.IsNegative(f)) ? "-" : "";
        float a = Math.Abs(f);
        if (a == 0) return sign + "0.0";
        // Shortest round-trip digits and decimal exponent: value = d.ddd × 10^exp.
        (string digits, int exp) = ShortestDigits(a);
        if (a >= 1e-4f && a < 1e16f) {
            string text;
            if (exp >= 0) {
                string intPart = digits.Length > exp + 1 ? digits[..(exp + 1)] : digits.PadRight(exp + 1, '0');
                string frac = digits.Length > exp + 1 ? digits[(exp + 1)..] : "0";
                text = intPart + "." + frac;
            }
            else {
                text = "0." + new string('0', -exp - 1) + digits;
            }
            return sign + text;
        }
        string mantissa = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
        return sign + mantissa + "e" + exp.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     The shortest significant digits that read back as <paramref name="a"/>, and the decimal
    ///     exponent of the first one. Among the shortest, the one nearest the exact value; an exact
    ///     tie rounds up, as Rust's Grisu/Dragon formatting does (.NET's "R" rounds ties to even).
    /// </summary>
    private static (string Digits, int Exp) ShortestDigits(float a) {
        // Exact decimal expansion of a = m × 2^e.
        int bits = BitConverter.SingleToInt32Bits(a);
        int rawExp = (bits >> 23) & 0xff;
        long m = bits & 0x7fffff;
        int e;
        if (rawExp == 0) e = -149;
        else {
            m |= 0x800000;
            e = rawExp - 150;
        }
        System.Numerics.BigInteger num = m, den = 1;
        if (e >= 0) num <<= e;
        else den <<= -e;
        string intDigits = (num / den).ToString(CultureInfo.InvariantCulture);
        System.Numerics.BigInteger rem = num % den;
        StringBuilder frac = new();
        while (rem != 0) {
            rem *= 10;
            frac.Append((char)('0' + (int)(rem / den)));
            rem %= den;
        }
        string all = (intDigits == "0" ? "" : intDigits) + frac;
        int point = intDigits == "0" ? 0 : intDigits.Length;
        int lead = 0;
        while (all[lead] == '0') lead++;
        string exact = all[lead..];
        int exp10 = point - lead - 1;
        for (int p = 1; p <= 9; p++) {
            string digits;
            int exp = exp10;
            if (exact.Length <= p) digits = exact;
            else {
                char[] d = exact[..p].ToCharArray();
                if (exact[p] >= '5') {
                    int i = p - 1;
                    while (i >= 0 && d[i] == '9') d[i--] = '0';
                    if (i >= 0) d[i]++;
                    else {
                        d = ['1', .. d[..^1]];
                        exp++;
                    }
                }
                digits = new string(d);
            }
            digits = digits.TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            string text = $"{digits[0]}.{(digits.Length > 1 ? digits[1..] : "0")}e{exp}";
            if (float.Parse(text, CultureInfo.InvariantCulture) == a) return (digits, exp);
        }
        throw new InvalidOperationException("no float needs more than 9 digits");
    }
}
