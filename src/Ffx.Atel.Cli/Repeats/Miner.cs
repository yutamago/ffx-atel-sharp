// SPDX-License-Identifier: MIT

namespace Ffx.Atel.Cli.Repeats;

// The repeats (docs/research/repeats.md, "Method"): runs of consecutive statements of one block whose
// shapes recur, found by splitting the starts of every shape by the shape that follows, a statement at
// a time (a suffix-tree walk over the blocks). A run is kept when it is maximal: no longer run has the
// same places, on either side. Then a greedy cover takes the run that saves the most lines, keeps the
// places none of the runs taken so far use (or hold, or sit in), and repeats; what it takes is the list
// of helpers, each with a parameter per hole whose value differs between its places.

/// <summary>A block of statements of the corpus: where it is, and its statements.</summary>
internal sealed class MinedBlock {
    public required int Script;
    public required int Worker;
    public required BlockRecord Record;
    /// <summary>The parent block's index in <see cref="Corpus.Blocks"/>, or -1 for a method's body.</summary>
    public required int Parent;
}

/// <summary>The statements of every script mined, as shapes and hole values.</summary>
internal sealed class Corpus {
    public List<string> Scripts { get; } = [];
    public List<long[]> Holes { get; } = [];
    public List<MinedBlock> Blocks { get; } = [];

    /// <summary>Adds a script: per worker, its blocks (parents and holes within the worker) and its holes, made script-wide.</summary>
    public void Add(string script, IReadOnlyList<(int Worker, List<BlockRecord> Blocks, long[] Holes)> workers) {
        int s = Scripts.Count;
        Scripts.Add(script);
        List<long> holes = [];
        foreach ((int worker, List<BlockRecord> blocks, long[] h) in workers) {
            int offset = Blocks.Count, holeOffset = holes.Count;
            holes.AddRange(h);
            foreach (BlockRecord b in blocks) {
                for (int k = 0; k < b.HoleFrom.Length; k++) {
                    b.HoleFrom[k] += holeOffset;
                    b.HoleTo[k] += holeOffset;
                }
                Blocks.Add(new MinedBlock { Script = s, Worker = worker, Record = b, Parent = b.Parent < 0 ? -1 : b.Parent + offset });
            }
        }
        Holes.Add([.. holes]);
    }
}

/// <summary>A hole's values over a helper's places: a parameter when they differ.</summary>
/// <param name="Columns">The holes (in walk order) that always hold this same value: one parameter used that often.</param>
/// <param name="Tag">What the hole holds (<see cref="ShapeWalker.TagInt"/>…).</param>
/// <param name="Distinct">How many different values.</param>
/// <param name="Common">The most common value, and at how many places.</param>
internal sealed record Param(IReadOnlyList<int> Columns, long Tag, int Distinct, long Common, int CommonCount);

/// <summary>A run of shapes that recurs, and its places (block, index) in order.</summary>
internal sealed record Candidate(int[] Pattern, long[] Places, int Size, int Lines, bool Truncated);

/// <summary>A helper the cover took: the run, the places it folds, and its parameters.</summary>
internal sealed record Helper(Candidate Run, IReadOnlyList<long> Places, IReadOnlyList<Param> Params, int Holes, int FixedVarHoles, Traits Traits) {
    public int Rank { get; set; }
    /// <summary>Lines saved: a line per place instead of the code, and the code once as the helper's body.</summary>
    public long SavedLines => Miner.Net(Places.Count, Run.Lines);
    public long SavedStatements => Miner.Net(Places.Count, Run.Size);
}

/// <summary>Finds the repeats of a <see cref="Corpus"/> and covers it with helpers.</summary>
internal sealed class Miner(Corpus corpus, int minPlaces = 2, int maxLength = 48, int minSaved = 20, int maxParams = 8) {
    public static long Place(int block, int index) => ((long)block << 32) | (uint)index;

    public static (int Block, int Index) Unplace(long p) => ((int)(p >> 32), (int)p);

    /// <summary>The maximal runs that save at least <c>minSaved</c> lines if every place were folded.</summary>
    public List<Candidate> Candidates() {
        Dictionary<int, List<long>> starts = [];
        for (int b = 0; b < corpus.Blocks.Count; b++) {
            BlockRecord r = corpus.Blocks[b].Record;
            if (r.Dead) continue;
            for (int i = 0; i < r.Stmts.Length; i++) {
                StmtInfo s = r.Stmts[i];
                if (s.Id < 0 || s.Escapes) continue;
                if (!starts.TryGetValue(s.Id, out List<long>? l)) starts[s.Id] = l = [];
                l.Add(Place(b, i));
            }
        }
        List<Candidate> found = [];
        List<int> pattern = [];
        foreach ((int id, List<long> places) in starts) {
            if (places.Count < minPlaces) continue;
            pattern.Clear();
            pattern.Add(id);
            StmtInfo first = Stmt(places[0], 0);
            Explore(pattern, [.. places], first.Size, first.Lines, found);
        }
        return found;
    }

    private StmtInfo Stmt(long place, int offset) {
        (int b, int i) = Unplace(place);
        return corpus.Blocks[b].Record.Stmts[i + offset];
    }

    private void Explore(List<int> pattern, long[] places, int size, int lines, List<Candidate> found) {
        int length = pattern.Count;
        Dictionary<int, List<long>> next = [];
        foreach (long p in places) {
            (int b, int i) = Unplace(p);
            StmtInfo[] stmts = corpus.Blocks[b].Record.Stmts;
            if (i + length >= stmts.Length || stmts[i + length].Id < 0 || stmts[i + length].Escapes) continue;
            int id = stmts[i + length].Id;
            if (!next.TryGetValue(id, out List<long>? l)) next[id] = l = [];
            l.Add(p);
        }
        bool truncated = length >= maxLength;
        bool rightMaximal = truncated || !next.Values.Any(l => l.Count == places.Length);
        if (rightMaximal && LeftMaximal(places) && size >= 2 && Net(places.Length, lines) >= minSaved) {
            found.Add(new Candidate([.. pattern], places, size, lines, truncated));
        }
        if (truncated) return;
        foreach ((int id, List<long> l) in next) {
            if (l.Count < minPlaces) continue;
            StmtInfo s = Stmt(l[0], length);
            pattern.Add(id);
            Explore(pattern, [.. l], size + s.Size, lines + s.Lines, found);
            pattern.RemoveAt(pattern.Count - 1);
        }
    }

    /// <summary>
    ///     Lines a helper saves: each place becomes one line, and the helper's body is written once (as the
    ///     first place's code, which stays in the cover so that runs inside it are found too).
    /// </summary>
    public static long Net(int places, int lines) => (long)places * (lines - 1) - lines;

    private bool LeftMaximal(long[] places) {
        int? before = null;
        foreach (long p in places) {
            (int b, int i) = Unplace(p);
            if (i == 0) return true;
            int id = corpus.Blocks[b].Record.Stmts[i - 1].Id;
            if (id < 0 || corpus.Blocks[b].Record.Stmts[i - 1].Escapes) return true;
            if (before is null) before = id;
            else if (before != id) return true;
        }
        return false;
    }

    // ----- Cover -----------------------------------------------------------------------------------

    private readonly Dictionary<int, bool[]> _covered = [];
    private readonly Dictionary<int, int[]> _inside = [];
    // The helper whose body a statement is (its first place): a run lies all inside one body or outside all.
    private readonly Dictionary<int, int[]> _body = [];

    /// <summary>The helpers, best first: each run with the places no helper taken before uses.</summary>
    public List<Helper> Cover(List<Candidate> candidates, int maxHelpers) {
        // Best first; on a tie the run found first, so the cover is the same in every run.
        PriorityQueue<int, (long, int)> queue = new();
        for (int c = 0; c < candidates.Count; c++) queue.Enqueue(c, (-Net(candidates[c].Places.Length, candidates[c].Lines), c));
        List<Helper> taken = [];
        while (queue.Count > 0 && taken.Count < maxHelpers) {
            int c = queue.Dequeue();
            Candidate run = candidates[c];
            List<long> free = Free(run);
            long saved = Net(free.Count, run.Lines);
            if (free.Count < minPlaces || saved < minSaved) continue;
            if (queue.TryPeek(out _, out (long Saved, int) best) && saved < -best.Saved) {
                queue.Enqueue(c, (-saved, c));
                continue;
            }
            Helper h = Parameters(run, free);
            if (h.Params.Count > maxParams) continue;
            h.Rank = taken.Count + 1;
            taken.Add(h);
            for (int k = 0; k < free.Count; k++) Take(free[k], run.Pattern.Length, body: k == 0 ? h.Rank : 0);
        }
        return taken;
    }

    private List<long> Free(Candidate run) {
        List<long> free = [];
        int lastBlock = -1, lastEnd = 0;
        Dictionary<int, bool> ancestors = [];
        int length = run.Pattern.Length;
        foreach (long p in run.Places) {
            (int b, int i) = Unplace(p);
            if (b == lastBlock && i < lastEnd) continue;
            if (!ancestors.TryGetValue(b, out bool blocked)) ancestors[b] = blocked = AncestorTaken(b);
            if (blocked) continue;
            bool ok = true;
            _covered.TryGetValue(b, out bool[]? cov);
            _inside.TryGetValue(b, out int[]? ins);
            _body.TryGetValue(b, out int[]? body);
            int fence = body?[i] ?? 0;
            for (int k = i; k < i + length && ok; k++) ok = !(cov?[k] ?? false) && (ins?[k] ?? 0) == 0 && (body?[k] ?? 0) == fence;
            // Not the whole body of a helper again.
            if (ok && fence != 0 && (i == 0 || body![i - 1] != fence) && (i + length == body!.Length || body[i + length] != fence)) ok = false;
            if (!ok) continue;
            free.Add(p);
            lastBlock = b;
            lastEnd = i + length;
        }
        return free;
    }

    private bool AncestorTaken(int b) {
        for (MinedBlock m = corpus.Blocks[b]; m.Parent >= 0; m = corpus.Blocks[m.Parent]) {
            if (_covered.TryGetValue(m.Parent, out bool[]? cov) && cov[m.Record.ParentAt]) return true;
        }
        return false;
    }

    private void Take(long place, int length, int body) {
        (int b, int i) = Unplace(place);
        int n = corpus.Blocks[b].Record.Stmts.Length;
        if (body != 0) {
            if (!_body.TryGetValue(b, out int[]? fence)) _body[b] = fence = new int[n];
            for (int k = i; k < i + length; k++) fence[k] = body;
        }
        else {
            if (!_covered.TryGetValue(b, out bool[]? cov)) _covered[b] = cov = new bool[n];
            for (int k = i; k < i + length; k++) cov[k] = true;
        }
        for (MinedBlock m = corpus.Blocks[b]; m.Parent >= 0; m = corpus.Blocks[m.Parent]) {
            if (!_inside.TryGetValue(m.Parent, out int[]? ins)) _inside[m.Parent] = ins = new int[corpus.Blocks[m.Parent].Record.Stmts.Length];
            ins[m.Record.ParentAt]++;
        }
    }

    // ----- Parameters ------------------------------------------------------------------------------

    /// <summary>The holes of a run at its places: a parameter per group of holes that always hold the same value and differ between places.</summary>
    public Helper Parameters(Candidate run, IReadOnlyList<long> places) {
        int length = run.Pattern.Length;
        List<long[]> rows = [];
        foreach (long p in places) {
            (int b, int i) = Unplace(p);
            MinedBlock m = corpus.Blocks[b];
            long[] holes = corpus.Holes[m.Script];
            int from = m.Record.HoleFrom[i], to = m.Record.HoleTo[i + length - 1];
            rows.Add(holes[from..to]);
        }
        int columns = rows[0].Length;
        Dictionary<string, List<int>> groups = new(StringComparer.Ordinal);
        int fixedVars = 0;
        for (int c = 0; c < columns; c++) {
            long v0 = rows[0][c];
            if (rows.All(r => r[c] == v0)) {
                if ((v0 >> 56) is ShapeWalker.TagVar or ShapeWalker.TagElem or ShapeWalker.TagVarTarget or ShapeWalker.TagElemTarget) fixedVars++;
                continue;
            }
            string key = string.Join(',', rows.Select(r => r[c]));
            if (!groups.TryGetValue(key, out List<int>? l)) groups[key] = l = [];
            l.Add(c);
        }
        List<Param> ps = [];
        foreach (List<int> cols in groups.Values.OrderBy(l => l[0])) {
            int c = cols[0];
            var counts = rows.GroupBy(r => r[c]).Select(g => (g.Key, Count: g.Count())).OrderByDescending(x => x.Count).ThenBy(x => x.Key).ToList();
            ps.Add(new Param(cols, rows[0][c] >> 56, counts.Count, counts[0].Key, counts[0].Count));
        }
        Traits traits = Traits.None;
        (int b0, int i0) = Unplace(places[0]);
        for (int k = 0; k < length; k++) traits |= corpus.Blocks[b0].Record.Stmts[i0 + k].Traits;
        return new Helper(run, places, ps, columns, fixedVars, traits);
    }
}
