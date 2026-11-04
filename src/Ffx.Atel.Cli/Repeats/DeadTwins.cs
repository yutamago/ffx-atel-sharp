// SPDX-License-Identifier: MIT

using Ffx.Atel.Decompiler.Repeats;

namespace Ffx.Atel.Cli.Repeats;

// A macro's `if (PARAM == k) A else B` with PARAM a constant was folded by the original compiler into a
// jump, keeping both branches (docs/research/dead-code.md): with the condition true the code is
// `A; if (false) { B }`, with it false `if (false) { A } else { B }`. The two are twins: the same macro
// with another constant. Here they are counted by shape (constants as holes), so that the report can say
// how many dead blocks a helper with that constant as a parameter would hold in its body.

/// <summary>The twins of folded <c>if</c>s in a <see cref="Corpus"/>.</summary>
internal static class DeadTwins {
    /// <summary>What <see cref="Count"/> found.</summary>
    /// <param name="FalseForms">Statements <c>if (false) { A } else { B }</c> (outside dead code).</param>
    /// <param name="TrueForms">Statements <c>if (false) { B }</c> without else, after at least one statement.</param>
    /// <param name="TrueWithTwin">Of <paramref name="TrueForms"/>, those whose statements before are an <c>A</c> of a false form with the same <c>B</c>.</param>
    /// <param name="FalseWithTwin">Of <paramref name="FalseForms"/>, those whose <c>A</c>, <c>B</c> occur as a true form.</param>
    /// <param name="TwinDeadLines">Lines of the dead branches of the true forms with a twin.</param>
    internal sealed record Result(int FalseForms, int TrueForms, int TrueWithTwin, int FalseWithTwin, long TwinDeadLines);

    public static Result Count(Corpus corpus, Func<int, bool>? script = null) {
        // The blocks of each statement: (block, index, ordinal) to the child block.
        Dictionary<(int, int, int), int> child = [];
        for (int b = 0; b < corpus.Blocks.Count; b++) {
            MinedBlock m = corpus.Blocks[b];
            if (m.Parent >= 0) child[(m.Parent, m.Record.ParentAt, m.Record.Ordinal)] = b;
        }
        bool IsFalseIf(int b, int i, out int then) =>
            child.TryGetValue((b, i, 0), out then) && corpus.Blocks[then].Record.Dead && !corpus.Blocks[b].Record.Dead;
        static string Key(StmtInfo[] s, int from, int count) => string.Join(',', s.Skip(from).Take(count).Select(x => x.Id));

        Dictionary<string, HashSet<string>> aByB = new(StringComparer.Ordinal);
        HashSet<string> truePairs = new(StringComparer.Ordinal);
        List<(string A, string B)> falseForms = [];
        int trueForms = 0, trueTwins = 0;
        long deadLines = 0;
        for (int b = 0; b < corpus.Blocks.Count; b++) {
            if (script is not null && !script(corpus.Blocks[b].Script)) continue;
            StmtInfo[] s = corpus.Blocks[b].Record.Stmts;
            for (int i = 0; i < s.Length; i++) {
                if (!IsFalseIf(b, i, out int then) || !child.TryGetValue((b, i, 1), out int other)) continue;
                StmtInfo[] a = corpus.Blocks[then].Record.Stmts, bb = corpus.Blocks[other].Record.Stmts;
                string ka = Key(a, 0, a.Length), kb = Key(bb, 0, bb.Length);
                falseForms.Add((ka, kb));
                if (!aByB.TryGetValue(kb, out HashSet<string>? set)) aByB[kb] = set = new(StringComparer.Ordinal);
                set.Add(ka);
            }
        }
        for (int b = 0; b < corpus.Blocks.Count; b++) {
            if (script is not null && !script(corpus.Blocks[b].Script)) continue;
            StmtInfo[] s = corpus.Blocks[b].Record.Stmts;
            for (int i = 1; i < s.Length; i++) {
                if (!IsFalseIf(b, i, out int then) || child.ContainsKey((b, i, 1))) continue;
                trueForms++;
                StmtInfo[] dead = corpus.Blocks[then].Record.Stmts;
                string kb = Key(dead, 0, dead.Length);
                if (!aByB.TryGetValue(kb, out HashSet<string>? las)) continue;
                foreach (string ka in las) {
                    int n = ka.Length == 0 ? 0 : ka.Count(c => c == ',') + 1;
                    if (n == 0 || n > i || Key(s, i - n, n) != ka) continue;
                    trueTwins++;
                    truePairs.Add(ka + "|" + kb);
                    deadLines += dead.Sum(x => x.Lines);
                    break;
                }
            }
        }
        int falseTwins = 0;
        foreach ((string a, string bk) in falseForms) {
            if (truePairs.Contains(a + "|" + bk)) falseTwins++;
        }
        return new Result(falseForms.Count, trueForms, trueTwins, falseTwins, deadLines);
    }
}
