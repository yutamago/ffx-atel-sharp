// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler;

/// <summary>The constructs the structurer recognises, as they are counted and switched off.</summary>
internal enum Construct { Switch, For, While, WhileTrue, Do, IfElse, If, DeadElse, Dead }

/// <summary>A statement of a method before structuring: one stack run, its labels and the entries that start there.</summary>
internal sealed class Unit {
    public required int Start, End;
    public Stmt? Stmt;
    public List<int> Labels { get; } = [];
    public List<int> Entries { get; } = [];

    /// <summary>The label its last instruction jumps to.</summary>
    public int? Jump;
}

/// <summary>A construct the structurer made: its kind, units and the blocks it holds.</summary>
internal sealed class Built {
    public required Construct Kind;
    public required int Start, End;
    public Stmt Node = null!;
    public List<int> Labels { get; } = [];
    public List<IReadOnlyList<Stmt>> Blocks { get; } = [];
}

/// <summary>
///     Turns one method's statements, with their labels and jumps, into <c>if</c>, loops and
///     <c>switch</c>, recognising the shapes the original compiler emitted (docs/corpus.md, "Control
///     flow"). Jumps no construct accounts for stay <c>goto</c> and their labels <c>lN:</c>. A construct
///     in <see cref="Disabled"/> (by its first unit) is not made; the decompiler switches off those
///     whose lowering does not give the original back.
/// </summary>
internal sealed class Structurer {
    private sealed record Ctx(int? Break, int? Continue, IReadOnlyDictionary<int, CaseLabel?>? Cases);

    private readonly List<Unit> _u;
    private readonly Func<Unit, Stmt> _asm;
    private readonly HashSet<(int, Construct)> _disabled;
    private readonly Func<int, int, string?>? _deadName;
    private readonly Dictionary<int, int> _pos = [];
    private readonly Dictionary<int, List<int>> _jumps = [];
    private readonly HashSet<int> _claimed = [];

    public List<Built> Built { get; } = [];
    public Dictionary<int, Built> Owner { get; } = [];
    public Dictionary<object, Built> ByBlock { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<object, Built> ByNode { get; } = new(ReferenceEqualityComparer.Instance);
    public List<Stmt> Body { get; }

    /// <param name="deadName">The name of a dead block (<see cref="IfStmt.Cut"/>) by its <c>jmp</c>'s unit and the unit its dead code ends before; null for none.</param>
    public Structurer(List<Unit> units, Func<Unit, Stmt> asm, HashSet<(int, Construct)> disabled, Func<int, int, string?>? deadName = null) {
        _u = units;
        _asm = asm;
        _disabled = disabled;
        _deadName = deadName;
        for (int i = 0; i < units.Count; i++) {
            foreach (int l in units[i].Labels) _pos[l] = i;
            if (units[i].Jump is int j) (_jumps.TryGetValue(j, out List<int>? list) ? list : _jumps[j] = []).Add(i);
        }
        Body = Parse(0, units.Count, new Ctx(null, null, null));
    }

    private Stmt? S(int i) => i >= 0 && i < _u.Count ? _u[i].Stmt : null;

    private int Pos(int label) => _pos.TryGetValue(label, out int p) ? p : -1;

    private IReadOnlyList<int> Jumps(int label) => _jumps.TryGetValue(label, out List<int>? l) ? l : [];

    private bool Free(int label) => !_claimed.Contains(label) && _pos.ContainsKey(label);

    private bool AllFrom(int label, int from, int to) => Jumps(label).All(j => j >= from && j < to);

    private static bool IsCtx(int label, Ctx ctx) => label == ctx.Break || label == ctx.Continue || ctx.Cases?.ContainsKey(label) == true;

    /// <param name="loopHead">The block of a loop that starts at <paramref name="from"/>: that unit's other labels and entries are placed before the loop.</param>
    private List<Stmt> Parse(int from, int to, Ctx ctx, bool loopHead = false) {
        List<Stmt> list = [];
        int i = from;
        while (i < to) {
            bool own = !(loopHead && i == from);
            if (own) foreach (int e in _u[i].Entries) list.Add(new EntryStmt(e));
            Built? b = Try(i, to, ctx);
            if (own) foreach (int l in _u[i].Labels.Where(l => !_claimed.Contains(l))) list.Add(new LabelStmt(l));
            // `x = 0; for (; …; x += 1)` reads as `for (x = 0; …; x += 1)`: the same code.
            if (b?.Node is ForStmt { Init.Count: 0, Step: [Stmt first, ..] } f
                && list.Count > 0 && list[^1] is AssignStmt { Clamped: false } init && !ByNode.ContainsKey(init)
                && (first is AssignStmt a ? a.Target : first is CompoundStmt cs ? cs.Target : null) == init.Target) {
                list.RemoveAt(list.Count - 1);
                ForStmt withInit = f with { Init = [init] };
                b.Node = withInit;
                ByNode.Remove(f);
                ByNode[withInit] = b;
            }
            if (b is not null) {
                list.Add(b.Node);
                i = b.End;
                continue;
            }
            list.Add(Convert(i, ctx));
            i++;
        }
        return list;
    }

    private Stmt Convert(int i, Ctx ctx) {
        Unit u = _u[i];
        switch (u.Stmt) {
            case null: return _asm(u);
            case GotoStmt g when g.Label == ctx.Break: return new BreakStmt();
            case GotoStmt g when g.Label == ctx.Continue: return new ContinueStmt();
            case GotoStmt g when ctx.Cases?.TryGetValue(g.Label, out CaseLabel? c) == true:
                return c is null ? new GotoCaseStmt(null) : c.Kind == CaseKind.Equal ? new GotoCaseStmt(c.Value) : g;
            default: return u.Stmt;
        }
    }

    /// <summary>Labels and entries of a unit the construct swallows (its own jump), as statements at the end of the block before it.</summary>
    private void Trailing(List<Stmt> block, int i) {
        foreach (int e in _u[i].Entries) block.Add(new EntryStmt(e));
        foreach (int l in _u[i].Labels.Where(l => !_claimed.Contains(l))) block.Add(new LabelStmt(l));
    }

    private bool Off(int i, Construct k) => _disabled.Contains((i, k));

    private Built Claim(Construct kind, int start, int end, params int?[] labels) {
        Built b = new() { Kind = kind, Start = start, End = end };
        foreach (int? l in labels) {
            if (l is not int n) continue;
            _claimed.Add(n);
            Owner[n] = b;
            b.Labels.Add(n);
        }
        Built.Add(b);
        return b;
    }

    private Built Done(Built b, Stmt node, params IReadOnlyList<Stmt>?[] blocks) {
        b.Node = node;
        ByNode[node] = b;
        foreach (IReadOnlyList<Stmt>? block in blocks) {
            if (block is null) continue;
            b.Blocks.Add(block);
            ByBlock[block] = b;
        }
        return b;
    }

    // A loop whose head is this unit holds whatever else starts here: loops first.
    private Built? Try(int i, int to, Ctx ctx) =>
        TryLoop(i, to, ctx)
        ?? (Off(i, Construct.Switch) ? null : TrySwitch(i, to, ctx))
        ?? TryIf(i, to, ctx)
        ?? TryDead(i, to, ctx);

    // ----- if ------------------------------------------------------------------------------------

    private Built? TryIf(int i, int to, Ctx ctx) {
        if (S(i) is not CondGotoStmt { IfTrue: false } g || !Free(g.Label)) return null;
        int p = Pos(g.Label);
        if (p <= i || p > to || Jumps(g.Label).Count != 1) return null;
        if (!Off(i, Construct.IfElse) && ElseOf(i, p, to, ctx) is (int ly, int q)) {
            Built b = Claim(Construct.IfElse, i, q, g.Label, ly);
            List<Stmt> then = Parse(i + 1, p - 1, ctx);
            Trailing(then, p - 1);
            List<Stmt> other = Parse(p, q, ctx);
            return Done(b, new IfStmt(g.Condition, then, other), then, other);
        }
        if (Off(i, Construct.If)) return null;
        Built c = Claim(Construct.If, i, p, g.Label);
        List<Stmt> body = Parse(i + 1, p, ctx);
        return Done(c, new IfStmt(g.Condition, body, null), body);
    }

    /// <summary>The else part of a construct whose first block ends at unit <paramref name="p"/>: a forward <c>jmp</c> just before it.</summary>
    private (int Label, int End)? ElseOf(int i, int p, int to, Ctx ctx) {
        if (p - 1 <= i || S(p - 1) is not GotoStmt e || IsCtx(e.Label, ctx) || !Free(e.Label)) return null;
        int q = Pos(e.Label);
        return q >= p && q <= to && Jumps(e.Label).Count == 1 ? (e.Label, q) : null;
    }

    private Built? TryDead(int i, int to, Ctx ctx) {
        if (S(i) is not GotoStmt g || IsCtx(g.Label, ctx) || !Free(g.Label)) return null;
        int p = Pos(g.Label);
        if (p <= i || p > to || Jumps(g.Label).Count != 1) return null;
        // Code an entry starts in is not dead: that stays a goto over it.
        if (Enumerable.Range(i + 1, p - i - 1).Any(k => _u[k].Entries.Count > 0)) return null;
        if (!Off(i, Construct.DeadElse) && ElseOf(i, p, to, ctx) is (int ly, int q)) {
            Built b = Claim(Construct.DeadElse, i, q, g.Label, ly);
            List<Stmt> then = Parse(i + 1, p - 1, ctx);
            Trailing(then, p - 1);
            List<Stmt> other = Parse(p, q, ctx);
            return Done(b, new IfStmt(null, then, other) { Cut = _deadName?.Invoke(i, p - 1) }, then, other);
        }
        if (Off(i, Construct.Dead)) return null;
        Built c = Claim(Construct.Dead, i, p, g.Label);
        List<Stmt> body = Parse(i + 1, p, ctx);
        return Done(c, new IfStmt(null, body, null) { Cut = _deadName?.Invoke(i, p) }, body);
    }

    // ----- loops ---------------------------------------------------------------------------------

    private Built? TryLoop(int i, int to, Ctx ctx) {
        foreach (int head in _u[i].Labels.Where(Free).Order()) {
            if (!AllFrom(head, i, to)) continue;
            if (!Off(i, Construct.For) && TryFor(i, to, head, ctx) is Built f) return f;
            // while (c): head: c; popa_jz exit; body; jmp head; exit:
            if (!Off(i, Construct.While) && S(i) is CondGotoStmt { IfTrue: false } c && Free(c.Label) && c.Label != head) {
                int p = Pos(c.Label);
                if (p - 1 > i && p <= to && S(p - 1) is GotoStmt { Label: var back } && back == head
                    && AllFrom(c.Label, i, p - 1) && AllFrom(head, i + 1, p)) {
                    Built b = Claim(Construct.While, i, p, c.Label, head);
                    List<Stmt> body = Parse(i + 1, p - 1, new Ctx(c.Label, head, ctx.Cases));
                    Trailing(body, p - 1);
                    return Done(b, new WhileStmt(c.Condition, body), body);
                }
            }
            int last = Jumps(head).Max();
            if (last < i) continue;
            // while (true): head: body; jmp head; [exit:]
            if (!Off(i, Construct.WhileTrue) && S(last) is GotoStmt) {
                int? exit = ExitOf(head, i, last);
                Built b = Claim(Construct.WhileTrue, i, last + 1, exit, head);
                List<Stmt> body = Parse(i, last, new Ctx(exit, head, ctx.Cases), loopHead: true);
                Trailing(body, last);
                return Done(b, new WhileStmt(null, body), body);
            }
            // do { body } while (c): head: body; c; popa_jnz head; [exit:]
            if (!Off(i, Construct.Do) && S(last) is CondGotoStmt { IfTrue: true } d && Jumps(head).Count == 1) {
                int? exit = ExitOf(head, i, last);
                Built b = Claim(Construct.Do, i, last + 1, exit, head);
                List<Stmt> body = Parse(i, last, new Ctx(exit, null, ctx.Cases), loopHead: true);
                Trailing(body, last);
                return Done(b, new DoStmt(body, d.Condition), body);
            }
        }
        return null;
    }

    /// <summary>
    ///     <c>for (; c; step) body</c>, laid out as <c>head: c; popa_jz exit; jmp body; step: …; jmp head;
    ///     body: …; jmp step; exit:</c>.
    /// </summary>
    private Built? TryFor(int i, int to, int head, Ctx ctx) {
        if (S(i) is not CondGotoStmt { IfTrue: false } c || !Free(c.Label) || c.Label == head) return null;
        if (S(i + 1) is not GotoStmt jb || !Free(jb.Label) || _u[i + 1].Labels.Count > 0 || _u[i + 1].Entries.Count > 0) return null;
        int p = Pos(c.Label);
        if (p > to || p - 1 <= i + 2 || S(p - 1) is not GotoStmt js || !Free(js.Label) || js.Label == c.Label || js.Label == head || js.Label == jb.Label) return null;
        int sp = Pos(js.Label), bp = Pos(jb.Label);
        if (sp != i + 2 || bp <= sp || bp >= p || S(bp - 1) is not GotoStmt { Label: var back } || back != head) return null;
        if (Jumps(head).Count != 1 || Jumps(jb.Label).Count != 1 || !AllFrom(c.Label, i, p - 1) || !AllFrom(js.Label, bp, p)) return null;
        if (Jumps(js.Label).Any(j => S(j) is not GotoStmt)) return null;
        // The step: plain statements, no other labels.
        for (int k = sp; k < bp - 1; k++) {
            if (S(k) is not (AssignStmt { Clamped: false } or CompoundStmt or CallStmt) || _u[k].Entries.Count > 0 || _u[k].Labels.Any(l => l != js.Label)) return null;
        }
        if (_u[bp - 1].Entries.Count > 0 || _u[bp - 1].Labels.Any(l => l != js.Label)) return null;
        if (_u[bp].Labels.Any(l => l != jb.Label) || _u[bp].Entries.Count > 0) return null;
        Built b = Claim(Construct.For, i, p, c.Label, jb.Label, head, js.Label);
        List<Stmt> step = [.. Enumerable.Range(sp, bp - 1 - sp).Select(k => S(k)!)];
        List<Stmt> body = Parse(bp, p - 1, new Ctx(c.Label, js.Label, ctx.Cases), loopHead: true);
        Trailing(body, p - 1);
        return Done(b, new ForStmt([], c.Condition, step, body), body);
    }

    /// <summary>
    ///     The exit label of a loop whose last unit is <paramref name="last"/>: a label just after it that
    ///     only <c>jmp</c>s inside the loop go to; the head's number minus one where there are several.
    /// </summary>
    private int? ExitOf(int head, int from, int last) {
        if (last + 1 >= _u.Count) return null;
        List<int> candidates = _u[last + 1].Labels
            .Where(l => Free(l) && AllFrom(l, from, last) && Jumps(l).All(j => S(j) is GotoStmt)).ToList();
        if (candidates.Count == 0) return null;
        return candidates.Contains(head - 1) ? head - 1 : candidates.Min();
    }

    // ----- switch --------------------------------------------------------------------------------

    private Built? TrySwitch(int i, int to, Ctx ctx) {
        if (S(i) is not AssignStmt { Target: RegExpr { Kind: RegKind.B }, Clamped: false } sel || S(i + 1) is not GotoStmt jc) return null;
        if (_u[i + 1].Labels.Count > 0 || _u[i + 1].Entries.Count > 0 || !Free(jc.Label) || Jumps(jc.Label).Count != 1) return null;
        int c = Pos(jc.Label);
        if (c <= i + 2 || c >= to || _u[c].Labels.Count != 1 || _u[c].Entries.Count > 0) return null;

        // The compare chain.
        List<(int Label, CaseLabel Case, int Skip)> cases = [];
        int k = c;
        int? pendingSkip = null, skipHere = null;
        while (k < to) {
            skipHere = pendingSkip;
            if (k > c && (_u[k].Entries.Count > 0 || _u[k].Labels.Any(l => l != pendingSkip))) break;
            if (k > c && pendingSkip is int ps && !_u[k].Labels.Contains(ps)) return null;
            pendingSkip = null;
            if (S(k) is CondGotoStmt { IfTrue: true, Condition: BinaryExpr { Left: IntLit v, Right: RegExpr { Kind: RegKind.B } } cmp } jn
                && CompareCase(cmp.Op, v.Value) is CaseLabel cl) {
                cases.Add((jn.Label, cl, -1));
                k++;
                continue;
            }
            if (S(k) is CondGotoStmt { IfTrue: false, Condition: BinaryExpr { Op: Opcodes.Ge, Left: RegExpr { Kind: RegKind.B }, Right: IntLit lo } } skip
                && S(k + 1) is CondGotoStmt { IfTrue: true, Condition: BinaryExpr { Op: Opcodes.Le, Left: RegExpr { Kind: RegKind.B }, Right: IntLit hi } } hit
                && k + 2 <= to && _u[k + 1].Labels.Count == 0 && _u[k + 1].Entries.Count == 0
                && Free(skip.Label) && Pos(skip.Label) == k + 2 && Jumps(skip.Label).Count == 1) {
                cases.Add((hit.Label, new CaseLabel(CaseKind.Range, lo.Value, hi.Value), skip.Label));
                pendingSkip = skip.Label;
                k += 2;
                continue;
            }
            break;
        }
        if (pendingSkip is not null && Pos(pendingSkip.Value) != k) return null;
        // The jump to the default: after the compares, or the whole chain (`switch (x) { default: … }`).
        int? def = null;
        if (k < to && S(k) is GotoStmt jd && pendingSkip is null && _u[k].Labels.All(l => l == skipHere || k == c && l == jc.Label) && _u[k].Entries.Count == 0
            && Pos(jd.Label) is int dp && dp >= i + 2 && dp < c && Free(jd.Label) && cases.All(x => x.Label != jd.Label)) {
            def = jd.Label;
            k++;
        }
        if (cases.Count == 0 && def is null) return null;
        int e = k;
        if (e > to) return null;

        // The sections: each case label once, in the bodies before the chain, in chain order.
        if (cases.Select(x => x.Label).Distinct().Count() != cases.Count) return null;
        foreach ((int label, CaseLabel cl, _) in cases) {
            int p = Pos(label);
            if (!Free(label) || p < i + 2 || p >= c || !AllFrom(label, i + 2, e)) return null;
            if (cl.Kind != CaseKind.Equal && Jumps(label).Count != 1) return null;
        }
        if (def is int d && !AllFrom(d, i + 2, e)) return null;
        List<(int Pos, int Order, int Label, CaseLabel? Case)> starts = [
            .. cases.Select((x, n) => (Pos(x.Label), n, x.Label, (CaseLabel?)x.Case)),
            .. def is int dd ? [(Pos(dd), cases.Count, dd, (CaseLabel?)null)] : Array.Empty<(int, int, int, CaseLabel?)>(),
        ];
        starts.Sort((a, b) => a.Pos != b.Pos ? a.Pos.CompareTo(b.Pos) : a.Order.CompareTo(b.Order));
        if (starts[0].Pos != i + 2) return null;
        List<int> chainOrder = starts.Where(s => s.Case is not null).Select(s => s.Order).ToList();
        if (!chainOrder.SequenceEqual(Enumerable.Range(0, cases.Count))) return null;
        if (Subsumed(starts.Select(s => s.Case).ToList())) return null;

        // The break label: after the chain, only jumps from the bodies.
        int? brk = null;
        if (e < _u.Count) {
            List<int> candidates = _u[e].Labels.Where(l => Free(l) && l != pendingSkip && AllFrom(l, i + 2, c) && Jumps(l).All(j => S(j) is GotoStmt)).ToList();
            if (candidates.Count > 0) brk = candidates.Contains(jc.Label + 1) ? jc.Label + 1 : candidates.Min();
        }

        Built b = Claim(Construct.Switch, i, e, [jc.Label, brk, .. cases.Select(x => (int?)x.Label), def, .. cases.Where(x => x.Skip >= 0).Select(x => (int?)x.Skip)]);
        Dictionary<int, CaseLabel?> map = cases.ToDictionary(x => x.Label, x => (CaseLabel?)x.Case);
        if (def is int dl) map[dl] = null;
        Ctx inner = new(brk, ctx.Continue, map);
        List<SwitchSection> sections = [];
        List<IReadOnlyList<Stmt>> blocks = [];
        for (int n = 0; n < starts.Count; n++) {
            int end = n + 1 < starts.Count ? starts[n + 1].Pos : c;
            List<Stmt> body = Parse(starts[n].Pos, end, inner);
            if (Flow.EndReachable(body)) {
                if (n + 1 >= starts.Count) {
                    // The last body runs into the chain: no C# for that.
                    _disabled.Add((i, Construct.Switch));
                    Fail = true;
                }
                else {
                    CaseLabel? next = starts[n + 1].Case;
                    if (next is not null && next.Kind != CaseKind.Equal) {
                        _disabled.Add((i, Construct.Switch));
                        Fail = true;
                    }
                    body.Add(new GotoCaseStmt(next?.Value));
                }
            }
            sections.Add(new SwitchSection(starts[n].Case, body));
            blocks.Add(body);
        }
        return Done(b, new SwitchStmt(sel.Value, sections), [.. blocks]);
    }

    /// <summary>Set when a construct was made that cannot stand; it is now disabled and the method must be structured again.</summary>
    public bool Fail { get; private set; }

    private static CaseLabel? CompareCase(byte op, int v) => op switch {
        Opcodes.Eq => new CaseLabel(CaseKind.Equal, v),
        Opcodes.Gt => new CaseLabel(CaseKind.Less, v),
        Opcodes.Ge => new CaseLabel(CaseKind.LessOrEqual, v),
        Opcodes.Lt => new CaseLabel(CaseKind.Greater, v),
        Opcodes.Le => new CaseLabel(CaseKind.GreaterOrEqual, v),
        _ => null,
    };

    /// <summary>Whether a case matches only values earlier cases already match (C# errors CS8120, CS0152).</summary>
    private static bool Subsumed(List<CaseLabel?> cases) {
        List<(long Lo, long Hi)> seen = [];
        foreach (CaseLabel? c in cases) {
            if (c is null) continue;
            (long lo, long hi) = c.Interval;
            if (lo > hi) return true;
            // Is [lo, hi] covered by the union of what came before?
            long at = lo;
            foreach ((long a, long b) in seen.OrderBy(s => s.Lo)) {
                if (a > at) break;
                if (b >= at) at = b + 1;
                if (at > hi) return true;
            }
            seen.Add((lo, hi));
        }
        return false;
    }
}

/// <summary>Reachability as C# sees it, on the safe side: where unsure, an end point is reachable.</summary>
internal static class Flow {
    public static bool EndReachable(IReadOnlyList<Stmt> list) {
        bool reachable = true;
        foreach (Stmt s in list) {
            if (s is LabelStmt) reachable = true;
            else if (reachable) reachable = EndReachable(s);
        }
        return reachable;
    }

    private static bool EndReachable(Stmt s) => s switch {
        GotoStmt or BreakStmt or ContinueStmt or GotoCaseStmt => false,
        OpStmt { Op: Opcodes.End or Opcodes.Ret } => false,
        IfStmt { Condition: null, Else: null } => true,
        IfStmt { Condition: null } i => EndReachable(i.Else!),
        IfStmt { Else: null } => true,
        IfStmt i => EndReachable(i.Then) || EndReachable(i.Else!),
        WhileStmt { Condition: null } w => Breaks(w.Body),
        ForStmt { Condition: null } f => Breaks(f.Body),
        DoStmt d => Breaks(d.Body) || EndReachable(d.Body),
        SwitchStmt sw => sw.Sections.All(x => x.Label is not null) || sw.Sections.Any(x => Breaks(x.Body)),
        _ => true,
    };

    /// <summary>Whether a <c>break</c> leaves the construct whose body this is (not one of an inner loop or switch).</summary>
    public static bool Breaks(IReadOnlyList<Stmt> list) => list.Any(s => s switch {
        BreakStmt => true,
        IfStmt i => Breaks(i.Then) || i.Else is not null && Breaks(i.Else),
        _ => false,
    });
}
