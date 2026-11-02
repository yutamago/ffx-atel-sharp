// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Bytecode.Code;

// Structured control flow (Phase 4): the statements that hold other statements, and the jumps
// that leave them. FlowEmitter lowers them as the original compiler did (docs/corpus.md,
// "Control flow"): `popa_jz` forward, `popa_jnz` back, and labels numbered per construct as it
// starts.

/// <summary><c>if (c) { … } else { … }</c>; a null <see cref="Condition"/> is <c>if (false)</c>, the dead code the original kept.</summary>
public sealed record IfStmt(Expr? Condition, IReadOnlyList<Stmt> Then, IReadOnlyList<Stmt>? Else) : Stmt;

/// <summary><c>while (c) { … }</c>; a null <see cref="Condition"/> is <c>while (true)</c>.</summary>
public sealed record WhileStmt(Expr? Condition, IReadOnlyList<Stmt> Body) : Stmt;

/// <summary><c>do { … } while (c);</c></summary>
public sealed record DoStmt(IReadOnlyList<Stmt> Body, Expr Condition) : Stmt;

/// <summary>
///     <c>for (init; c; step) { … }</c>: the original compiler put the step before the body (see
///     <see cref="FlowEmitter"/>); <c>continue</c> goes to the step.
/// </summary>
public sealed record ForStmt(IReadOnlyList<Stmt> Init, Expr? Condition, IReadOnlyList<Stmt> Step, IReadOnlyList<Stmt> Body) : Stmt;

/// <summary>What a <c>case</c> compares register B with.</summary>
public enum CaseKind {
    /// <summary><c>case k:</c> — <c>push k; pushb; eq</c>.</summary>
    Equal,
    /// <summary><c>case &lt; k:</c> — <c>push k; pushb; gt</c>.</summary>
    Less,
    /// <summary><c>case &lt;= k:</c> — <c>push k; pushb; ge</c>.</summary>
    LessOrEqual,
    /// <summary><c>case &gt; k:</c> — <c>push k; pushb; lt</c>.</summary>
    Greater,
    /// <summary><c>case &gt;= k:</c> — <c>push k; pushb; le</c>.</summary>
    GreaterOrEqual,
    /// <summary><c>case &gt;= lo and &lt;= hi:</c> — <c>pushb; push lo; ge; popa_jz next; pushb; push hi; le; popa_jnz case; next:</c>.</summary>
    Range,
}

/// <summary>A <c>case</c> label: its compare and constants (<see cref="High"/> for a range).</summary>
public sealed record CaseLabel(CaseKind Kind, int Value, int High = 0) {
    /// <summary>The values it matches, as an interval.</summary>
    public (long Lo, long Hi) Interval => Kind switch {
        CaseKind.Equal => (Value, Value),
        CaseKind.Less => (int.MinValue, (long)Value - 1),
        CaseKind.LessOrEqual => (int.MinValue, Value),
        CaseKind.Greater => ((long)Value + 1, int.MaxValue),
        CaseKind.GreaterOrEqual => (Value, int.MaxValue),
        _ => (Value, High),
    };
}

/// <summary>A switch section: its label (null: <c>default</c>) and body.</summary>
public sealed record SwitchSection(CaseLabel? Label, IReadOnlyList<Stmt> Body);

/// <summary>
///     <c>switch (selector) { … }</c>: the selector into register B, a jump to the compare chain, the
///     sections in source order, then the chain (the cases in source order, a jump to the default
///     if there is one) and the break label.
/// </summary>
public sealed record SwitchStmt(Expr Selector, IReadOnlyList<SwitchSection> Sections) : Stmt;

/// <summary><c>break;</c>: a jump to the innermost loop's or switch's end.</summary>
public sealed record BreakStmt : Stmt;

/// <summary><c>continue;</c>: a jump to the innermost loop's head (a <c>for</c>'s step).</summary>
public sealed record ContinueStmt : Stmt;

/// <summary>
///     <c>goto case k;</c> (<see cref="Case"/> the constant) or <c>goto default;</c> (null). As the last
///     statement of a section, to the next section, it is the original's fall-through: no code.
/// </summary>
public sealed record GotoCaseStmt(int? Case) : Stmt;

/// <summary>A source label <c>lN:</c>, for the jumps that stay <c>goto</c>.</summary>
public sealed record LabelStmt(int Label) : Stmt;

/// <summary><c>Entry(n);</c>: entry point <c>n</c> starts here, inside a method that holds several entries' code.</summary>
public sealed record EntryStmt(int Entry) : Stmt;

/// <summary><c>Asm("""…""")</c>: assembly lines, whose labels and jumps use the worker's label numbers.</summary>
public sealed record AsmStmt(IReadOnlyList<AsmLine> Lines) : Stmt;

/// <summary>A jump label of the emitter: fixed by its number (<c>lN</c>), or numbered at the end.</summary>
public sealed class FlowLabel {
    internal FlowLabel(int seq, int? number, int method, object? tag) {
        Tag = tag;
        Seq = seq;
        Fixed = number;
        Method = method;
        Number = number ?? -1;
    }

    /// <summary>The <see cref="FlowItem.Tag"/> of the statement that made it.</summary>
    public object? Tag { get; }

    /// <summary>Allocation order in the worker.</summary>
    public int Seq { get; }

    /// <summary>The number of a source label <c>lN</c>.</summary>
    public int? Fixed { get; }

    /// <summary>The method (in declaration order) whose construct made it.</summary>
    public int Method { get; }

    /// <summary>Its number in the label table; -1 until <see cref="FlowEmitter.Number"/> (and for labels nothing jumps to).</summary>
    public int Number { get; internal set; }

    /// <summary>Whether a jump goes to it: only those labels are in the table.</summary>
    public bool Used { get; internal set; }

    /// <summary>Whether it is placed.</summary>
    public bool Placed { get; internal set; }
}

/// <summary>Statements the emitter cannot lower, with the <see cref="FlowItem.Tag"/> of the one at fault.</summary>
public sealed class FlowException(string message, object? tag) : ArgumentException(message) {
    public object? Tag { get; } = tag;
}

/// <summary>What a <see cref="FlowItem"/> is.</summary>
public enum FlowItemKind { Instruction, Label, Entry }

/// <summary>
///     An instruction (<see cref="Target"/> for a jump), a label placement or an entry placement, with
///     <see cref="Tag"/> naming the statement it came from (the compiler's source location).
/// </summary>
public readonly record struct FlowItem(FlowItemKind Kind, CodeOp Op, FlowLabel? Target, int Entry, object? Tag);

/// <summary>
///     Lowers a worker's methods, in order, into instructions, labels and entries. Labels are numbered
///     afterwards (<see cref="Number"/>): a construct allocates its labels as it starts (<c>if</c>: the
///     else or end label, and the end label of an <c>if</c>/<c>else</c> when the else starts;
///     <c>while</c>: exit, head; <c>do</c>: exit, head; <c>for</c>: exit, body, head, step; <c>switch</c>:
///     chain and break, then each section's label as it starts, the range skips with the chain), and the labels something jumps to are
///     numbered in that order, skipping the numbers of source labels <c>lN</c>. A method's pin
///     (<c>[Labels(…)]</c>) gives its labels' numbers instead.
/// </summary>
public sealed class FlowEmitter {
    /// <summary>A loop being lowered; an exit label that nothing needs at the start is made at the first <c>break</c>.</summary>
    private sealed class Loop(Func<FlowLabel> exit, FlowLabel? @continue) {
        private FlowLabel? _exit;

        public FlowLabel Break => _exit ??= exit();

        public FlowLabel? Made => _exit;

        public FlowLabel? Continue => @continue;
    }

    /// <summary>A switch being lowered: its break label and its sections' labels, made when first needed.</summary>
    private sealed record Switch(FlowLabel Break, Func<SwitchSection, FlowLabel> LabelOf, IReadOnlyList<SwitchSection> Sections);

    private readonly List<FlowLabel> _labels = [];
    private readonly Dictionary<int, FlowLabel> _fixed = [];
    private readonly List<object> _scopes = [];
    private readonly List<IReadOnlyList<int>?> _pins = [];
    private int _method = -1;

    public List<FlowItem> Items { get; } = [];

    /// <summary>The statement being lowered, for <see cref="FlowItem.Tag"/>.</summary>
    public Func<Stmt, object?>? TagOf { get; init; }

    private object? _tag;

    /// <summary>Starts the next method; <paramref name="pin"/> numbers its labels.</summary>
    public void BeginMethod(IReadOnlyList<int>? pin = null) {
        _method++;
        _pins.Add(pin);
    }

    public void Entry(int number) => Items.Add(new FlowItem(FlowItemKind.Entry, default, null, number, _tag));

    private FlowLabel New() {
        FlowLabel l = new(_labels.Count, null, _method, _tag);
        _labels.Add(l);
        return l;
    }

    private FlowLabel Fixed(int n) {
        if (!_fixed.TryGetValue(n, out FlowLabel? l)) {
            l = new FlowLabel(_labels.Count, n, _method, _tag);
            _labels.Add(l);
            _fixed[n] = l;
        }
        return l;
    }

    private void Place(FlowLabel l) {
        if (l.Placed) throw new ArgumentException(l.Fixed is int n ? $"label l{n} is placed twice" : "a label is placed twice");
        l.Placed = true;
        Items.Add(new FlowItem(FlowItemKind.Label, default, l, 0, _tag));
    }

    private void Jump(byte op, FlowLabel l) {
        l.Used = true;
        Items.Add(new FlowItem(FlowItemKind.Instruction, CodeOp.Of(op, 0), l, 0, _tag));
    }

    private void Ops(List<CodeOp> ops) {
        foreach (CodeOp op in ops) Items.Add(new FlowItem(FlowItemKind.Instruction, op, null, 0, _tag));
    }

    private void Expr(Expr e) {
        List<CodeOp> o = [];
        Lowering.Lower(e, o);
        Ops(o);
    }

    public void Block(IReadOnlyList<Stmt> body) {
        for (int k = 0; k < body.Count; k++) {
            // A section's last `goto case` to the next section is the fall-through: no code.
            if (body[k] is GotoCaseStmt g && k == body.Count - 1 && FallsInto(body, g)) continue;
            Statement(body[k]);
        }
    }

    private SwitchSection? _sectionNext;
    private IReadOnlyList<Stmt>? _sectionBody;

    private bool FallsInto(IReadOnlyList<Stmt> body, GotoCaseStmt g) =>
        ReferenceEquals(body, _sectionBody) && _sectionNext is SwitchSection next
        && (g.Case is int k ? next.Label is { Kind: CaseKind.Equal } c && c.Value == k : next.Label is null);

    public void Statement(Stmt s) {
        object? outer = _tag;
        if (TagOf?.Invoke(s) is object t) _tag = t;
        try {
            Lower(s);
        }
        catch (ArgumentException ex) when (ex is not FlowException) {
            throw new FlowException(ex.Message, _tag);
        }
        finally {
            _tag = outer;
        }
    }

    private void Lower(Stmt s) {
        switch (s) {
            case IfStmt i: {
                FlowLabel otherwise = New();
                if (i.Condition is null) Jump(Opcodes.Jmp, otherwise);
                else {
                    Expr(i.Condition);
                    Jump(Opcodes.PopAJz, otherwise);
                }
                Block(i.Then);
                if (i.Else is null) {
                    Place(otherwise);
                    break;
                }
                FlowLabel end = New();
                Jump(Opcodes.Jmp, end);
                Place(otherwise);
                Block(i.Else);
                Place(end);
                break;
            }
            case WhileStmt w: {
                // exit, head (also for while (true), where only a break needs the exit).
                FlowLabel exit = New(), head = New();
                Loop loop = new(() => exit, head);
                Place(head);
                if (w.Condition is not null) {
                    Expr(w.Condition);
                    Jump(Opcodes.PopAJz, loop.Break);
                }
                InScope(loop, () => Block(w.Body));
                Jump(Opcodes.Jmp, head);
                if (loop.Made is FlowLabel made) Place(made);
                break;
            }
            case ForStmt f: {
                // The original's layout puts the step before the body:
                // head: c; popa_jz exit; jmp body; step: …; jmp head; body: …; jmp step; exit:
                Block(f.Init);
                FlowLabel exit = New(), body = New(), head = New(), step = New();
                Place(head);
                if (f.Condition is not null) {
                    Expr(f.Condition);
                    Jump(Opcodes.PopAJz, exit);
                }
                Jump(Opcodes.Jmp, body);
                Place(step);
                Block(f.Step);
                Jump(Opcodes.Jmp, head);
                Place(body);
                InScope(new Loop(() => exit, step), () => Block(f.Body));
                Jump(Opcodes.Jmp, step);
                Place(exit);
                break;
            }
            case DoStmt d: {
                // exit, head, as while.
                FlowLabel exit = New(), head = New();
                Loop loop = new(() => exit, null);
                Place(head);
                InScope(loop, () => Block(d.Body));
                Expr(d.Condition);
                Jump(Opcodes.PopAJnz, head);
                if (loop.Made is FlowLabel made) Place(made);
                break;
            }
            case SwitchStmt sw:
                LowerSwitch(sw);
                break;
            case BreakStmt:
                Jump(Opcodes.Jmp, Innermost(breaks: true) ?? throw new ArgumentException("break outside a loop or switch"));
                break;
            case ContinueStmt:
                Jump(Opcodes.Jmp, Innermost(breaks: false) ?? throw new ArgumentException("continue outside a loop that has one (do … while has none)"));
                break;
            case GotoCaseStmt g: {
                Switch sw = _scopes.OfType<Switch>().LastOrDefault() ?? throw new ArgumentException("goto case outside a switch");
                SwitchSection section = g.Case is int k
                    ? sw.Sections.FirstOrDefault(x => x.Label is { Kind: CaseKind.Equal } c && c.Value == k) ?? throw new ArgumentException($"the switch has no case {k}")
                    : sw.Sections.FirstOrDefault(x => x.Label is null) ?? throw new ArgumentException("the switch has no default");
                Jump(Opcodes.Jmp, sw.LabelOf(section));
                break;
            }
            case LabelStmt l:
                Place(Fixed(l.Label));
                break;
            case GotoStmt g:
                Jump(Opcodes.Jmp, Fixed(g.Label));
                break;
            case CondGotoStmt g:
                Expr(g.Condition);
                Jump(g.IfTrue ? Opcodes.PopAJnz : Opcodes.PopAJz, Fixed(g.Label));
                break;
            case EntryStmt e:
                Entry(e.Entry);
                break;
            case AsmStmt a:
                foreach (AsmLine line in a.Lines) {
                    switch (line.Kind) {
                        case AsmLineKind.Label: Place(Fixed(line.Number)); break;
                        case AsmLineKind.Entry: Entry(line.Number); break;
                        case AsmLineKind.Instruction when StackEffects.Jumps(line.Instr.Code) && line.Instr.Operand is ushort n:
                            Jump(line.Instr.Code, Fixed(n));
                            break;
                        case AsmLineKind.Instruction:
                            Items.Add(new FlowItem(FlowItemKind.Instruction, new CodeOp(line.Instr.Code, line.Instr.Operand), null, 0, _tag));
                            break;
                        default: throw new ArgumentException("`worker` inside an entry");
                    }
                }
                break;
            default:
                Ops(Lowering.Lower(s));
                break;
        }
    }

    private FlowLabel? Innermost(bool breaks) {
        for (int k = _scopes.Count - 1; k >= 0; k--) {
            switch (_scopes[k]) {
                case Loop l: return breaks ? l.Break : l.Continue;
                case Switch sw when breaks: return sw.Break;
            }
        }
        return null;
    }

    private void InScope(object scope, Action body) {
        _scopes.Add(scope);
        try {
            body();
        }
        finally {
            _scopes.RemoveAt(_scopes.Count - 1);
        }
    }

    private void LowerSwitch(SwitchStmt sw) {
        // The chain and break labels at the start; each section's label when it starts (or a goto
        // case needs it first); the range skips with the chain.
        FlowLabel chain = New(), brk = New();
        if (sw.Sections.Count(x => x.Label is null) > 1) throw new ArgumentException("a switch has one default");
        Dictionary<SwitchSection, FlowLabel> labels = new(ReferenceEqualityComparer.Instance);
        FlowLabel LabelOf(SwitchSection x) => labels.TryGetValue(x, out FlowLabel? l) ? l : labels[x] = New();
        List<SwitchSection> cases = sw.Sections.Where(x => x.Label is not null).ToList();
        SwitchSection? def = sw.Sections.FirstOrDefault(x => x.Label is null);

        Expr(sw.Selector);
        Items.Add(new FlowItem(FlowItemKind.Instruction, CodeOp.Of(Opcodes.PopB), null, 0, _tag));
        Jump(Opcodes.Jmp, chain);
        Switch scope = new(brk, LabelOf, sw.Sections);
        InScope(scope, () => {
            for (int k = 0; k < sw.Sections.Count; k++) {
                SwitchSection section = sw.Sections[k];
                Place(LabelOf(section));
                (SwitchSection? next, IReadOnlyList<Stmt>? body) = (_sectionNext, _sectionBody);
                _sectionNext = k + 1 < sw.Sections.Count ? sw.Sections[k + 1] : null;
                _sectionBody = section.Body;
                try {
                    Block(section.Body);
                }
                finally {
                    (_sectionNext, _sectionBody) = (next, body);
                }
            }
        });
        Place(chain);
        Dictionary<SwitchSection, FlowLabel> skips = new(ReferenceEqualityComparer.Instance);
        foreach (SwitchSection section in cases.Where(c => c.Label!.Kind == CaseKind.Range)) skips[section] = New();
        foreach (SwitchSection section in cases) {
            CaseLabel c = section.Label!;
            FlowLabel label = LabelOf(section);
            RegExpr b = new(RegKind.B);
            if (c.Kind == CaseKind.Range) {
                Expr(new BinaryExpr(Opcodes.Ge, b, new IntLit(c.Value)));
                Jump(Opcodes.PopAJz, skips[section]);
                Expr(new BinaryExpr(Opcodes.Le, b, new IntLit(c.High)));
                Jump(Opcodes.PopAJnz, label);
                Place(skips[section]);
                continue;
            }
            byte op = c.Kind switch {
                CaseKind.Equal => Opcodes.Eq, CaseKind.Less => Opcodes.Gt, CaseKind.LessOrEqual => Opcodes.Ge,
                CaseKind.Greater => Opcodes.Lt, _ => Opcodes.Le,
            };
            Expr(new BinaryExpr(op, new IntLit(c.Value), b));
            Jump(Opcodes.PopAJnz, label);
        }
        if (def is not null) Jump(Opcodes.Jmp, LabelOf(def));
        Place(brk);
    }

    /// <summary>
    ///     Numbers the labels (see the class remarks) and checks that every used label is placed.
    ///     Returns the label table's size.
    /// </summary>
    public int Number() {
        HashSet<int> taken = [.. _fixed.Keys];
        int next = 0;
        for (int m = 0; m <= _method; m++) {
            List<FlowLabel> own = _labels.Where(l => l.Method == m && l.Fixed is null && l.Used).ToList();
            if (m < _pins.Count && _pins[m] is IReadOnlyList<int> pin) {
                if (pin.Count != own.Count) throw new ArgumentException($"[Labels] lists {pin.Count} numbers; the method's constructs have {own.Count} labels");
                for (int k = 0; k < own.Count; k++) own[k].Number = pin[k];
                if (pin.Count > 0) next = Math.Max(next, pin.Max() + 1);
                continue;
            }
            foreach (FlowLabel l in own) {
                while (taken.Contains(next)) next++;
                l.Number = next++;
            }
        }
        foreach (FlowLabel l in _labels) {
            if (l.Used && !l.Placed) throw new ArgumentException(l.Fixed is int n ? $"label l{n} is not placed" : "a jump to a label that is not placed");
        }
        List<FlowLabel> table = _labels.Where(l => l.Used || l.Fixed is not null).ToList();
        if (table.GroupBy(l => l.Number).FirstOrDefault(g => g.Count() > 1) is { } dup) throw new ArgumentException($"label number {dup.Key} is given twice");
        return table.Count == 0 ? 0 : table.Max(l => l.Number) + 1;
    }

    /// <summary>The labels of method <paramref name="m"/> that <see cref="Number"/> numbers by the rule or a pin, in allocation order.</summary>
    public IEnumerable<FlowLabel> MethodLabels(int m) => _labels.Where(l => l.Method == m && l.Fixed is null && l.Used);
}
