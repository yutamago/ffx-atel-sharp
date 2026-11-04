// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler.Layout;
using Ffx.Atel.Decompiler.Naming;
using Ffx.Atel.Decompiler.Repeats;
using Microsoft.CodeAnalysis.CSharp;

namespace Ffx.Atel.Decompiler;

/// <summary>
///     How the decompiler writes a worker's repeated code once, as inline methods of the worker
///     (docs/language.md, "Repeated code"): a run of statements is folded when it is at least
///     <paramref name="MinLines"/> lines long and saves at least <paramref name="MinSaved"/> lines, at most
///     <paramref name="MaxHelpers"/> per worker, each with at most <paramref name="MaxParams"/> parameters and
///     <paramref name="MaxLength"/> statements. Shorter runs, or runs with more parameters, read better
///     written out than as a call of a method whose name says nothing of them.
/// </summary>
public sealed record RepeatOptions(int MinSaved = 100, int MinLines = 8, int MaxHelpers = 48, int MaxParams = 6, int MaxLength = 48) {
    /// <summary>No repeated code folded.</summary>
    public static RepeatOptions Disabled { get; } = new(MaxHelpers: 0);
}

// Repeated code (docs/research/repeats.md): after the reference assembly's inline methods are folded, the
// worker's own statements are mined for runs that recur with only different constants (the miner of
// `atel repeats`, on this worker alone). Each run the cover takes becomes an [Inline] method of the
// worker, its body the run's first place with a parameter where the places differ, and every place
// becomes a call. The worker is lowered again with the calls and compared with the original; if it
// differs, nothing is folded. A worker's helpers use its registers and the script's variables, and
// travel with the worker's code (into the library, when the worker is shared).
internal sealed partial class WorkerDecompiler {
    private readonly NativeSignatures _natives;
    private readonly string[] _workerNames;

    /// <summary>The worker's name, read when used: <see cref="Naming.WorkerNaming.AssignRoles"/> may rename it after <see cref="Structure"/>.</summary>
    private string _name => _workerNames[_worker];

    /// <summary>The worker's own inline methods, as written: the method, its body as printed (calls of later ones folded), its calls.</summary>
    private readonly List<(InlineMacro Macro, IReadOnlyList<Stmt> Printed, int Calls)> _own = [];

    /// <summary>Folds the worker's repeated code into inline methods of its own (<see cref="RepeatOptions"/>).</summary>
    public void FoldRepeats(RepeatOptions o) {
        if (o.MaxHelpers <= 0) return;
        ShapeTable table = new();
        ShapeWalker walker = new(table, _natives, vars: false);
        for (int m = 0; m < _methods.Count; m++) walker.WalkMethod(_methods[m].Body, m);
        Corpus corpus = new();
        corpus.Add(_name, [(_worker, walker.Blocks, [.. walker.Holes])]);
        Miner miner = new(corpus, minPlaces: 2, maxLength: o.MaxLength, minSaved: o.MinSaved, maxParams: o.MaxParams);
        List<Helper> helpers = miner.Cover([.. miner.Candidates().Where(c => c.Lines >= o.MinLines)], o.MaxHelpers);
        if (helpers.Count == 0) return;

        // The methods: each helper's first place, its varying holes as parameters.
        List<(Helper Helper, InlineMacro Macro)> made = [];
        foreach (Helper h in helpers) {
            if (Make(corpus, table, h, made.Count + 1) is InlineMacro macro) made.Add((h, macro));
        }
        if (made.Count == 0) return;

        // Every place becomes a call, outermost first: a place inside another's run is inside its call.
        Dictionary<int, SortedDictionary<int, (InlineMacro Macro, int Length)>> at = [];
        foreach ((Helper h, InlineMacro macro) in made) {
            foreach (long p in h.Places) {
                (int b, int i) = Miner.Unplace(p);
                if (!at.TryGetValue(b, out var row)) at[b] = row = [];
                row.TryAdd(i, (macro, h.Run.Pattern.Length));
            }
        }
        List<List<Stmt>> before = [.. _methods.Select(m => m.Body)];
        Dictionary<InlineMacro, int> calls = [];
        PlaceWalk walk = new() { At = at, Calls = calls };
        foreach (Method m in _methods) {
            List<Stmt>? body = Rebuild(m.Body, walk);
            if (!walk.Ok) break;
            m.Body = body!;
        }
        if (!walk.Ok || calls.Count == 0 || !Verify()) {
            for (int k = 0; k < _methods.Count; k++) _methods[k].Body = before[k];
            CountInline("repeats-fallback", calls.Values.Sum());
            return;
        }
        // A helper's body may hold calls of helpers taken after it (the cover mines inside the first place).
        for (int k = 0; k < made.Count; k++) {
            InlineMacro macro = made[k].Macro;
            if (!calls.TryGetValue(macro, out int n)) continue;
            List<InlineMacro> later = [.. made.Skip(k + 1).Select(x => x.Macro)];
            int dead = 0;
            List<InlineStmt> inner = [];
            List<Stmt> printed = later.Count == 0 ? [.. macro.Body] : Fold(macro.Body, later, inner, ref dead);
            foreach (InlineStmt i in inner) calls[i.Macro] = calls.GetValueOrDefault(i.Macro) + 1;
            _own.Add((macro, printed, n));
        }
        // Helpers no place calls any more (all inside others' runs, not folded there) are left out.
        _own.RemoveAll(x => !calls.ContainsKey(x.Macro));
        // Every body must be C#; else the worker keeps its code as it was.
        foreach ((InlineMacro macro, IReadOnlyList<Stmt> printed, _) in _own) {
            _printer.Params = macro.Params;
            try {
                Print(printed);
            }
            catch (NotPrintableException) {
                _own.Clear();
                for (int k = 0; k < _methods.Count; k++) _methods[k].Body = before[k];
                CountInline("repeats-fallback-print", calls.Values.Sum());
                return;
            }
            finally {
                _printer.Params = null;
            }
        }
        foreach ((InlineMacro macro, _, int n) in _own) {
            CountInline("repeats-helpers");
            CountInline("repeats-calls", n);
        }
    }

    /// <summary>Where the places are (block, index), the calls made, the blocks numbered so far, and whether every place matched.</summary>
    private sealed class PlaceWalk {
        public required Dictionary<int, SortedDictionary<int, (InlineMacro Macro, int Length)>> At;
        public required Dictionary<InlineMacro, int> Calls;
        public int Block;
        public bool Ok = true;
    }

    /// <summary>The block's statements with the places folded; blocks numbered as the miner's walk numbers them.</summary>
    private static List<Stmt>? Rebuild(IReadOnlyList<Stmt> list, PlaceWalk w) {
        int me = w.Block++;
        w.At.TryGetValue(me, out var row);
        List<Stmt> result = [];
        for (int k = 0; k < list.Count; k++) {
            if (row is not null && row.TryGetValue(k, out var place) && k + place.Length <= list.Count) {
                if (Inline.Match(place.Macro, list, k) is not Expr[] args) {
                    w.Ok = false;
                    return null;
                }
                // The statements folded here hold blocks the walk numbered: skip them.
                for (int j = k; j < k + place.Length; j++) w.Block += Blocks(list[j]);
                result.Add(new InlineStmt(place.Macro, args, Inline.Expand(place.Macro, args)));
                w.Calls[place.Macro] = w.Calls.GetValueOrDefault(place.Macro) + 1;
                k += place.Length - 1;
                continue;
            }
            Stmt s = list[k];
            result.Add(s switch {
                IfStmt i => i with { Then = Rebuild(i.Then, w)!, Else = i.Else is null ? null : Rebuild(i.Else, w) },
                WhileStmt x => x with { Body = Rebuild(x.Body, w)! },
                DoStmt d => d with { Body = Rebuild(d.Body, w)! },
                ForStmt f => f with { Body = Rebuild(f.Body, w)! },
                SwitchStmt sw => sw with { Sections = [.. sw.Sections.Select(x => x with { Body = Rebuild(x.Body, w)! })] },
                _ => s,
            });
            if (!w.Ok) return null;
        }
        return result;
    }

    /// <summary>How many blocks the miner's walk registers in a statement (its nested blocks, theirs included).</summary>
    private static int Blocks(Stmt s) => s switch {
        IfStmt i => 1 + i.Then.Sum(Blocks) + (i.Else is null ? 0 : 1 + i.Else.Sum(Blocks)),
        WhileStmt w => 1 + w.Body.Sum(Blocks),
        DoStmt d => 1 + d.Body.Sum(Blocks),
        ForStmt f => 1 + f.Body.Sum(Blocks),
        SwitchStmt sw => sw.Sections.Sum(x => 1 + x.Body.Sum(Blocks)),
        _ => 0,
    };

    /// <summary>A helper as an inline method: its first place with its parameters in the holes that vary; null if it cannot be one.</summary>
    private InlineMacro? Make(Corpus corpus, ShapeTable table, Helper h, int number) {
        (int b, int i) = Miner.Unplace(h.Places[0]);
        IReadOnlyList<Stmt> list = corpus.Blocks[b].Record.Source;
        List<Stmt> run = [.. list.Skip(i).Take(h.Run.Pattern.Length)];
        Dictionary<int, int> param = [];
        for (int p = 0; p < h.Params.Count; p++) foreach (int c in h.Params[p].Columns) param[c] = p;
        Expr[] first = new Expr[h.Params.Count];
        ShapeWalker rewriter = new(table, _natives, vars: false, (hole, e) => {
            if (!param.TryGetValue(hole, out int p)) return null;
            first[p] ??= e;
            return new ParamExpr(p);
        });
        (List<Stmt> template, _) = rewriter.Rewrite(run);
        if (first.Any(e => e is null)) return null;
        bool waits = false;
        Inline.Walk(template, e => {
            if (e is CallExpr c) waits |= _natives[c.Native]?.Polls ?? true;
        }, s => {
            if (s is CallStmt c) waits |= _natives[c.Native]?.Polls ?? true;
            if (s is RequestStmt or CallWorkerStmt or OpArgsStmt) waits = true;
        });
        string name = string.Create(CultureInfo.InvariantCulture, $"{_name}.Repeated{number:00}");
        InlineMacro draft = new(name, [.. first.Select((e, k) => new InlineParam($"p{k}", IsFloat(e), null))], template, waits);
        List<InlineParam> ps = Params(draft, first);
        InlineMacro macro = new(name, ps, template, waits) { CallName = name[(name.IndexOf('.', StringComparison.Ordinal) + 1)..] };
        // The miner's own check: the template finds its first place again and gives it back.
        if (Inline.Match(macro, list, i) is not Expr[] a || !Inline.Same(Inline.Expand(macro, a), run)) return null;
        // The body must be C#; its text without names is its signature, which data/inline-names.toml may name.
        string code;
        _printer.Params = [.. ps.Select((p, k) => p with { Name = string.Create(CultureInfo.InvariantCulture, $"p{k}") })];
        try {
            code = Print(template);
        }
        catch (NotPrintableException) {
            return null;
        }
        finally {
            _printer.Params = null;
        }
        string signature = InlineNames.Signature(code);
        if (Environment.GetEnvironmentVariable("ATEL_DUMP_REPEATS") is string dump) {
            lock (s_dumpLock) File.AppendAllText(dump, $"{signature}\t{_name}\t{h.Places.Count}\t{h.Run.Lines}\t{code.Replace("\n", " | ", StringComparison.Ordinal)}\n");
        }
        if (InlineNames.Default.Find(signature) is { } found && (found.Params is null || found.Params.Count == ps.Count)) {
            string unique = found.Name;
            for (int k = 2; !_ownNames.Add(unique); k++) unique = found.Name + k.ToString(CultureInfo.InvariantCulture);
            if (found.Params is IReadOnlyList<string> names) ps = [.. ps.Select((p, k) => p with { Name = names[k] })];
            macro = new InlineMacro($"{_name}.{unique}", ps, template, waits) { CallName = unique };
        }
        return macro;
    }

    private static readonly object s_dumpLock = new();

    /// <summary>The names the worker's own inline methods took from the table, so that two of them get two names.</summary>
    private readonly HashSet<string> _ownNames = new(StringComparer.Ordinal);

    private static bool IsFloat(Expr e) => e is FloatLit || e is PoolExpr { Code: not Opcodes.PushC };

    /// <summary>
    ///     The parameters' names and types: each named after the native argument it is first passed to
    ///     (<c>slot</c>, <c>x</c>), and typed as that argument (<c>TextId</c>, an enum) when every use is such an argument.
    /// </summary>
    private List<InlineParam> Params(InlineMacro draft, Expr[] first) {
        List<InlineParam> result = [];
        HashSet<string> taken = new(StringComparer.Ordinal);
        for (int k = 0; k < first.Length; k++) {
            string baseName = "value";
            if (draft.FirstUse[k] is (ushort native, int arg) && _natives[native]?.ParamNames is string[] names && arg < names.Length && names[arg].Length > 0) {
                baseName = names[arg];
            }
            if (!SyntaxFacts.IsValidIdentifier(baseName) || SyntaxFacts.GetKeywordKind(baseName) != SyntaxKind.None) baseName += "Value";
            string n = baseName;
            for (int j = 2; !taken.Add(n); j++) n = baseName + j.ToString(CultureInfo.InvariantCulture);
            result.Add(new InlineParam(n, IsFloat(first[k]), null, Type: TypeOf(draft.Body, k)));
        }
        return result;
    }

    /// <summary>A parameter's type when every use is a native's argument of one domain with a type (<c>TextId</c>, an enum).</summary>
    private static string? TypeOf(IReadOnlyList<Stmt> body, int index) {
        HashSet<Domain?> domains = [];
        bool other = false;
        void Note(ushort native, IReadOnlyList<Expr> args) {
            for (int k = 0; k < args.Count; k++) {
                if (args[k] is ParamExpr p && p.Index == index) domains.Add(GameNames.NativeParameters.GetValueOrDefault(native)?[k]);
            }
        }
        int uses = 0;
        Inline.Walk(body, e => {
            if (e is ParamExpr p && p.Index == index) uses++;
            if (e is CallExpr c) Note(c.Native, c.Args);
        }, s => {
            if (s is CallStmt c) Note(c.Native, c.Args);
            if (s is InlineStmt) other = true;
        });
        if (other || domains.Count != 1 || domains.Single() is not Domain d) return null;
        // Every use must be such an argument (a use in arithmetic is no argument of the domain).
        int direct = 0;
        Inline.Walk(body, e => {
            if (e is CallExpr c) direct += c.Args.Count(a => a is ParamExpr p && p.Index == index);
        }, s => {
            if (s is CallStmt c) direct += c.Args.Count(a => a is ParamExpr p && p.Index == index);
        });
        if (direct != uses) return null;
        return d.EnumType ?? (ReferenceEquals(d, GameNames.Text) ? "TextId" : null);
    }

    /// <summary>
    ///     Which of the worker's own inline methods are <c>async Task</c>: those whose body awaits a request, as
    ///     written once requests are named (a call of one is <c>await Name(…);</c>). Later methods first, as a
    ///     body calls only methods taken after it.
    /// </summary>
    private void PrepareOwnInlines() {
        for (int k = _own.Count - 1; k >= 0; k--) {
            (InlineMacro m, IReadOnlyList<Stmt> printed, _) = _own[k];
            _printer.Params = m.Params;
            try {
                if (Print(printed).Contains("await ", StringComparison.Ordinal)) _printer.AsyncInlines.Add(m);
            }
            finally {
                _printer.Params = null;
            }
        }
    }

    /// <summary>The worker's own inline methods, after its entry methods (<see cref="WriteMethods"/>).</summary>
    private void WriteOwnInlines(StringBuilder sb, TreeWriter writer) {
        foreach ((InlineMacro m, IReadOnlyList<Stmt> printed, int calls) in _own) {
            sb.Append('\n');
            string natives = string.Join(", ", NativesOf(m.Body).Take(6));
            sb.Append(Indent).Append(CultureInfo.InvariantCulture, $"/// <summary>Code this worker repeats with only different values, written once: {calls} call{(calls == 1 ? "" : "s")}")
                .Append(natives.Length > 0 ? $" ({natives})" : "").Append(".</summary>\n");
            sb.Append(Indent).Append(m.Waits ? "[Inline(Waits = true)]\n" : "[Inline]\n");
            string ps = string.Join(", ", m.Params.Select(p => $"{p.Type ?? (p.Float ? "float" : "int")} {p.Name}"));
            string returns = _printer.AsyncInlines.Contains(m) ? "async Task" : "void";
            sb.Append(Indent).Append(CultureInfo.InvariantCulture, $"private static {returns} {m.CallName}({ps})\n");
            sb.Append(Indent).Append("{\n");
            _printer.Params = m.Params;
            try {
                writer.Block(printed, 2);
            }
            finally {
                _printer.Params = null;
            }
            sb.Append(Indent).Append("}\n");
        }
    }

    private IEnumerable<string> NativesOf(IReadOnlyList<Stmt> body) {
        List<string> names = [];
        void Add(ushort id) {
            if (_natives[id] is { } n) names.Add(n.Name);
        }
        Inline.Walk(body, e => {
            if (e is CallExpr c) Add(c.Native);
        }, s => {
            if (s is CallStmt c) Add(c.Native);
        });
        return names.Distinct(StringComparer.Ordinal);
    }
}
