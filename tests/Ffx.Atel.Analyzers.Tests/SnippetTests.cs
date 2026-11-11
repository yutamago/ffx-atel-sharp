// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text.RegularExpressions;
using Ffx.Atel.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using static Ffx.Atel.Analyzers.Tests.Harness;

namespace Ffx.Atel.Analyzers.Tests;

/// <summary>
///     The code the website shows, <c>site/snippets/</c> (docs/website.md, "Code on the site"), and the analyzer pages
///     the IDE links to. A line marked <c>// ATL1001</c> (or <c>// ATL1002, ATL1009</c>) must raise those rules, and no
///     other line may raise one; the snippets project turns a bad example's rule off with <c>#pragma</c>, so these
///     count suppressed diagnostics too. The language snippets also go through the Atel compiler, as a script or as a
///     mod on it.
/// </summary>
public sealed partial class SnippetTests {
    private const string Site = "https://yutamago.github.io/ffx-atel-sharp/reference/analyzers/";

    private static readonly string Root = FindRoot();
    private static readonly string Snippets = Path.Combine(Root, "site", "snippets");
    private static readonly string AnalyzerPages = Path.Combine(Root, "site", "src", "content", "docs", "reference", "analyzers");

    [GeneratedRegex(@"//\s*(?<ids>ATL[0-9]{4}(?:\s*,\s*ATL[0-9]{4})*)\b")]
    private static partial Regex Marker();

    [GeneratedRegex(@"^\s*//\s*(?<end>end)?region\b\s*(?<name>\S*)")]
    private static partial Regex RegionLine();

    private static string FindRoot() {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent) {
            if (File.Exists(Path.Combine(d.FullName, "ffx-atel-sharp.slnx"))) return d.FullName;
        }
        throw new InvalidOperationException("not inside the ffx-atel-sharp checkout");
    }

    private static List<string> Files(string dir) => [
        .. Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(Snippets, f).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Order(StringComparer.Ordinal),
    ];

    private static string Relative(string path) => Path.GetRelativePath(Snippets, path).Replace('\\', '/');

    private static List<DiagnosticDescriptor> Descriptors() => [
        .. typeof(Rules).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(DiagnosticDescriptor))
            .Select(f => (DiagnosticDescriptor)f.GetValue(null)!)
            .OrderBy(d => d.Id, StringComparer.Ordinal),
    ];

    [Fact]
    public void MarkedLinesRaiseTheirRulesAndNoOtherLineRaisesOne() {
        List<string> files = Files(Snippets);
        List<SyntaxTree> trees = [.. files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), new CSharpParseOptions(LanguageVersion.Latest), f, System.Text.Encoding.UTF8))];
        CSharpCompilation compilation = Compilation(trees);
        List<string> errors = [.. compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())];
        Assert.Empty(errors);

        List<string> want = [];
        foreach (string file in files) {
            string[] lines = File.ReadAllLines(file);
            for (int k = 0; k < lines.Length; k++) {
                if (Marker().Match(lines[k]) is { Success: true } m) {
                    want.AddRange(m.Groups["ids"].Value.Split(',').Select(id => $"{Relative(file)}:{k + 1} {id.Trim()}"));
                }
            }
        }
        List<string> got = [
            .. Analyze(compilation, reportSuppressed: true)
                .Where(d => d.Id.StartsWith("ATL", StringComparison.Ordinal) || d.Id == "AD0001")
                .Select(d => $"{Relative(d.Location.SourceTree!.FilePath)}:{d.Location.GetLineSpan().StartLinePosition.Line + 1} {d.Id}" + (d.Id == "AD0001" ? " " + d.GetMessage() : "")),
        ];
        List<string> missing = [.. want.Except(got).Order(StringComparer.Ordinal)];
        List<string> extra = [.. got.Except(want).Order(StringComparer.Ordinal)];
        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"marked but not raised:\n  {string.Join("\n  ", missing)}\nraised but not marked:\n  {string.Join("\n  ", extra)}");
    }

    /// <summary>Every rule has a bad example and its fix in <c>Analyzers/AtlNNNN.cs</c>, which its page shows.</summary>
    [Fact]
    public void EveryRuleHasABadExampleAndAFix() {
        foreach (DiagnosticDescriptor rule in Descriptors()) {
            string file = Path.Combine(Snippets, "Analyzers", $"Atl{rule.Id[3..]}.cs");
            Assert.True(File.Exists(file), $"{rule.Id}: no {Relative(file)}");
            HashSet<string> regions = Regions(file);
            Assert.True(regions.Contains("bad") && regions.Contains("fix"), $"{rule.Id}: {Relative(file)} has no 'bad' and 'fix' regions");
            Assert.Contains(File.ReadAllLines(file), l => Marker().Match(l) is { Success: true } m && m.Groups["ids"].Value.Contains(rule.Id, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RegionsAreClosedAndNamedOnce() {
        foreach (string file in Files(Snippets)) Regions(file);
    }

    private static HashSet<string> Regions(string file) {
        HashSet<string> names = new(StringComparer.Ordinal);
        Stack<string> open = [];
        foreach (string line in File.ReadAllLines(file)) {
            Match m = RegionLine().Match(line);
            if (!m.Success) continue;
            if (m.Groups["end"].Success) {
                Assert.True(open.Count > 0, $"{Relative(file)}: an endregion without a region");
                open.Pop();
            }
            else {
                string name = m.Groups["name"].Value;
                Assert.True(name.Length > 0 && names.Add(name), $"{Relative(file)}: region '{name}' is unnamed or named twice");
                open.Push(name);
            }
        }
        Assert.True(open.Count == 0, $"{Relative(file)}: region '{(open.Count > 0 ? open.Peek() : "")}' is not closed");
        return names;
    }

    /// <summary>The folders of <c>site/snippets/Language</c>: each is one script, its <c>Mod/</c> folder a mod on it.</summary>
    public static TheoryData<string> LanguageScripts() => [.. Directory.GetDirectories(Path.Combine(Snippets, "Language")).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(LanguageScripts))]
    public void LanguageSnippetsCompileWithTheAtelCompiler(string script) {
        string dir = Path.Combine(Snippets, "Language", script);
        string mod = Path.Combine(dir, "Mod");
        List<SyntaxTree> vanilla = [.. Directory.GetFiles(dir, "*.cs").Order(StringComparer.Ordinal).Select(f => ScriptCompiler.Parse(File.ReadAllText(f), f))];
        if (Directory.Exists(mod)) {
            List<SyntaxTree> modTrees = [.. Files(mod).Select(f => ScriptCompiler.Parse(File.ReadAllText(f), f))];
            ScriptCompiler.CompileMod(vanilla, modTrees);
        }
        else {
            ScriptCompiler.Compile(vanilla);
        }
    }

    /// <summary>The IDE's link on each diagnostic opens the rule's page on the website.</summary>
    [Fact]
    public void EveryRuleLinksToItsPage() {
        foreach (DiagnosticDescriptor rule in Descriptors()) {
            string id = rule.Id.ToLowerInvariant();
            Assert.Equal(Site + id + "/", rule.HelpLinkUri);
            Assert.True(File.Exists(Path.Combine(AnalyzerPages, id + ".mdx")) || File.Exists(Path.Combine(AnalyzerPages, id + ".md")), $"{rule.Id}: no page {id}.mdx");
        }
    }

    /// <summary>A page is for a rule that exists (ATL1011 is retired and has none).</summary>
    [Fact]
    public void EveryRulePageHasARule() {
        HashSet<string> ids = [.. Descriptors().Select(d => d.Id.ToLowerInvariant())];
        foreach (string page in Directory.GetFiles(AnalyzerPages).Select(Path.GetFileNameWithoutExtension).Where(n => n!.StartsWith("atl", StringComparison.Ordinal))!) {
            Assert.Contains(page, ids);
        }
    }
}
