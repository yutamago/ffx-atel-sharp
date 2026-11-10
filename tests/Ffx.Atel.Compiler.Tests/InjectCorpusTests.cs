// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>
///     <c>[Inject]</c> anchored on members over every shipped script: the decompiled source writes most natives as
///     members (<c>Self.Hp = 1;</c>, <c>Yaw = …</c>, <c>Camera.Eye.Position = …</c>), and an anchor names them. Needs
///     <c>FFX_GAME_DIR</c>; takes the corpus lock.
/// </summary>
public sealed partial class InjectCorpusTests(ITestOutputHelper output) {
    private const int MethodsPerWorker = 6;

    /// <summary>
    ///     In the first methods of every worker, a place of a property in the middle of the method: an inject before it
    ///     named by the member (<c>"Self.Hp"</c>, its n-th use) and one named by its native (<c>"Btl.SetActorProperty"</c>,
    ///     its m-th call) land on the same statement, one after it compiles, and the package links onto the game's file.
    ///     The statements whose code is not in one piece (the compiler says so, as for a call) are counted, not failures.
    /// </summary>
    [Fact]
    public void MemberAnchorsResolveOnEveryScript() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        using VbfArchive archive = GameData.OpenArchiveExclusive(dir!);
        List<string> paths = GameData.AllScriptPaths(archive);
        object read = new();
        ConcurrentBag<string> failures = [];
        ConcurrentDictionary<string, int> roots = new(StringComparer.Ordinal);
        int linked = 0, without = 0, anchors = 0, split = 0, tooLarge = 0;
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = 4 }, path => {
            byte[] bytes;
            lock (read) bytes = archive.Read(path);
            try {
                ScriptFile file = ScriptFile.Parse(bytes);
                List<SyntaxTree> trees = [.. ScriptDecompiler.Decompile(path, file).Files().Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath))];
                CompiledScript vanilla = ScriptCompiler.Compile(trees);
                List<Anchor> found = Anchors(vanilla.NativeSites);
                if (found.Count == 0) {
                    Interlocked.Increment(ref without);
                    return;
                }
                // Compile, leaving out the anchors whose statement is not in one piece, until it compiles.
                HashSet<int> left = [];
                CompiledMod? mod = null;
                (string Text, Dictionary<int, int> Lines) source = default;
                while (mod is null) {
                    source = ModSource(found, left);
                    try {
                        mod = ScriptCompiler.CompileMod(trees, [ScriptCompiler.Parse(source.Text, "mod/Inject.cs")]);
                    }
                    catch (CompileException ex) when (LineOf(ex.Message) is int line && source.Lines.TryGetValue(line, out int k)) {
                        if (!ex.Message.Contains("not in one piece", StringComparison.Ordinal) && !ex.Message.Contains("compiles to no code", StringComparison.Ordinal)) {
                            failures.Add($"{path}: {found[k].Text} #{found[k].Occurrence} in {found[k].Class}.{found[k].Method}: {ex.Message}");
                        }
                        left.Add(k);
                        Interlocked.Increment(ref split);
                        if (left.Count == found.Count) return;
                    }
                }
                Dictionary<string, EntryPatch> patches = mod.Package.Patches.ToDictionary(p => p.Name, StringComparer.Ordinal);
                for (int k = 0; k < found.Count; k++) {
                    if (left.Contains(k)) continue;
                    Anchor a = found[k];
                    InjectPoint byMember = patches[$"M{k}"].At!, byNative = patches[$"N{k}"].At!;
                    if ((byMember.Start, byMember.End) != (byNative.Start, byNative.End)) {
                        failures.Add($"{path}: {a.Class}.{a.Method}: {a.Text} #{a.Occurrence} is {byMember.Start:x}-{byMember.End:x}, {a.Native} #{a.NativeOccurrence} {byNative.Start:x}-{byNative.End:x}");
                    }
                    Interlocked.Increment(ref anchors);
                    roots.AddOrUpdate(a.Text.Split('.')[0], 1, (_, n) => n + 1);
                }
                mod.Package.File = path;
                LinkResult r = ScriptLinker.Link(bytes, [new ModScript("member-anchors", ScriptPackage.FromJson(mod.Package.ToJson()))]);
                // A worker with more labels than a copy of the method leaves room for (blitzball's worker 36, as for
                // call anchors; docs/language.md, "Injecting code"): its injects left out.
                if (r.Messages.FirstOrDefault(m => m.Severity == LinkSeverity.Error && TooManyLabels().IsMatch(m.Text)) is LinkMessage full) {
                    int worker = int.Parse(TooManyLabels().Match(full.Text).Groups[1].ValueSpan, CultureInfo.InvariantCulture);
                    Interlocked.Add(ref tooLarge, mod.Package.Patches.RemoveAll(p => p.Worker == worker) / 3);
                    r = ScriptLinker.Link(bytes, [new ModScript("member-anchors", ScriptPackage.FromJson(mod.Package.ToJson()))]);
                }
                if (!r.Changed || r.Messages.Any(m => m.Severity == LinkSeverity.Error)) {
                    failures.Add($"{path}: {string.Join("; ", r.Messages.Where(m => m.Severity == LinkSeverity.Error))}");
                    return;
                }
                Interlocked.Increment(ref linked);
            }
            catch (Exception ex) when (ex is ScriptFormatException or CompileException or InvalidOperationException or ArgumentException) {
                failures.Add($"{path}: {ex.GetType().Name}: {ex.Message}");
            }
        });
        output.WriteLine($"{linked} scripts linked with member anchors in {anchors} methods ({string.Join(", ", roots.OrderByDescending(x => x.Value).Take(12).Select(x => $"{x.Key} {x.Value}"))}); {without} without a property to anchor on; {split} anchor(s) on statements not in one piece left out, {tooLarge} in workers with too many labels to copy a method");
        foreach (string f in failures.Order(StringComparer.Ordinal).Take(40)) output.WriteLine(f);
        Assert.Empty(failures);
        Assert.True(anchors > 10_000, $"only {anchors} member anchors");
    }

    /// <summary>An anchor to try: the member's place, its name and which of its places, and the native's name and which of its.</summary>
    private sealed record Anchor(string Namespace, string Class, string Method, string Text, int Occurrence, string Native, int NativeOccurrence);

    /// <summary>In each worker's first methods, the place of a property in the middle of the method, named in full.</summary>
    private static List<Anchor> Anchors(IReadOnlyList<NativeSite> sites) {
        List<Anchor> found = [];
        IEnumerable<IGrouping<MethodDeclarationSyntax, NativeSite>> byMethod = sites
            .Where(s => s.Node is not InvocationExpressionSyntax)
            .GroupBy(s => s.Node.FirstAncestorOrSelf<MethodDeclarationSyntax>()!)
            .Where(g => g.Key is not null);
        foreach (IGrouping<ClassDeclarationSyntax, IGrouping<MethodDeclarationSyntax, NativeSite>> cls in byMethod.GroupBy(g => g.Key.FirstAncestorOrSelf<ClassDeclarationSyntax>()!)) {
            string ns = cls.Key.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>()?.Name.ToString() ?? "";
            foreach (IGrouping<MethodDeclarationSyntax, NativeSite> method in cls.OrderBy(g => g.Key.SpanStart).Take(MethodsPerWorker)) {
                List<NativeSite> own = [.. method.DistinctBy(s => s.Node).OrderBy(s => s.Node.SpanStart)];
                NativeSite site = own[own.Count / 2];
                string? text = MemberText((ExpressionSyntax)site.Node);
                if (text is null || !ScriptCompiler.AnchorIsProperty(text)) continue;
                if (!ScriptCompiler.AnchorNatives(text).Any(k => k.Id == site.Id && (k.Constant is null || k.Constant == site.Constant))) continue;
                string native = ScriptCompiler.Natives[site.Id]!.Name;
                int n = ScriptCompiler.AnchorPlaces(text, method.Key, sites).IndexOf(site.Node) + 1;
                int m = ScriptCompiler.AnchorPlaces(native, method.Key, sites).IndexOf(site.Node) + 1;
                if (n < 1 || m < 1) throw new InvalidOperationException($"{cls.Key.Identifier}.{method.Key.Identifier}: {site.Node} is no place of {text} or {native}");
                found.Add(new Anchor(ns, cls.Key.Identifier.ValueText, method.Key.Identifier.ValueText, text, n, native, m));
            }
        }
        return found;
    }

    /// <summary>The member as an anchor names it: <c>Self.Hp</c>, <c>Yaw</c>, <c>Camera.Eye.Position</c>, <c>Actors.Hp</c> for <c>Actors[…].Hp</c>.</summary>
    private static string? MemberText(ExpressionSyntax e) => e switch {
        MemberAccessExpressionSyntax { Expression: ElementAccessExpressionSyntax { Expression: IdentifierNameSyntax list } } m => $"{list.Identifier.ValueText}.{m.Name.Identifier.ValueText}",
        _ => ScriptCompiler.Dotted(e),
    };

    /// <summary>The mod: per anchor, an inject before the member's place (<c>Mk</c>), one before the native's (<c>Nk</c>) and one after the member's (<c>Ak</c>); the line of each attribute.</summary>
    private static (string Text, Dictionary<int, int> Lines) ModSource(List<Anchor> found, HashSet<int> left) {
        StringBuilder s = new();
        Dictionary<int, int> lines = [];
        int line = 1;
        void Line(string text, int? anchor = null) {
            s.Append(text).Append('\n');
            if (anchor is int k) lines[line] = k;
            line++;
        }
        Line("using Ffx;");
        Line("using Ffx.Atel;");
        foreach (IGrouping<(string Namespace, string Class), (Anchor A, int K)> cls in found.Select((a, k) => (A: a, K: k)).Where(x => !left.Contains(x.K)).GroupBy(x => (x.A.Namespace, x.A.Class))) {
            Line($"namespace {cls.Key.Namespace} {{ partial class {cls.Key.Class} {{");
            foreach ((Anchor a, int k) in cls) {
                Line(string.Create(CultureInfo.InvariantCulture, $"[Inject(nameof({a.Method}), Before = \"{a.Text}\", Occurrence = {a.Occurrence})] void M{k}() {{ }}"), k);
                Line(string.Create(CultureInfo.InvariantCulture, $"[Inject(nameof({a.Method}), Before = \"{a.Native}\", Occurrence = {a.NativeOccurrence})] void N{k}() {{ }}"), k);
                Line(string.Create(CultureInfo.InvariantCulture, $"[Inject(nameof({a.Method}), After = \"{a.Text}\", Occurrence = {a.Occurrence})] void A{k}() {{ }}"), k);
            }
            Line("} }");
        }
        return (s.ToString(), lines);
    }

    private static int? LineOf(string message) =>
        Position().Match(message) is { Success: true } m ? int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture) : null;

    [GeneratedRegex(@"worker (\d+): operand \d+ does not fit 16 bits")]
    private static partial Regex TooManyLabels();

    [GeneratedRegex(@"^mod/Inject\.cs\((\d+),")]
    private static partial Regex Position();
}
