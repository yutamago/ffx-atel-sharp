// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler;

/// <summary>
///     One worker's code as entry methods of statements (Phase 3): every statement that lifts, lowers
///     back to its own bytes and has a C# form is written as C#; the rest stays <c>Asm</c>. Labels are
///     C# labels where every jump to them is a C# <c>goto</c> in the same method, else <c>lN:</c> lines in
///     <c>Asm</c>.
/// </summary>
internal sealed class WorkerDecompiler {
    /// <summary>A statement, or an instruction kept as assembly.</summary>
    private sealed class Item {
        public required int Start, End;
        public List<int> Labels { get; } = [];
        public Stmt? Stmt;
        public string? Text;
        public bool IsCSharp => Text is not null;
    }

    private sealed record Method(List<int> Entries, List<Item> Items);

    private readonly ScriptBlock _block;
    private readonly int _worker;
    private readonly List<Instr> _code;
    private readonly List<Method> _methods = [];
    private readonly StatementLifter _lifter;
    private readonly bool _functionWorker;

    public WorkerDecompiler(ScriptBlock block, int worker, VarInfo[] vars, IReadOnlyList<VarEntry> table, NativeSignatures natives, string[] workerNames) {
        _block = block;
        _worker = worker;
        Bytecode.Script.Worker w = block.Workers[worker];
        _functionWorker = w.Type == 0;
        _code = block.Decode(w);
        _lifter = new StatementLifter(block, worker, table, natives);
        StatementPrinter printer = new(vars, natives, workerNames, _functionWorker);

        Dictionary<uint, int> indexOf = [];
        for (int i = 0; i < _code.Count; i++) indexOf[_code[i].Offset] = i;
        List<int>[] labelsAt = new List<int>[_code.Count];
        bool[] boundary = new bool[_code.Count];
        for (int l = 0; l < w.Labels.Length; l++) {
            int i = indexOf[w.Labels[l]];
            (labelsAt[i] ??= []).Add(l);
            boundary[i] = true;
        }
        foreach (uint e in w.Entries) boundary[indexOf[e]] = true;

        // One method per distinct entry offset, in code order; each runs to the next.
        List<uint> starts = w.Entries.Distinct().Order().ToList();
        for (int m = 0; m < starts.Count; m++) {
            int from = indexOf[starts[m]], to = m + 1 < starts.Count ? indexOf[starts[m + 1]] : _code.Count;
            List<int> entries = Enumerable.Range(0, w.Entries.Length).Where(e => w.Entries[e] == starts[m]).ToList();
            List<Item> items = [];
            List<(int Start, int End)>? split = _lifter.Split(_code, from, to, i => boundary[i]);
            foreach ((int s, int e) in split ?? [.. Enumerable.Range(from, to - from).Select(i => (i, i + 1))]) {
                Item item = new() { Start = s, End = e };
                if (labelsAt[s] is List<int> ls) item.Labels.AddRange(ls);
                if (split is not null && _lifter.Lift(_code, s, e) is Stmt stmt) {
                    try {
                        item.Text = printer.Statement(stmt);
                        item.Stmt = stmt;
                    }
                    catch (NotPrintableException) {
                    }
                }
                items.Add(item);
            }
            _methods.Add(new Method(entries, items));
        }
    }

    /// <summary>
    ///     Keeps as assembly every statement that pushes a pool constant by an index other than the first
    ///     with its value (a pool with duplicates: the compiler gives a value its first index).
    /// </summary>
    public void KeepDuplicateIndices(ScriptImage image) {
        Dictionary<uint, int> firstInt = [], firstFloat = [];
        for (int k = 0; k < image.IntConstants.Count; k++) firstInt.TryAdd(image.IntConstants[k], k);
        for (int k = 0; k < image.FloatConstants.Count; k++) firstFloat.TryAdd(image.FloatConstants[k], k);
        foreach (Item item in _methods.SelectMany(m => m.Items).Where(i => i.IsCSharp)) {
            for (int i = item.Start; i < item.End; i++) {
                if (_code[i] is { Code: Opcodes.PushC, Operand: ushort c } && c < image.IntConstants.Count && firstInt[image.IntConstants[c]] != c
                    || _code[i] is { Code: Opcodes.PushFC, Operand: ushort f } && f < image.FloatConstants.Count && firstFloat[image.FloatConstants[f]] != f) {
                    item.Text = null;
                    item.Stmt = null;
                }
            }
        }
    }

    /// <summary>
    ///     Decides where labels go: a C# label where every jump to it is a C# <c>goto</c> in its own method;
    ///     else an <c>lN:</c> line in assembly, and the <c>goto</c>s to it become assembly too.
    /// </summary>
    public void PlaceLabels() {
        int labels = _block.Workers[_worker].Labels.Length;
        int[] methodOf = new int[labels];
        Array.Fill(methodOf, -1);
        bool[] csharp = new bool[labels];
        bool[] jumped = new bool[labels];
        for (int m = 0; m < _methods.Count; m++) {
            foreach (Item item in _methods[m].Items) {
                foreach (int l in item.Labels) {
                    methodOf[l] = m;
                    csharp[l] = true;
                }
            }
        }
        for (int m = 0; m < _methods.Count; m++) {
            foreach (Item item in _methods[m].Items) {
                for (int i = item.Start; i < item.End; i++) {
                    if (!StackEffects.Jumps(_code[i].Code) || _code[i].Operand is not ushort l || l >= labels) continue;
                    jumped[l] = true;
                    if (!item.IsCSharp || methodOf[l] != m) csharp[l] = false;
                }
            }
        }
        for (int l = 0; l < labels; l++) csharp[l] &= jumped[l];
        foreach (Item item in _methods.SelectMany(m => m.Items)) {
            if (item.Stmt is GotoStmt { Label: int g } && !csharp[g] || item.Stmt is CondGotoStmt { Label: int c } && !csharp[c]) {
                item.Text = null;
                item.Stmt = null;
            }
        }
        _csharpLabel = csharp;
    }

    private bool[] _csharpLabel = [];

    /// <summary>The code with pool constants as values, for the pool rules.</summary>
    public List<CodeOp> Ops() => _code.Select(_lifter.Op).ToList();

    /// <summary>Whether code kept as assembly pushes an integer (<c>pushc</c>) or float (<c>pushfc</c>) constant: assembly names them by pool index.</summary>
    public (bool Ints, bool Floats) AsmUsesPools() {
        bool ints = false, floats = false;
        foreach (Item item in _methods.SelectMany(m => m.Items).Where(i => !i.IsCSharp)) {
            for (int i = item.Start; i < item.End; i++) {
                ints |= _code[i].Code == Opcodes.PushC;
                floats |= _code[i].Code == Opcodes.PushFC;
            }
        }
        return (ints, floats);
    }

    public int Statements => _methods.Sum(m => m.Items.Count(i => i.IsCSharp));

    public int AsmInstructions => _methods.Sum(m => m.Items.Where(i => !i.IsCSharp).Sum(i => i.End - i.Start));

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
        foreach (Stmt s in _methods.SelectMany(m => m.Items).Select(i => i.Stmt).OfType<Stmt>()) {
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
    private const string Body = Indent + Indent;

    /// <summary>The entry methods; <paramref name="annotate"/> names a native in assembly comments.</summary>
    public void WriteMethods(StringBuilder sb, Func<ushort, string?> annotate) {
        for (int m = 0; m < _methods.Count; m++) {
            Method method = _methods[m];
            if (m > 0) sb.Append('\n');
            sb.Append(Indent).Append(string.Join("", method.Entries.Select(n => string.Create(CultureInfo.InvariantCulture, $"[Entry({n})]")))).Append('\n');
            sb.Append(Indent).Append(CultureInfo.InvariantCulture, $"void E{method.Entries[0]}()\n");
            sb.Append(Indent).Append("{\n");
            List<string> asm = [];
            void Flush() {
                if (asm.Count == 0) return;
                if (asm.Count == 1 && !asm[0].Contains(';', StringComparison.Ordinal)) {
                    sb.Append(Body).Append("Asm(\"").Append(asm[0].Trim()).Append("\");\n");
                }
                else {
                    sb.Append(Body).Append("Asm(\"\"\"\n");
                    foreach (string line in asm) sb.Append(Body).Append(line).Append('\n');
                    sb.Append(Body).Append("\"\"\");\n");
                }
                asm.Clear();
            }
            foreach (Item item in method.Items) {
                List<int> cs = item.Labels.Where(l => _csharpLabel[l]).ToList();
                List<int> raw = item.Labels.Where(l => !_csharpLabel[l]).ToList();
                if (cs.Count > 0 || item.IsCSharp) Flush();
                foreach (int l in cs) sb.Append(Indent).Append(CultureInfo.InvariantCulture, $"l{l}:\n");
                foreach (int l in raw) asm.Add(string.Create(CultureInfo.InvariantCulture, $"l{l}:"));
                if (item.IsCSharp) {
                    Flush();
                    sb.Append(Body).Append(item.Text).Append('\n');
                    continue;
                }
                for (int i = item.Start; i < item.End; i++) {
                    Instr instr = _code[i];
                    string line = Bytecode.Script.Assembly.Render(_block, _worker, instr);
                    if (instr.Opcode.Operand == OperandKind.Func && instr.Operand is ushort id && annotate(id) is string native) {
                        line = $"{line,-24} ; {native}";
                    }
                    asm.Add(Indent + line);
                }
            }
            Flush();
            sb.Append(Indent).Append("}\n");
        }
    }
}
