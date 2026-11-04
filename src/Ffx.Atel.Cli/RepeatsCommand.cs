// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Cli.Repeats;
using Ffx.Atel.Decompiler.Repeats;
using Ffx.Atel.Decompiler;

namespace Ffx.Atel.Cli;

/// <summary>
///     <c>atel repeats</c>: the statement sequences that recur with only different values, as candidate
///     inline helpers (docs/research/repeats.md). Mines the structured statements the decompiler makes,
///     covers them greedily with helpers, prints each with an example and its parameters, and with
///     <c>--verify</c> folds the helpers in the real decompiler, whose check lowers the code again and
///     compares it with the original instruction by instruction. Game code: the files stay on your machine.
/// </summary>
internal static class RepeatsCommand {
    public const string Usage = """
          repeats [filter] -o <dir> [--vars] [--no-blitzball] [--top 40] [--max-len 48] [--min-places 2] [--max-params 8]
                  [--verify <n>] [--jobs 4]
                                           the statement sequences that recur with only different values
                                           (docs/research/repeats.md): <dir>/repeats.tsv (the helpers the
                                           greedy cover takes), repeats.md (each with an example and its
                                           parameters); --vars lets variables be parameters too;
                                           --no-blitzball leaves out the blitzball scripts; --verify n
                                           folds the first n helpers in the decompiler and checks the bytes
                                           (verify.tsv); game code: stays on your machine
        """;

    private sealed record Options(bool Vars, bool NoBlitzball, int Top, int MaxLength, int MinPlaces, int MaxParams, int Verify, int Jobs);

    public static int Run(VbfArchive archive, List<string> args, string? output) {
        if (output is null) throw new Program.UsageException("repeats [filter] -o <dir>");
        Options o = new(
            Vars: TakeFlag(args, "--vars"),
            NoBlitzball: TakeFlag(args, "--no-blitzball"),
            Top: TakeInt(args, "--top", 40),
            MaxLength: TakeInt(args, "--max-len", 48),
            MinPlaces: TakeInt(args, "--min-places", 2),
            MaxParams: TakeInt(args, "--max-params", 8),
            Verify: TakeInt(args, "--verify", 0),
            Jobs: TakeInt(args, "--jobs", 4));
        string filter = args.Count > 0 ? args[0] : "";
        List<string> paths = [.. GameData.AllScriptPaths(archive).Where(p => p.Contains(filter, StringComparison.Ordinal)
            && !(o.NoBlitzball && DeadCommand.IsBlitzball(Path.GetFileNameWithoutExtension(p))))];
        Directory.CreateDirectory(output);

        // Pass 1: every worker's statements as shapes. A worker with the same code as one before it (the
        // script variants, the workers the library shares) is mined once, as the library writes it once.
        ShapeTable table = new();
        ConcurrentDictionary<string, List<(string Signature, int Worker, List<BlockRecord> Blocks, long[] Holes)>> walked = new(StringComparer.Ordinal);
        ConcurrentBag<string> failures = [];
        ParallelOptions jobs = new() { MaxDegreeOfParallelism = o.Jobs };
        Parallel.ForEach(paths, jobs, path => {
            try {
                ScriptFile file = ScriptFile.Parse(archive.Read(path));
                StructuredScript s = ScriptDecompiler.Structured(path, file, inline: []);
                List<(string, int, List<BlockRecord>, long[])> workers = [];
                foreach (StructuredWorker sw in s.Workers) {
                    ShapeWalker w = new(table, ScriptDecompiler.Natives, o.Vars);
                    for (int m = 0; m < sw.Methods.Count; m++) w.WalkMethod(sw.Methods[m].Body, m);
                    workers.Add((Decompiler.Layout.WorkerSignature.Of(file.Script, sw.Index), sw.Index, w.Blocks, [.. w.Holes]));
                }
                walked[path] = workers;
            }
            catch (Exception ex) when (ex is ScriptFormatException or InvalidOperationException or FormatException) {
                failures.Add($"{path}: {ex.Message}");
            }
        });
        foreach (string f in failures.Order(StringComparer.Ordinal)) Console.Error.WriteLine(f);
        Corpus corpus = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        int duplicates = 0;
        foreach (string path in paths.Where(walked.ContainsKey)) {
            List<(string Signature, int Worker, List<BlockRecord> Blocks, long[] Holes)> kept = [];
            foreach (var w in walked[path]) {
                if (seen.Add(w.Signature)) kept.Add(w);
                else duplicates++;
            }
            corpus.Add(path, [.. kept.Select(w => (w.Worker, w.Blocks, w.Holes))]);
        }
        walked.Clear();
        Console.Error.WriteLine($"{duplicates} workers skipped as copies of one mined before");
        long statements = corpus.Blocks.Where(b => b.Parent < 0).Sum(b => (long)b.Record.Stmts.Sum(s => s.Size));
        long lines = corpus.Blocks.Where(b => b.Parent < 0).Sum(b => (long)b.Record.Stmts.Sum(s => s.Lines));
        Console.Error.WriteLine($"{corpus.Scripts.Count} scripts, {corpus.Blocks.Count} blocks, {statements} statements, {lines} lines, {table.Count} shapes");

        // Mining and the cover.
        Miner miner = new(corpus, o.MinPlaces, o.MaxLength, maxParams: o.MaxParams);
        List<Candidate> candidates = miner.Candidates();
        Console.Error.WriteLine($"{candidates.Count} maximal runs");
        List<Helper> helpers = miner.Cover(candidates, Math.Max(o.Top, o.Verify));
        candidates.Clear();

        // Pass 2: an example of each helper, its template and C#.
        Dictionary<Helper, Example> examples = Examples(archive, corpus, helpers, table, o.Vars);
        List<InlineMacro> macros = [.. helpers.Where(examples.ContainsKey).Select(h => examples[h].Macro)];

        WriteTable(Path.Combine(output, "repeats.tsv"), corpus, helpers, examples, o.Vars);
        string report = Report(corpus, helpers, examples, o, statements, lines);
        report += Twins(corpus);
        File.WriteAllText(Path.Combine(output, "repeats.md"), report);
        File.WriteAllText(Path.Combine(output, "repeats-code.txt"), string.Concat(helpers.Where(examples.ContainsKey).Select(h =>
            string.Create(CultureInfo.InvariantCulture, $"// ----- R{h.Rank:000}: {Path.GetFileNameWithoutExtension(examples[h].Script)} {examples[h].Location}\n{examples[h].Code}\n"))));

        // Pass 3: fold the first helpers in the decompiler, which checks the bytes.
        if (o.Verify > 0) {
            List<InlineMacro> verify = [.. macros.Take(o.Verify)];
            long bodies = helpers.Take(o.Verify).Sum(h => (long)h.Run.Lines);
            string summary = VerifyFolds(archive, paths, verify, jobs, Path.Combine(output, "verify.tsv"), bodies);
            File.AppendAllText(Path.Combine(output, "repeats.md"), summary);
            report += summary;
        }
        Console.Out.Write(report[..Math.Min(report.Length, 6000)]);
        return failures.IsEmpty ? 0 : 1;
    }

    // ----- Examples --------------------------------------------------------------------------------

    /// <summary>A helper's first place, as code: its C#, its template and whether the template gives the place back.</summary>
    /// <param name="DeadBlocks">The dead <c>if (false)</c> blocks in the helper's body.</param>
    internal sealed record Example(string Script, string Worker, string Location, string Code, InlineMacro Macro, IReadOnlyList<string> Natives, IReadOnlyList<string> Values, bool SelfCheck, int DeadBlocks);

    private static Dictionary<Helper, Example> Examples(VbfArchive archive, Corpus corpus, List<Helper> helpers, ShapeTable table, bool vars) {
        Dictionary<Helper, Example> result = [];
        foreach (IGrouping<int, Helper> g in helpers.GroupBy(h => corpus.Blocks[Miner.Unplace(h.Places[0]).Block].Script)) {
            string path = corpus.Scripts[g.Key];
            StructuredScript s = ScriptDecompiler.Structured(path, ScriptFile.Parse(archive.Read(path)), inline: []);
            foreach (Helper h in g) {
                (int b, int i) = Miner.Unplace(h.Places[0]);
                MinedBlock mb = corpus.Blocks[b];
                StructuredWorker w = s.Workers[mb.Worker];
                (IReadOnlyList<Stmt> list, string where) = Navigate(corpus, b, w);
                List<Stmt> run = [.. list.Skip(i).Take(h.Run.Pattern.Length)];
                Dictionary<int, int> param = [];
                for (int p = 0; p < h.Params.Count; p++) foreach (int c in h.Params[p].Columns) param[c] = p;
                string[] values = new string[h.Params.Count];
                ShapeWalker rewriter = new(table, ScriptDecompiler.Natives, vars, (hole, e) => {
                    if (!param.TryGetValue(hole, out int p)) return null;
                    values[p] ??= ValueText(e);
                    return new ParamExpr(p);
                });
                (List<Stmt> template, _) = rewriter.Rewrite(run);
                string name = string.Create(CultureInfo.InvariantCulture, $"Repeats.R{h.Rank:000}");
                List<InlineParam> ps = [.. h.Params.Select((x, k) => new InlineParam($"p{k}", x.Tag == ShapeWalker.TagFloat, null))];
                InlineMacro macro = new(name, ps, template, (h.Traits & Traits.Waits) != 0);
                bool ok = Inline.Match(macro, list, i) is Expr[] a && Inline.Same(Inline.Expand(macro, a), run);
                List<string> natives = [];
                Inline.Walk(run, e => {
                    if (e is CallExpr c) natives.Add(NativeName(c.Native));
                }, x => {
                    if (x is CallStmt c) natives.Add(NativeName(c.Native));
                });
                int dead = 0;
                Inline.Walk(run, _ => { }, x => {
                    if (x is IfStmt { Condition: null }) dead++;
                });
                result[h] = new Example(path, w.Name, $"{w.Name} {where} [{i}]", w.Print(run), macro, [.. natives.Distinct()], values, ok, dead);
            }
        }
        return result;
    }

    private static string NativeName(ushort id) => ScriptDecompiler.Natives[id]?.Name ?? $"0x{id:x4}";

    /// <summary>The block's statements, found again from the method's body down, and where it is (<c>E3 if/else</c>).</summary>
    private static (IReadOnlyList<Stmt>, string) Navigate(Corpus corpus, int block, StructuredWorker w) {
        List<(int At, int Ordinal)> steps = [];
        int root = block;
        while (corpus.Blocks[root].Parent >= 0) {
            steps.Add((corpus.Blocks[root].Record.ParentAt, corpus.Blocks[root].Record.Ordinal));
            root = corpus.Blocks[root].Parent;
        }
        steps.Reverse();
        StructuredMethod m = w.Methods[corpus.Blocks[root].Record.Method];
        IReadOnlyList<Stmt> list = m.Body;
        List<string> where = [string.Create(CultureInfo.InvariantCulture, $"E{m.Entries[0]}")];
        foreach ((int at, int ordinal) in steps) {
            Stmt s = list[at];
            (list, string kind) = s switch {
                IfStmt x => (ordinal == 0 ? x.Then : x.Else!, ordinal == 0 ? (x.Condition is null ? "if (false)" : "if") : "else"),
                WhileStmt x => (x.Body, "while"),
                DoStmt x => (x.Body, "do"),
                ForStmt x => (x.Body, "for"),
                SwitchStmt x => (x.Sections[ordinal].Body, "case"),
                _ => throw new InvalidOperationException("no block there"),
            };
            where.Add(string.Create(CultureInfo.InvariantCulture, $"[{at}] {kind}"));
        }
        return (list, string.Join(" > ", where));
    }

    private static string ValueText(Expr e) => e switch {
        IntLit x => StatementPrinterText(x.Value),
        FloatLit x => x.Value.ToString("R", CultureInfo.InvariantCulture) + "f",
        PoolExpr x => x.Value.ToString(CultureInfo.InvariantCulture),
        VarExpr x => string.Create(CultureInfo.InvariantCulture, $"V{x.Var}"),
        ElemExpr x => string.Create(CultureInfo.InvariantCulture, $"V{x.Var}[{ValueText(x.Index)}]"),
        Elem2Expr x => string.Create(CultureInfo.InvariantCulture, $"V{x.Var}[{ValueText(x.Row)}, {ValueText(x.Col)}]"),
        BinaryExpr x => $"({ValueText(x.Left)} op{x.Op} {ValueText(x.Right)})",
        _ => e.GetType().Name,
    };

    private static string StatementPrinterText(int v) => v is >= 0x10000 or <= -0x10000 ? $"0x{v:x}" : v.ToString(CultureInfo.InvariantCulture);

    // ----- Output ----------------------------------------------------------------------------------

    /// <summary>What an inline method would need beyond today's rules (docs/language.md, "Inline methods"); empty when it can be written now.</summary>
    internal static List<string> Needs(Helper h, bool vars) {
        List<string> needs = [];
        bool readsFixed = (h.Traits & Traits.ReadsVars) != 0 && (!vars || h.FixedVarHoles > 0);
        if (readsFixed) needs.Add("fields");
        if ((h.Traits & Traits.Assigns) != 0) needs.Add(h.Params.Any(p => p.Tag is ShapeWalker.TagVarTarget or ShapeWalker.TagElemTarget) ? "assign-ref" : "assign");
        if ((h.Traits & Traits.Registers) != 0) needs.Add("locals");
        if ((h.Traits & Traits.Requests) != 0) needs.Add("requests");
        if ((h.Traits & Traits.Ops) != 0) needs.Add("ops");
        return needs;
    }

    private static string TraitText(Traits t) => string.Join(",", Enum.GetValues<Traits>().Where(x => x != Traits.None && (t & x) != 0 && x is Traits.Control or Traits.Dead or Traits.Waits).Select(x => x.ToString().ToLowerInvariant()));

    private static string Base(string path) {
        string file = Path.GetFileNameWithoutExtension(path);
        foreach (string prefix in new[] { "psvcn_", "psv_", "cn_" }) {
            if (file.StartsWith(prefix, StringComparison.Ordinal)) return file[prefix.Length..];
        }
        return file;
    }

    private static (int Scripts, int Bases) Spread(Corpus corpus, Helper h) {
        HashSet<int> scripts = [.. h.Places.Select(p => corpus.Blocks[Miner.Unplace(p).Block].Script)];
        return (scripts.Count, scripts.Select(s => Base(corpus.Scripts[s])).Distinct().Count());
    }

    private static void WriteTable(string file, Corpus corpus, List<Helper> helpers, Dictionary<Helper, Example> examples, bool vars) {
        StringBuilder sb = new();
        sb.Append("rank\tsaved_lines\tsaved_statements\tplaces\tscripts\tscripts_without_variants\tstatements\tsize\tlines\tparams\tholes\tneeds\ttraits\tself_check\tnatives\texample\n");
        foreach (Helper h in helpers) {
            (int scripts, int bases) = Spread(corpus, h);
            examples.TryGetValue(h, out Example? ex);
            sb.Append(CultureInfo.InvariantCulture, $"{h.Rank}\t{h.SavedLines}\t{h.SavedStatements}\t{h.Places.Count}\t{scripts}\t{bases}\t{h.Run.Pattern.Length}\t{h.Run.Size}\t{h.Run.Lines}\t{h.Params.Count}\t{h.Holes}\t{string.Join(',', Needs(h, vars))}\t{TraitText(h.Traits)}\t{ex?.SelfCheck}\t{string.Join(' ', ex?.Natives.Take(8) ?? [])}\t{(ex is null ? "" : Path.GetFileNameWithoutExtension(ex.Script) + " " + ex.Location)}\n");
        }
        File.WriteAllText(file, sb.ToString());
    }

    private static string Report(Corpus corpus, List<Helper> helpers, Dictionary<Helper, Example> examples, Options o, long statements, long lines) {
        StringBuilder sb = new();
        long savedLines = helpers.Sum(h => h.SavedLines), savedStatements = helpers.Sum(h => h.SavedStatements);
        sb.Append(CultureInfo.InvariantCulture, $"# Repeats\n\n{corpus.Scripts.Count} scripts, {statements} statements, {lines} lines; variables as parameters: {(o.Vars ? "yes" : "no")}.\n");
        sb.Append(CultureInfo.InvariantCulture, $"The {helpers.Count} helpers below fold {helpers.Sum(h => h.Places.Count)} places: {savedLines} lines ({100.0 * savedLines / Math.Max(1, lines):0.0} %), {savedStatements} statements ({100.0 * savedStatements / Math.Max(1, statements):0.0} %).\n\n");
        sb.Append("| # | saved lines | places | scripts (w/o variants) | stmts/size/lines | params | needs | traits | natives | example |\n|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (Helper h in helpers) {
            (int scripts, int bases) = Spread(corpus, h);
            examples.TryGetValue(h, out Example? ex);
            sb.Append(CultureInfo.InvariantCulture, $"| {h.Rank} | {h.SavedLines} | {h.Places.Count} | {scripts} ({bases}) | {h.Run.Pattern.Length}/{h.Run.Size}/{h.Run.Lines} | {h.Params.Count} | {string.Join(", ", Needs(h, o.Vars))} | {TraitText(h.Traits)} | {string.Join(" ", ex?.Natives.Take(5) ?? [])} | {(ex is null ? "" : Path.GetFileNameWithoutExtension(ex.Script) + " " + ex.Worker)} |\n");
        }
        sb.Append('\n');
        int deadIn = helpers.Where(examples.ContainsKey).Sum(h => examples[h].DeadBlocks);
        long deadFolded = helpers.Where(examples.ContainsKey).Sum(h => (long)examples[h].DeadBlocks * h.Places.Count);
        sb.Append(CultureInfo.InvariantCulture, $"Dead `if (false)` blocks: {deadFolded} at the places folded, {deadIn} in the helpers' bodies.\n\n");
        sb.Append(Workers(corpus, helpers));
        foreach (Helper h in helpers) {
            if (!examples.TryGetValue(h, out Example? ex)) continue;
            sb.Append(CultureInfo.InvariantCulture, $"## R{h.Rank:000}: {h.Places.Count} places, {h.SavedLines} lines\n\n");
            sb.Append(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(ex.Script)} {ex.Location}; template gives the place back: {ex.SelfCheck}; needs: {string.Join(", ", Needs(h, o.Vars)) switch { "" => "nothing new", var n => n }}.\n\n");
            for (int p = 0; p < h.Params.Count; p++) {
                Param x = h.Params[p];
                sb.Append(CultureInfo.InvariantCulture, $"- p{p}: {TagText(x.Tag)}, used {x.Columns.Count}×, {x.Distinct} values, most common {CommonText(x)} at {x.CommonCount} of {h.Places.Count} places; here {ex.Values[p]}\n");
            }
            sb.Append("\n```csharp\n").Append(Shorten(ex.Code, 40)).Append("```\n\n");
        }
        return sb.ToString();
    }

    /// <summary>The workers with the most lines, and what the helpers save in each (lines and statements, from the cover).</summary>
    private static string Workers(Corpus corpus, List<Helper> helpers) {
        Dictionary<(int, int), (long Stmts, long Lines)> size = [];
        foreach (MinedBlock b in corpus.Blocks.Where(b => b.Parent < 0)) {
            (long st, long li) = size.GetValueOrDefault((b.Script, b.Worker));
            size[(b.Script, b.Worker)] = (st + b.Record.Stmts.Sum(x => (long)x.Size), li + b.Record.Stmts.Sum(x => (long)x.Lines));
        }
        // A helper's first place is its body: what other helpers fold inside it is saved in the body, not in the worker.
        Dictionary<int, List<(int From, int To, Helper Body)>> bodies = [];
        foreach (Helper h in helpers) {
            (int b, int i) = Miner.Unplace(h.Places[0]);
            if (!bodies.TryGetValue(b, out List<(int, int, Helper)>? l)) bodies[b] = l = [];
            l.Add((i, i + h.Run.Pattern.Length, h));
        }
        bool InBody(long place, Helper own) {
            (int block, int index) = Miner.Unplace(place);
            for (int b = block, i = index; b >= 0; i = corpus.Blocks[b].Record.ParentAt, b = corpus.Blocks[b].Parent) {
                if (!bodies.TryGetValue(b, out List<(int From, int To, Helper Body)>? l)) continue;
                // (A helper's own first place is a call of it, where it is.)
                if (l.Any(r => r.From <= i && i < r.To && !(ReferenceEquals(r.Body, own) && place == own.Places[0]))) return true;
            }
            return false;
        }
        Dictionary<(int, int), (long Stmts, long Lines, int Places)> saved = [];
        Dictionary<(int, int), Dictionary<int, int>> used = [];
        foreach (Helper h in helpers) {
            foreach (long p in h.Places) {
                if (InBody(p, h)) continue;
                MinedBlock b = corpus.Blocks[Miner.Unplace(p).Block];
                (long st, long li, int n) = saved.GetValueOrDefault((b.Script, b.Worker));
                saved[(b.Script, b.Worker)] = (st + h.Run.Size - 1, li + h.Run.Lines - 1, n + 1);
                if (!used.TryGetValue((b.Script, b.Worker), out Dictionary<int, int>? u)) used[(b.Script, b.Worker)] = u = [];
                u[h.Rank] = u.GetValueOrDefault(h.Rank) + 1;
            }
        }
        StringBuilder sb = new("| script | worker | statements | lines | places folded | statements after | lines after (calls, without the helpers' bodies) | helpers (places) |\n|---|---|---|---|---|---|---|---|\n");
        foreach (((int s, int w), (long st, long li)) in size.OrderByDescending(kv => kv.Value.Lines).Take(12)) {
            (long ss, long sl, int n) = saved.GetValueOrDefault((s, w));
            string name = corpus.Blocks.First(b => b.Script == s && b.Worker == w).Worker.ToString("00", CultureInfo.InvariantCulture);
            string top = used.TryGetValue((s, w), out Dictionary<int, int>? u)
                ? string.Join(", ", u.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(6).Select(kv => string.Create(CultureInfo.InvariantCulture, $"R{kv.Key:000}×{kv.Value}")))
                : "";
            sb.Append(CultureInfo.InvariantCulture, $"| {Path.GetFileNameWithoutExtension(corpus.Scripts[s])} | W{name} | {st} | {li} | {n} | {st - ss} | {li - sl} | {top} |\n");
        }
        return sb.Append('\n').ToString();
    }

    private static string Shorten(string code, int max) {
        string[] l = code.Split('\n');
        return l.Length <= max + 1 ? code : string.Join('\n', l.Take(max)) + $"\n// … {l.Length - 1 - max} more lines\n";
    }

    private static string TagText(long tag) => tag switch {
        ShapeWalker.TagInt => "int",
        ShapeWalker.TagFloat => "float",
        ShapeWalker.TagPool => "pool constant",
        ShapeWalker.TagVar => "variable",
        ShapeWalker.TagElem => "array element",
        ShapeWalker.TagVarTarget => "assigned variable",
        ShapeWalker.TagElemTarget => "assigned element",
        _ => "?",
    };

    private static string CommonText(Param p) {
        long v = p.Common & 0x00ff_ffff_ffff_ffffL;
        return p.Tag switch {
            ShapeWalker.TagInt => StatementPrinterText((int)(uint)v),
            ShapeWalker.TagFloat => BitConverter.UInt32BitsToSingle((uint)v).ToString("R", CultureInfo.InvariantCulture) + "f",
            ShapeWalker.TagVar or ShapeWalker.TagVarTarget => string.Create(CultureInfo.InvariantCulture, $"V{v}"),
            _ => "(an element)",
        };
    }

    private static string Twins(Corpus corpus) {
        StringBuilder sb = new("\n# Folded ifs and their twins\n\n");
        sb.Append("| scripts | `if (false) { A } else { B }` | with twin | `A; if (false) { B }` | with twin | dead lines in those twins |\n|---|---|---|---|---|---|\n");
        foreach ((string name, Func<int, bool>? which) in new (string, Func<int, bool>?)[] {
            ("all", null),
            ("blitzball", s => DeadCommand.IsBlitzball(Path.GetFileNameWithoutExtension(corpus.Scripts[s]))),
            ("other", s => !DeadCommand.IsBlitzball(Path.GetFileNameWithoutExtension(corpus.Scripts[s]))),
        }) {
            Repeats.DeadTwins.Result r = Repeats.DeadTwins.Count(corpus, which);
            sb.Append(CultureInfo.InvariantCulture, $"| {name} | {r.FalseForms} | {r.FalseWithTwin} | {r.TrueForms} | {r.TrueWithTwin} | {r.TwinDeadLines} |\n");
        }
        return sb.ToString();
    }

    // ----- Verify ----------------------------------------------------------------------------------

    /// <summary>
    ///     Decompiles every script with and without the helpers; the decompiler keeps a worker's folds only if
    ///     its code, lowered again, is the original. Writes per worker the folds and lines, returns a summary.
    /// </summary>
    private static string VerifyFolds(VbfArchive archive, List<string> paths, List<InlineMacro> helpers, ParallelOptions jobs, string file, long bodies) {
        List<InlineMacro> with = [.. Compiler.InlineMethods.Game.All.Concat(helpers).OrderByDescending(m => m.Size).ThenBy(m => m.Name, StringComparer.Ordinal)];
        ConcurrentBag<(string Path, string Script, string Worker, string Signature, int Before, int After)> rows = [];
        ConcurrentDictionary<string, int> counts = new(StringComparer.Ordinal);
        Parallel.ForEach(paths, jobs, path => {
            try {
                ScriptFile f = ScriptFile.Parse(archive.Read(path));
                DecompiledScript before = ScriptDecompiler.Decompile(path, f);
                DecompiledScript after = ScriptDecompiler.Decompile(path, f, inline: with);
                foreach ((string k, int n) in after.Stats.Inline) {
                    if (k.StartsWith("inline Repeats.", StringComparison.Ordinal) || k.StartsWith("inline-fallback", StringComparison.Ordinal)) counts.AddOrUpdate(k, n, (_, v) => v + n);
                }
                foreach ((string k, int n) in before.Stats.Inline) {
                    if (k.StartsWith("inline-fallback", StringComparison.Ordinal)) counts.AddOrUpdate("baseline " + k, n, (_, v) => v + n);
                }
                for (int w = 0; w < after.Parts.Workers.Count; w++) {
                    int b = Lines(before.Parts.Workers[w].Body), a = Lines(after.Parts.Workers[w].Body);
                    rows.Add((path, Path.GetFileNameWithoutExtension(path), after.Parts.Workers[w].Name, Decompiler.Layout.WorkerSignature.Of(f.Script, after.Parts.Workers[w].Number), b, a));
                }
            }
            catch (Exception ex) when (ex is ScriptFormatException or InvalidOperationException or FormatException) {
                Console.Error.WriteLine($"{path}: {ex.Message}");
            }
        });
        StringBuilder tsv = new("script\tworker\tlines_before\tlines_after\n");
        foreach (var r in rows.OrderBy(r => r.Script, StringComparer.Ordinal).ThenBy(r => r.Worker, StringComparer.Ordinal)) {
            if (r.After != r.Before) tsv.Append(CultureInfo.InvariantCulture, $"{r.Script}\t{r.Worker}\t{r.Before}\t{r.After}\n");
        }
        File.WriteAllText(file, tsv.ToString());
        // Each distinct worker once (its first script), as the library writes it once.
        var distinct = rows.OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Worker, StringComparer.Ordinal).DistinctBy(r => r.Signature).ToList();
        long before = distinct.Sum(r => (long)r.Before), after = distinct.Sum(r => (long)r.After);
        StringBuilder sb = new();
        sb.Append(CultureInfo.InvariantCulture, $"\n# Verify: the first {helpers.Count} helpers folded by the decompiler\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Lines of the {distinct.Count} distinct workers {before} → {after} ({100.0 * (before - after) / Math.Max(1, before):0.0} % fewer); the helpers' bodies, written once, at most {bodies} lines more ({100.0 * (before - after - bodies) / Math.Max(1, before):0.0} % fewer in all).\n\n");
        foreach ((string k, int n) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sb.Append(CultureInfo.InvariantCulture, $"- {k}: {n}\n");
        sb.Append("\nLargest workers:\n\n| script | worker | lines before | after |\n|---|---|---|---|\n");
        foreach (var r in distinct.OrderByDescending(r => r.Before).Take(15)) sb.Append(CultureInfo.InvariantCulture, $"| {r.Script} | {r.Worker} | {r.Before} | {r.After} |\n");
        return sb.ToString();
    }

    private static int Lines(string s) => s.Count(c => c == '\n');

    // ----- Options ---------------------------------------------------------------------------------

    private static bool TakeFlag(List<string> args, string name) => args.Remove(name);

    private static int TakeInt(List<string> args, string name, int fallback) {
        int i = args.IndexOf(name);
        if (i < 0) return fallback;
        if (i + 1 >= args.Count || !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) throw new Program.UsageException($"{name} takes a number");
        args.RemoveRange(i, 2);
        return v;
    }
}
