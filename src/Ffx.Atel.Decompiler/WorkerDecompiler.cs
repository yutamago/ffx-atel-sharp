// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler;

/// <summary>What structuring a worker made: constructs by kind, what stayed <c>goto</c>, pins and fallbacks.</summary>
internal sealed class FlowStats {
    public Dictionary<string, int> Constructs { get; } = [];

    /// <summary>Dead blocks written with a name (<c>if (Cut.…)</c>), by name.</summary>
    public Dictionary<string, int> Cuts { get; } = [];

    /// <summary>The signatures of the named dead blocks (<see cref="Layout.DeadSignature"/>).</summary>
    public HashSet<string> CutSignatures { get; } = new(StringComparer.Ordinal);

    public int Gotos, LabelPins, Fallbacks, MergedMethods, FlatWorkers;
}

/// <summary>
///     One worker's code as entry methods of structured statements (Phases 3 and 4): statements that
///     lift, lower back to their own bytes and have a C# form are C#, the rest <c>Asm</c>; jumps become
///     <c>if</c>, loops and <c>switch</c> where lowering the construct gives the original back
///     (<see cref="Structurer"/>), else <c>goto</c>. Methods that jump into each other's code are one
///     method with <c>Entry(n);</c> where each further entry starts.
/// </summary>
internal sealed partial class WorkerDecompiler {
    private sealed class Method {
        public required List<int> Entries;
        public required List<Unit> Units;
        public List<Stmt> Body = [];
        public IReadOnlyList<int>? Pin;
        public List<Built> Built = [];
        public Dictionary<object, Built> ByNode = new(ReferenceEqualityComparer.Instance);
        public HashSet<(int, Construct)> Disabled = [];
        public bool Flat;
    }

    private readonly ScriptBlock _block;
    private readonly int _worker;
    private readonly List<Instr> _code;
    private List<Method> _methods = [];
    private readonly StatementLifter _lifter;
    private readonly StatementPrinter _printer;
    private readonly bool _functionWorker;
    private readonly Dictionary<uint, int> _indexOf = [];
    private readonly Layout.CutNames _cuts;

    public FlowStats Flow { get; } = new();

    /// <summary>Methods written as <c>Init</c> or <c>Main</c> overrides (entries 0 and 1).</summary>
    public int Overrides { get; private set; }

    /// <summary>Names the written statements placed, by domain (counted while <see cref="WriteMethods"/> runs).</summary>
    public IReadOnlyDictionary<string, int> Names => _printer.Names;

    /// <summary>Why constructs were dropped, over the process: "reason kind" to count.</summary>
    public static System.Collections.Concurrent.ConcurrentDictionary<string, int> Reasons { get; } = new(StringComparer.Ordinal);

    private static void Reason(string why, Built b) => Reasons.AddOrUpdate($"{why} {b.Kind}", 1, (_, v) => v + 1);

    /// <param name="cuts">Names for dead blocks (<c>data/cut-names.toml</c> by default).</param>
    public WorkerDecompiler(ScriptBlock block, int worker, VarInfo[] vars, IReadOnlyList<VarEntry> table, NativeSignatures natives, string[] workerNames, Layout.CutNames? cuts = null) {
        _block = block;
        _cuts = cuts ?? Layout.CutNames.Default;
        _worker = worker;
        Bytecode.Script.Worker w = block.Workers[worker];
        _functionWorker = w.Type == 0;
        _code = block.Decode(w);
        _lifter = new StatementLifter(block, worker, table, natives);
        _printer = new StatementPrinter(vars, natives, workerNames, _functionWorker) { Self = worker };

        for (int i = 0; i < _code.Count; i++) _indexOf[_code[i].Offset] = i;
        List<int>[] labelsAt = new List<int>[_code.Count];
        bool[] boundary = new bool[_code.Count];
        for (int l = 0; l < w.Labels.Length; l++) {
            int i = _indexOf[w.Labels[l]];
            (labelsAt[i] ??= []).Add(l);
            boundary[i] = true;
        }
        foreach (uint e in w.Entries) boundary[_indexOf[e]] = true;

        // One method per distinct entry offset, in code order; each runs to the next.
        List<uint> starts = w.Entries.Distinct().Order().ToList();
        for (int m = 0; m < starts.Count; m++) {
            int from = _indexOf[starts[m]], to = m + 1 < starts.Count ? _indexOf[starts[m + 1]] : _code.Count;
            List<int> entries = Enumerable.Range(0, w.Entries.Length).Where(e => w.Entries[e] == starts[m]).ToList();
            List<Unit> units = [];
            List<(int Start, int End)>? split = _lifter.Split(_code, from, to, i => boundary[i]);
            foreach ((int s, int e) in split ?? [.. Enumerable.Range(from, to - from).Select(i => (i, i + 1))]) {
                Unit unit = new() { Start = s, End = e };
                if (labelsAt[s] is List<int> ls) unit.Labels.AddRange(ls);
                if (StackEffects.Jumps(_code[e - 1].Code) && _code[e - 1].Operand is ushort target) unit.Jump = target;
                if (split is not null && _lifter.Lift(_code, s, e) is Stmt stmt) {
                    try {
                        _printer.Statement(stmt);
                        unit.Stmt = stmt;
                    }
                    catch (NotPrintableException) {
                    }
                }
                units.Add(unit);
            }
            _methods.Add(new Method { Entries = entries, Units = units });
        }
    }

    private IEnumerable<Unit> Units => _methods.SelectMany(m => m.Units);

    // ----- Structuring ---------------------------------------------------------------------------

    /// <summary>Merges methods that jump into each other, structures each, and checks the lowering against the original.</summary>
    public void Structure() {
        MergeMethods();
        foreach (Method m in _methods) StructureMethod(m);
        for (int round = 0; ; round++) {
            List<(Built Built, Method Where)>? culprits = Culprits();
            if (culprits is null) break;
            if (culprits.Count == 0 || round >= 1000) {
                // A mismatch no construct accounts for: the whole worker as goto.
                foreach (Method m in _methods) {
                    m.Flat = true;
                    StructureMethod(m);
                }
                Flow.FlatWorkers++;
                break;
            }
            foreach ((Built c, Method where) in culprits) {
                Reason(_shapeWhy, c);
                where.Disabled.Add((c.Start, c.Kind));
            }
            foreach (Method m in culprits.Select(c => c.Where).Distinct()) StructureMethod(m);
        }
        Pin();
        if (!Verify()) {
            foreach (Method m in _methods) {
                m.Flat = true;
                m.Pin = null;
                StructureMethod(m);
            }
            Flow.FlatWorkers++;
            Pin();
            if (!Verify()) throw new InvalidOperationException($"worker {_worker}: the goto form does not give the original back");
        }
        foreach (Method m in _methods) {
            Flow.Fallbacks += m.Disabled.Count;
            foreach (Built b in m.Built) {
                string k = b.Kind.ToString();
                Flow.Constructs[k] = Flow.Constructs.GetValueOrDefault(k) + 1;
                if (b.Node is IfStmt { Cut: not null }) Flow.CutSignatures.Add(DeadSignatureOf(m.Units, b.Start, DeadUnits(m, b).To));
            }
            if (m.Pin is not null) Flow.LabelPins++;
            Walk(m.Body, s => {
                if (s is GotoStmt or CondGotoStmt) Flow.Gotos++;
                if (s is IfStmt { Cut: string cut }) Flow.Cuts[cut] = Flow.Cuts.GetValueOrDefault(cut) + 1;
            });
        }
    }

    /// <summary>Joins the methods a jump crosses (C# has no goto between methods): the later entries become <c>Entry(n);</c>.</summary>
    private void MergeMethods() {
        Dictionary<int, int> methodOfLabel = [];
        for (int m = 0; m < _methods.Count; m++) {
            foreach (Unit u in _methods[m].Units) foreach (int l in u.Labels) methodOfLabel[l] = m;
        }
        int[] reach = Enumerable.Range(0, _methods.Count).ToArray();
        int[] low = Enumerable.Range(0, _methods.Count).ToArray();
        for (int m = 0; m < _methods.Count; m++) {
            foreach (Unit u in _methods[m].Units) {
                if (u.Jump is int l && methodOfLabel.TryGetValue(l, out int t) && t != m) {
                    int a = Math.Min(m, t), b = Math.Max(m, t);
                    reach[a] = Math.Max(reach[a], b);
                }
            }
        }
        List<Method> merged = [];
        for (int m = 0; m < _methods.Count;) {
            int end = reach[m];
            for (int k = m; k <= end; k++) end = Math.Max(end, reach[k]);
            Method first = _methods[m];
            for (int k = m + 1; k <= end; k++) {
                _methods[k].Units[0].Entries.AddRange(_methods[k].Entries);
                first.Units.AddRange(_methods[k].Units);
            }
            if (end > m) Flow.MergedMethods++;
            merged.Add(first);
            m = end + 1;
        }
        _methods = merged;
    }

    private void StructureMethod(Method m) {
        while (true) {
            Structurer st = new(m.Units, Asm, m.Flat ? AllOff(m) : m.Disabled, _cuts.Names.Count == 0 ? null : (jump, end) => _cuts.NameOf(DeadSignatureOf(m.Units, jump, end)));
            if (st.Fail && !m.Flat) continue;
            m.Body = st.Body;
            m.Built = st.Built;
            m.ByNode = st.ByNode;
            if (m.Flat) {
                m.Built = [];
                return;
            }
            List<(Built Built, string Why)> bad = Invalid(st);
            if (bad.Count > 0) {
                foreach ((Built c, string why) in bad) {
                    Reason(why, c);
                    m.Disabled.Add((c.Start, c.Kind));
                }
                continue;
            }
            return;
        }
    }

    private static HashSet<(int, Construct)> AllOff(Method m) {
        HashSet<(int, Construct)> all = [];
        for (int i = 0; i < m.Units.Count; i++) foreach (Construct k in Enum.GetValues<Construct>()) all.Add((i, k));
        return all;
    }

    private Stmt Asm(Unit u) => new AsmStmt([.. Enumerable.Range(u.Start, u.End - u.Start).Select(i => new AsmLine(AsmLineKind.Instruction, 0, _code[i]))]);

    /// <summary>
    ///     A construct that cannot stand as C#: one whose label a remaining <c>goto</c> needs, or one whose
    ///     block holds a label a <c>goto</c> from outside it jumps to.
    /// </summary>
    private static List<(Built Built, string Why)> Invalid(Structurer st) {
        List<(Built, string)> bad = [];
        HashSet<Built> seen = new(ReferenceEqualityComparer.Instance);
        void Bad(Built b, string why) {
            if (seen.Add(b)) bad.Add((b, why));
        }
        Dictionary<int, List<object>> labelPath = [];
        List<(int Label, List<object> Path)> jumps = [];
        void Block(IReadOnlyList<Stmt> list, List<object> path) {
            List<object> here = [.. path, list];
            foreach (Stmt s in list) {
                switch (s) {
                    case LabelStmt l: labelPath[l.Label] = here; break;
                    case GotoStmt g: jumps.Add((g.Label, here)); break;
                    case CondGotoStmt g: jumps.Add((g.Label, here)); break;
                    case AsmStmt a:
                        foreach (AsmLine line in a.Lines) {
                            if (StackEffects.Jumps(line.Instr.Code) && line.Instr.Operand is ushort n) jumps.Add((n, here));
                        }
                        break;
                    case IfStmt i:
                        Block(i.Then, here);
                        if (i.Else is not null) Block(i.Else, here);
                        break;
                    case WhileStmt w: Block(w.Body, here); break;
                    case ForStmt f: Block(f.Body, here); break;
                    case DoStmt d: Block(d.Body, here); break;
                    case SwitchStmt sw: foreach (SwitchSection x in sw.Sections) Block(x.Body, here); break;
                }
            }
        }
        Block(st.Body, []);
        foreach ((int label, List<object> path) in jumps) {
            if (st.Owner.TryGetValue(label, out Built? owner)) {
                Bad(owner, "goto-to-construct-label");
                continue;
            }
            if (!labelPath.TryGetValue(label, out List<object>? lp)) continue;
            for (int k = 0; k < lp.Count; k++) {
                if (k >= path.Count || !ReferenceEquals(lp[k], path[k])) {
                    Bad(st.ByBlock[lp[k]], "goto-into-block");
                    break;
                }
            }
        }
        return bad;
    }

    // ----- Checking the lowering -----------------------------------------------------------------

    private sealed record Lowered(List<FlowItem> Items, FlowEmitter Emitter, List<CodeOp> Ops, List<FlowLabel?> Targets, Dictionary<FlowLabel, int> LabelAt, List<(int Entry, int At)> Entries);

    private Lowered Lower(bool pins) {
        FlowEmitter em = new() { TagOf = s => s };
        foreach (Method m in _methods) {
            em.BeginMethod(pins ? m.Pin : null);
            foreach (int e in m.Entries) em.Entry(e);
            em.Block(m.Body);
        }
        List<CodeOp> ops = [];
        List<FlowLabel?> targets = [];
        Dictionary<FlowLabel, int> labelAt = new(ReferenceEqualityComparer.Instance);
        List<(int, int)> entries = [];
        foreach (FlowItem it in em.Items) {
            switch (it.Kind) {
                case FlowItemKind.Instruction: ops.Add(it.Op); targets.Add(it.Target); break;
                case FlowItemKind.Label: labelAt[it.Target!] = ops.Count; break;
                default: entries.Add((it.Entry, ops.Count)); break;
            }
        }
        return new Lowered(em.Items, em, ops, targets, labelAt, entries);
    }

    /// <summary>Whether a lowered instruction is instruction <paramref name="i"/>: a pool constant by value, or (from assembly) by index.</summary>
    private bool Same(CodeOp got, int i) => got.Pooled ? got == _lifter.Op(_code[i]) : got.Code == _code[i].Code && got.Operand == _code[i].Operand;

    private int LabelIndex(int label) => _indexOf[_block.Workers[_worker].Labels[label]];

    /// <summary>
    ///     Lowers every method and compares the code with the original, labels by position: the construct
    ///     where the first difference lies (and its method), or null when the shape is the original's.
    /// </summary>
    /// <summary>
    ///     Lowers every method and compares the code with the original, labels by position: null when the
    ///     shape is the original's, else the constructs where the differences lie (empty when none does).
    /// </summary>
    private List<(Built, Method)>? Culprits() {
        List<(Built, Method)> found = [];
        HashSet<Built> seen = new(ReferenceEqualityComparer.Instance);
        void Add(Built? b, Method? m) {
            if (b is not null && m is not null && seen.Add(b)) found.Add((b, m));
        }
        Lowered low;
        try {
            low = Lower(pins: false);
        }
        catch (ArgumentException ex) {
            _shapeWhy = "lowering-throws";
            Add(ex is FlowException { Tag: object tag } ? NodeOwner(tag, out Method? w) : Innermost(0, out w), w);
            return found;
        }
        _shapeWhy = "shape";
        int n = Math.Min(low.Ops.Count, _code.Count);
        for (int i = 0; i < n; i++) {
            CodeOp want = _lifter.Op(_code[i]);
            CodeOp got = low.Ops[i];
            bool equal = low.Targets[i] is FlowLabel t
                ? got.Code == want.Code && StackEffects.Jumps(want.Code) && want.Operand is ushort o && o < _block.Workers[_worker].Labels.Length
                  && low.LabelAt.TryGetValue(t, out int at) && at == LabelIndex(o)
                : Same(got, i);
            if (equal) continue;
            Add(Innermost(i, out Method? w), w);
            // Past a difference in length, everything after differs too.
            if (low.Ops.Count != _code.Count) return found;
        }
        if (low.Ops.Count != _code.Count) {
            Add(Innermost(n, out Method? w), w);
            return found;
        }
        if (found.Count > 0) return found;

        // Labels: each lowered label stands for exactly one original label, and the other way round.
        _shapeWhy = "label-merge";
        Dictionary<FlowLabel, int> number = new(ReferenceEqualityComparer.Instance);
        Dictionary<int, FlowLabel> byNumber = [];
        for (int i = 0; i < low.Ops.Count; i++) {
            if (low.Targets[i] is not FlowLabel t) continue;
            int o = _code[i].Operand!.Value;
            FlowLabel? other = byNumber.GetValueOrDefault(o);
            if (number.TryGetValue(t, out int had) && had != o || other is not null && !ReferenceEquals(other, t)) {
                Built? b = Owner(t, out Method? w) ?? Owner(other ?? t, out w) ?? Innermost(i, out w);
                Add(b, w);
                continue;
            }
            number[t] = o;
            byNumber[o] = t;
        }
        return found.Count > 0 ? found : null;
    }

    private Built? NodeOwner(object tag, out Method? where) {
        foreach (Method m in _methods) {
            if (m.ByNode.TryGetValue(tag, out Built? b)) {
                where = m;
                return b;
            }
        }
        return Innermost(0, out where);
    }

    private string _shapeWhy = "";

    private Built? Owner(FlowLabel l, out Method? where) {
        where = null;
        if (l.Fixed is not null) return null;
        foreach (Method m in _methods) {
            if (l.Tag is not null && m.ByNode.TryGetValue(l.Tag, out Built? b) && l.Fixed is null) {
                where = m;
                return b;
            }
        }
        where = null;
        return null;
    }

    /// <summary>The innermost construct whose code holds instruction <paramref name="index"/>.</summary>
    private Built? Innermost(int index, out Method? where) {
        where = null;
        Built? best = null;
        int bestSize = int.MaxValue;
        foreach (Method m in _methods) {
            foreach (Built b in m.Built) {
                int from = m.Units[b.Start].Start, to = b.End < m.Units.Count ? m.Units[b.End].Start : m.Units[^1].End;
                if (index >= from && index <= to && to - from < bestSize) {
                    best = b;
                    bestSize = to - from;
                    where = m;
                }
            }
        }
        if (best is null) where = _methods.FirstOrDefault(m => m.Built.Count > 0);
        return best ?? where?.Built[0];
    }

    /// <summary>Pins the label numbers of every method whose constructs the rule numbers otherwise.</summary>
    private void Pin() {
        Lowered low = Lower(pins: false);
        Dictionary<FlowLabel, int> numbers = OriginalNumbers(low);
        HashSet<int> taken = [.. low.Items.Where(i => i.Target?.Fixed is not null).Select(i => i.Target!.Fixed!.Value)];
        int next = 0;
        for (int m = 0; m < _methods.Count; m++) {
            List<FlowLabel> own = low.Emitter.MethodLabels(m).ToList();
            List<int> want = own.Select(l => numbers[l]).ToList();
            int probe = next;
            bool rule = true;
            foreach (int w in want) {
                while (taken.Contains(probe)) probe++;
                if (probe++ != w) rule = false;
            }
            _methods[m].Pin = rule ? null : want;
            next = rule ? probe : want.Count > 0 ? Math.Max(next, want.Max() + 1) : next;
        }
    }

    /// <summary>The original number of each lowered label: the operand of the original jump at the same place.</summary>
    private Dictionary<FlowLabel, int> OriginalNumbers(Lowered low) {
        Dictionary<FlowLabel, int> numbers = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < low.Ops.Count && i < _code.Count; i++) {
            if (low.Targets[i] is FlowLabel t && _code[i].Operand is ushort o) numbers[t] = o;
        }
        return numbers;
    }

    /// <summary>Whether the lowering with its pins gives the original code, label table and entries.</summary>
    private bool Verify() {
        Lowered low;
        try {
            low = Lower(pins: true);
            low.Emitter.Number();
        }
        catch (ArgumentException) {
            return false;
        }
        if (low.Ops.Count != _code.Count) return false;
        for (int i = 0; i < _code.Count; i++) {
            CodeOp got = low.Targets[i] is FlowLabel t ? low.Ops[i] with { Operand = (ushort)t.Number } : low.Ops[i];
            if (!Same(got, i)) return false;
        }
        Bytecode.Script.Worker w = _block.Workers[_worker];
        List<(FlowLabel Label, int At)> table = [.. low.LabelAt.Where(kv => kv.Key.Used || kv.Key.Fixed is not null).Select(kv => (kv.Key, kv.Value))];
        if (table.Count != w.Labels.Length) return false;
        foreach ((FlowLabel l, int at) in table) {
            if (l.Number < 0 || l.Number >= w.Labels.Length || LabelIndex(l.Number) != at) return false;
        }
        if (low.Entries.Count != w.Entries.Length) return false;
        foreach ((int e, int at) in low.Entries) {
            if (e < 0 || e >= w.Entries.Length || _indexOf[w.Entries[e]] != at) return false;
        }
        return true;
    }

    private static void Walk(IReadOnlyList<Stmt> list, Action<Stmt> visit) {
        foreach (Stmt s in list) {
            visit(s);
            switch (s) {
                case IfStmt i:
                    Walk(i.Then, visit);
                    if (i.Else is not null) Walk(i.Else, visit);
                    break;
                case WhileStmt w: Walk(w.Body, visit); break;
                case ForStmt f: Walk(f.Body, visit); break;
                case DoStmt d: Walk(d.Body, visit); break;
                case SwitchStmt sw: foreach (SwitchSection x in sw.Sections) Walk(x.Body, visit); break;
            }
        }
    }

    // ----- Facts for the script --------------------------------------------------------------

    /// <summary>The code with pool constants as values, for the pool rules.</summary>
    public List<CodeOp> Ops() => _code.Select(_lifter.Op).ToList();

    /// <summary>Whether code kept as assembly pushes an integer (<c>pushc</c>) or float (<c>pushfc</c>) constant: assembly names them by pool index.</summary>
    public (bool Ints, bool Floats) AsmUsesPools() {
        bool ints = false, floats = false;
        foreach (Unit u in Units.Where(u => u.Stmt is null)) {
            for (int i = u.Start; i < u.End; i++) {
                ints |= _code[i].Code == Opcodes.PushC;
                floats |= _code[i].Code == Opcodes.PushFC;
            }
        }
        return (ints, floats);
    }

    public int Statements => Units.Count(u => u.Stmt is not null);

    public int AsmInstructions => Units.Where(u => u.Stmt is null).Sum(u => u.End - u.Start);

    /// <summary>The integer and float registers the C# statements use.</summary>
    public SortedSet<(RegKind Kind, int Number)> Registers() {
        SortedSet<(RegKind Kind, int Number)> used = [];
        void Walk(Expr e) {
            switch (e) {
                case RegExpr { Kind: RegKind.Int or RegKind.Float } r: used.Add((r.Kind, r.Number)); break;
                case ElemExpr x: Walk(x.Index); break;
                case Elem2Expr x: Walk(x.Row); Walk(x.Col); break;
                case AddrExpr x: Walk(x.Element); break;
                case UnaryExpr x: Walk(x.Operand); break;
                case BinaryExpr x: Walk(x.Left); Walk(x.Right); break;
                case CallExpr x: foreach (Expr a in x.Args) Walk(a); break;
            }
        }
        foreach (Stmt s in Units.Select(u => u.Stmt).OfType<Stmt>()) {
            switch (s) {
                case AssignStmt a: Walk(a.Target); Walk(a.Value); break;
                case CompoundStmt c: Walk(c.Target); Walk(c.Value); break;
                case CallStmt c: foreach (Expr a in c.Args) Walk(a); break;
                case RequestStmt r: Walk(r.Level); Walk(r.Worker); Walk(r.Entry); break;
                case CondGotoStmt g: Walk(g.Condition); break;
                case EndWithStmt e: Walk(e.Value); break;
                case OpArgsStmt x: foreach (Expr a in x.Args) Walk(a); break;
            }
        }
        return used;
    }

    // ----- Writing -------------------------------------------------------------------------------

    private const string Indent = "    ";

    /// <summary>The entry methods; <paramref name="annotate"/> names a native in assembly comments.</summary>
    public void WriteMethods(StringBuilder sb, Func<ushort, string?> annotate) {
        HashSet<int> csharpTargets = [];
        foreach (Method m in _methods) {
            Walk(m.Body, s => {
                if (s is GotoStmt g) csharpTargets.Add(g.Label);
                if (s is CondGotoStmt c) csharpTargets.Add(c.Label);
            });
        }
        TreeWriter writer = new(sb, _printer, _block, _worker, annotate, csharpTargets);
        _printer.Counting = true;
        for (int m = 0; m < _methods.Count; m++) {
            Method method = _methods[m];
            if (m > 0) sb.Append('\n');
            MethodOffsets.Add(sb.Length);
            if (method.Pin is not null) sb.Append(Indent).Append("[Labels(").Append(string.Join(", ", method.Pin.Select(n => n.ToString(CultureInfo.InvariantCulture)))).Append(")]\n");
            WriteHeader(sb, method);
            sb.Append(Indent).Append("{\n");
            writer.Block(method.Body, 2);
            sb.Append(Indent).Append("}\n");
        }
    }
}

/// <summary>Writes structured statements as C#, indented.</summary>
internal sealed class TreeWriter(StringBuilder sb, StatementPrinter printer, ScriptBlock block, int worker, Func<ushort, string?> annotate, HashSet<int> csharpTargets) {
    private const string Unit = "    ";

    private static string Pad(int depth) => string.Concat(Enumerable.Repeat(Unit, depth));

    public void Block(IReadOnlyList<Stmt> list, int depth) {
        List<string> asm = [];
        string pad = Pad(depth);
        void Flush() {
            if (asm.Count == 0) return;
            if (asm.Count == 1 && !asm[0].Contains(';', StringComparison.Ordinal)) {
                sb.Append(pad).Append("Asm(\"").Append(asm[0].Trim()).Append("\");\n");
            }
            else {
                sb.Append(pad).Append("Asm(\"\"\"\n");
                foreach (string line in asm) sb.Append(pad).Append(line).Append('\n');
                sb.Append(pad).Append("\"\"\");\n");
            }
            asm.Clear();
        }
        for (int k = 0; k < list.Count; k++) {
            Stmt s = list[k];
            switch (s) {
                case LabelStmt l when !csharpTargets.Contains(l.Label):
                    asm.Add(string.Create(CultureInfo.InvariantCulture, $"l{l.Label}:"));
                    continue;
                case AsmStmt a:
                    foreach (AsmLine line in a.Lines) {
                        string text = Bytecode.Script.Assembly.Render(block, worker, line.Instr);
                        if (line.Instr.Opcode.Operand == OperandKind.Func && line.Instr.Operand is ushort id && annotate(id) is string native) {
                            text = $"{text,-24} ; {native}";
                        }
                        asm.Add(Unit + text);
                    }
                    continue;
            }
            Flush();
            switch (s) {
                case LabelStmt l: {
                    // A label needs a statement after it in its block.
                    bool last = list.Skip(k + 1).All(x => x is LabelStmt);
                    sb.Append(Pad(depth - 1)).Append(CultureInfo.InvariantCulture, $"l{l.Label}:").Append(last ? " ;\n" : "\n");
                    break;
                }
                case IfStmt i:
                    sb.Append(pad).Append("if (").Append(ConditionText(i)).Append(")\n");
                    Braced(i.Then, depth);
                    IReadOnlyList<Stmt>? other = i.Else;
                    while (other is [IfStmt elseIf]) {
                        sb.Append(pad).Append("else if (").Append(ConditionText(elseIf)).Append(")\n");
                        Braced(elseIf.Then, depth);
                        other = elseIf.Else;
                    }
                    if (other is not null) {
                        sb.Append(pad).Append("else\n");
                        Braced(other, depth);
                    }
                    break;
                case WhileStmt w:
                    sb.Append(pad).Append("while (").Append(w.Condition is null ? "true" : printer.Condition(w.Condition)).Append(")\n");
                    Braced(w.Body, depth);
                    break;
                case ForStmt f: {
                    string Part(IReadOnlyList<Stmt> list) => string.Join(", ", list.Select(x => printer.Statement(x).TrimEnd(';')));
                    sb.Append(pad).Append("for (").Append(Part(f.Init)).Append("; ").Append(f.Condition is null ? "" : printer.Condition(f.Condition))
                        .Append("; ").Append(Part(f.Step)).Append(")\n");
                    Braced(f.Body, depth);
                    break;
                }
                case DoStmt d:
                    sb.Append(pad).Append("do\n");
                    sb.Append(pad).Append("{\n");
                    Block(d.Body, depth + 1);
                    sb.Append(pad).Append("} while (").Append(printer.Condition(d.Condition)).Append(");\n");
                    break;
                case SwitchStmt sw:
                    sb.Append(pad).Append("switch (").Append(printer.Selector(sw.Selector, sw.Sections.Select(x => x.Label).OfType<CaseLabel>())).Append(")\n");
                    sb.Append(pad).Append("{\n");
                    printer.Switches.Push(printer.SelectorDomain(sw.Selector));
                    foreach (SwitchSection section in sw.Sections) {
                        sb.Append(pad).Append(Unit).Append(CaseText(section.Label)).Append('\n');
                        Block(section.Body, depth + 2);
                    }
                    printer.Switches.Pop();
                    sb.Append(pad).Append("}\n");
                    break;
                default:
                    sb.Append(pad).Append(printer.Statement(s)).Append('\n');
                    break;
            }
        }
        Flush();
    }

    /// <summary>An <c>if</c>'s condition: <c>false</c> or <c>Cut.Name</c> for dead code.</summary>
    private string ConditionText(IfStmt i) => i.Condition is not null ? printer.Condition(i.Condition) : i.Cut is string cut ? "Cut." + cut : "false";

    private void Braced(IReadOnlyList<Stmt> body, int depth) {
        string pad = Pad(depth);
        sb.Append(pad).Append("{\n");
        Block(body, depth + 1);
        sb.Append(pad).Append("}\n");
    }

    private string CaseText(CaseLabel? c) {
        Naming.Domain? d = printer.Switches.Peek();
        string V(int v) => printer.CaseValue(v, d);
        return c switch {
            null => "default:",
            { Kind: CaseKind.Equal } => $"case {V(c.Value)}:",
            { Kind: CaseKind.Less } => $"case < {V(c.Value)}:",
            { Kind: CaseKind.LessOrEqual } => $"case <= {V(c.Value)}:",
            { Kind: CaseKind.Greater } => $"case > {V(c.Value)}:",
            { Kind: CaseKind.GreaterOrEqual } => $"case >= {V(c.Value)}:",
            _ => $"case >= {V(c.Value)} and <= {V(c.High)}:",
        };
    }
}
