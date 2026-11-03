// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler.Layout;

namespace Ffx.Atel.Decompiler;

/// <summary>
///     A dead <c>if (false)</c> block of the source: code the original compiler kept behind a <c>jmp</c>
///     (docs/research/dead-code.md). <see cref="Offset"/> is the <c>jmp</c>'s offset in the script's code,
///     <see cref="Start"/> to <see cref="End"/> the dead code it skips (for <c>if (false) … else</c> without
///     the <c>jmp</c> over the else).
/// </summary>
/// <param name="Worker">The worker's number.</param>
/// <param name="WorkerName">The worker's class name in the source.</param>
/// <param name="Entries">The entries whose code holds the block (those that start where the block's entry code starts).</param>
/// <param name="HasElse">Whether it is <c>if (false) … else</c>.</param>
/// <param name="Instructions">The dead instructions.</param>
/// <param name="Depth">How many dead blocks it is inside (0: in live code).</param>
/// <param name="Signature">The <see cref="DeadSignature"/> of the dead code, the key of its name.</param>
/// <param name="Name">Its name in <c>data/cut-names.toml</c>, if it has one.</param>
/// <param name="Code">The dead code as the decompiler writes it, unindented.</param>
/// <param name="ElseCode">For <c>if (false) … else</c>: the live else part, the code that runs instead.</param>
/// <param name="LiveTwins">
///     How many times the same instructions (constants by value, variables by location, jumps relative) stand in
///     live code of the same script: a dead block with a live twin is a variant whose sibling runs, one without
///     is code that runs nowhere in the script.
/// </param>
public sealed record DeadBlock(
    int Worker, string WorkerName, IReadOnlyList<int> Entries, uint Offset, uint Start, uint End, bool HasElse,
    int Instructions, int Depth, string Signature, string? Name, string Code, string? ElseCode, int LiveTwins = 0);

internal sealed partial class WorkerDecompiler {
    /// <summary>The worker's decoded code.</summary>
    internal IReadOnlyList<Instr> Code => _code;

    /// <summary>The index of the instruction at <paramref name="offset"/>, or the code's length past its end.</summary>
    internal int IndexAt(uint offset) => _indexOf.TryGetValue(offset, out int i) ? i : _code.Count;

    /// <summary>The token of instruction <paramref name="i"/> as <see cref="DeadSignature"/> reads it; a jump's target is apart, as its offset.</summary>
    internal (string Token, long Target) Token(int i) {
        Instr instr = _code[i];
        string op = instr.Code.ToString("x2", CultureInfo.InvariantCulture);
        if (instr.Operand is not ushort v) return (op, -1);
        Bytecode.Script.Worker w = _block.Workers[_worker];
        if (instr.Opcode.Operand == OperandKind.Label && v < w.Labels.Length) return (op + " @", w.Labels[v]);
        return (op + " " + WorkerSignature.Operand(_block, _worker, instr.Opcode.Operand, v), -1);
    }
    /// <summary>The <c>jmp</c> unit and the dead units (exclusive end) of a dead construct of a method.</summary>
    private static (int From, int To) DeadUnits(Method m, Built b) {
        if (b.Kind == Construct.Dead) return (b.Start + 1, b.End);
        int p = m.Units.FindIndex(u => u.Labels.Contains(b.Labels[0]));
        return (b.Start + 1, p - 1);
    }

    /// <summary>The instruction range a run of units covers (exclusive end).</summary>
    private (int From, int To) Instructions(Method m, int fromUnit, int toUnit) {
        int from = fromUnit < m.Units.Count ? m.Units[fromUnit].Start : m.Units[^1].End;
        int to = toUnit < m.Units.Count ? m.Units[toUnit].Start : m.Units[^1].End;
        return (from, Math.Max(from, to));
    }

    /// <summary>The signature of a dead construct's code, from its units: what <see cref="Structurer"/> asks to name it.</summary>
    private string DeadSignatureOf(List<Unit> units, int jumpUnit, int endUnit) {
        int from = jumpUnit + 1 < units.Count ? units[jumpUnit + 1].Start : units[^1].End;
        int to = endUnit < units.Count ? units[endUnit].Start : units[^1].End;
        return DeadSignature.Of(_block, _worker, _code, from, Math.Max(from, to));
    }

    /// <summary>The worker's dead blocks, in code order, with their code as C#.</summary>
    public List<DeadBlock> DeadBlocks(string workerName) {
        Bytecode.Script.Worker w = _block.Workers[_worker];
        HashSet<int> csharpTargets = [];
        foreach (Method m in _methods) {
            Walk(m.Body, s => {
                if (s is GotoStmt g) csharpTargets.Add(g.Label);
                if (s is CondGotoStmt c) csharpTargets.Add(c.Label);
            });
        }
        bool counting = _printer.Counting;
        _printer.Counting = false;
        List<DeadBlock> result = [];
        try {
            foreach (Method m in _methods) {
                List<(Built B, int From, int To)> dead = [.. m.Built.Where(b => b.Kind is Construct.Dead or Construct.DeadElse).Select(b => {
                    (int f, int t) = DeadUnits(m, b);
                    return (b, f, t);
                })];
                foreach ((Built b, int fromUnit, int toUnit) in dead) {
                    (int from, int to) = Instructions(m, fromUnit, toUnit);
                    uint offset = _code[m.Units[b.Start].Start].Offset;
                    uint start = from < _code.Count ? _code[from].Offset : w.End;
                    uint end = to < _code.Count ? _code[to].Offset : w.End;
                    uint entryStart = w.Entries.Where(e => e <= offset).DefaultIfEmpty(0u).Max();
                    List<int> entries = [.. Enumerable.Range(0, w.Entries.Length).Where(e => w.Entries[e] == entryStart)];
                    int depth = dead.Count(o => !ReferenceEquals(o.B, b) && o.From <= b.Start && b.Start < o.To);
                    IfStmt node = (IfStmt)b.Node;
                    StringBuilder sb = new();
                    TreeWriter writer = new(sb, _printer, _block, _worker, _ => null, csharpTargets);
                    writer.Block(node.Then, 0);
                    string code = sb.ToString();
                    string? other = null;
                    if (node.Else is not null) {
                        sb.Clear();
                        writer.Block(node.Else, 0);
                        other = sb.ToString();
                    }
                    result.Add(new DeadBlock(_worker, workerName, entries, offset, start, end, b.Kind == Construct.DeadElse, to - from, depth,
                        DeadSignature.Of(_block, _worker, _code, from, to), node.Cut, code, other));
                }
            }
        }
        finally {
            _printer.Counting = counting;
        }
        return [.. result.OrderBy(d => d.Offset)];
    }
}

/// <summary>Counts the live twins of dead blocks (<see cref="DeadBlock.LiveTwins"/>) across a script's workers.</summary>
internal static class DeadTwins {
    public static List<DeadBlock> Count(IReadOnlyList<WorkerDecompiler> workers, List<DeadBlock> blocks) {
        int n = workers.Count;
        var tokens = new (string Token, long Target)[n][];
        var dead = new bool[n][];
        for (int w = 0; w < n; w++) {
            IReadOnlyList<Instr> code = workers[w].Code;
            tokens[w] = [.. Enumerable.Range(0, code.Count).Select(workers[w].Token)];
            dead[w] = new bool[code.Count + 1];
        }
        foreach (DeadBlock b in blocks) {
            WorkerDecompiler wd = workers[b.Worker];
            for (int i = wd.IndexAt(b.Start), end = wd.IndexAt(b.End); i < end; i++) dead[b.Worker][i] = true;
        }
        // Live windows by their first two tokens (one for the last instruction).
        Dictionary<string, List<(int W, int I)>> index = new(StringComparer.Ordinal);
        for (int w = 0; w < n; w++) {
            for (int i = 0; i < tokens[w].Length; i++) {
                if (dead[w][i]) continue;
                string key = i + 1 < tokens[w].Length ? tokens[w][i].Token + "|" + tokens[w][i + 1].Token : tokens[w][i].Token;
                (index.TryGetValue(key, out List<(int, int)>? list) ? list : index[key] = []).Add((w, i));
                string one = "1:" + tokens[w][i].Token;
                (index.TryGetValue(one, out List<(int, int)>? l1) ? l1 : index[one] = []).Add((w, i));
            }
        }
        List<DeadBlock> result = [];
        foreach (DeadBlock b in blocks) {
            WorkerDecompiler wd = workers[b.Worker];
            int from = wd.IndexAt(b.Start), to = wd.IndexAt(b.End), len = to - from;
            if (len == 0) {
                result.Add(b);
                continue;
            }
            var t = tokens[b.Worker];
            string key = len >= 2 ? t[from].Token + "|" + t[from + 1].Token : "1:" + t[from].Token;
            int twins = 0;
            if (index.TryGetValue(key, out List<(int W, int I)>? candidates)) {
                long origin = wd.Code[from].Offset;
                foreach ((int w, int i) in candidates) {
                    if (i + len > tokens[w].Length) continue;
                    long start = workers[w].Code[i].Offset;
                    bool same = true;
                    for (int k = 0; k < len && same; k++) {
                        (string tok, long target) = tokens[w][i + k];
                        same = !dead[w][i + k] && tok == t[from + k].Token
                            && (target < 0 ? t[from + k].Target < 0 : target - start == t[from + k].Target - origin);
                    }
                    if (same) twins++;
                }
            }
            result.Add(b with { LiveTwins = twins });
        }
        return result;
    }
}
