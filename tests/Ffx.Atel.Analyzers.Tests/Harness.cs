// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Ffx.Atel.Analyzers.Tests;

/// <summary>
///     A small analyzer test harness: source with markup <c>{|ATL1001:text|}</c> around each expected
///     diagnostic, compiled against the reference assembly and a few .NET assemblies (so that the
///     rules about them have something to find), with every Atel analyzer attached.
/// </summary>
internal static partial class Harness {
    /// <summary>What every test source starts with.</summary>
    public const string Usings = "using Ffx;\nusing Ffx.Atel;\n";

    private static readonly Lazy<MetadataReference[]> References = new(() => {
        string[] platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator);
        string[] wanted = ["System.Runtime.dll", "System.Private.CoreLib.dll", "System.Collections.dll", "System.Linq.dll", "System.Console.dll"];
        return [
            .. platform.Where(p => wanted.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)).Select(p => MetadataReference.CreateFromFile(p)),
            MetadataReference.CreateFromFile(typeof(Worker).Assembly.Location),
        ];
    });

    public static CSharpCompilation Compilation(params string[] sources) =>
        Compilation(sources.Select((s, k) => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest), $"Test{k}.cs", Encoding.UTF8)));

    public static CSharpCompilation Compilation(IEnumerable<SyntaxTree> trees, params MetadataReference[] more) => CSharpCompilation.Create(
        "AtelTest",
        trees,
        [.. References.Value, .. more],
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
            .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["CS0162"] = ReportDiagnostic.Suppress }));

    /// <summary>Every diagnostic, the analyzers' (all severities) and C#'s (warnings and errors).</summary>
    public static ImmutableArray<Diagnostic> Analyze(Compilation compilation) {
        CompilationWithAnalyzers analyzed = compilation.WithAnalyzers(AtelAnalyzers.All,
            new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null, concurrentAnalysis: true, logAnalyzerExecutionTime: false));
        return [.. analyzed.GetAllDiagnosticsAsync().GetAwaiter().GetResult().Where(d => d.Id.StartsWith("ATL", StringComparison.Ordinal) || d.Id == "AD0001" || d.Severity >= DiagnosticSeverity.Warning)];
    }

    [GeneratedRegex(@"\{\|(?<id>[A-Z]+[0-9]+):|\|\}")]
    private static partial Regex Markup();

    /// <summary>Strips the markup and returns the expected (id, span) pairs.</summary>
    public static (string Source, List<(string Id, TextSpan Span)> Expected) Parse(string markup) {
        StringBuilder text = new();
        List<(string Id, TextSpan Span)> expected = [];
        Stack<(string Id, int Start)> open = [];
        int last = 0;
        foreach (Match m in Markup().Matches(markup)) {
            text.Append(markup, last, m.Index - last);
            last = m.Index + m.Length;
            if (m.Groups["id"].Success) {
                open.Push((m.Groups["id"].Value, text.Length));
            }
            else {
                (string id, int start) = open.Pop();
                expected.Add((id, TextSpan.FromBounds(start, text.Length)));
            }
        }
        text.Append(markup, last, markup.Length - last);
        return (text.ToString(), expected);
    }

    /// <summary>Checks that the marked diagnostics, and no others, are reported (C# errors fail the test too).</summary>
    /// <param name="usings">More <c>using</c> directives.</param>
    /// <param name="ignore">Diagnostic ids the test does not look at.</param>
    public static void Verify(string markup, string usings = "", params string[] ignore) {
        (string source, List<(string Id, TextSpan Span)> expected) = Parse(Usings + usings + markup);
        IEnumerable<Diagnostic> found = Analyze(Compilation(source))
            .Where(d => !ignore.Contains(d.Id) && (d.Id.StartsWith("ATL", StringComparison.Ordinal) || d.Severity == DiagnosticSeverity.Error || d.Id == "AD0001"));
        List<string> want = [.. expected.Select(e => Describe(source, e.Id, e.Span)).Order(StringComparer.Ordinal)];
        List<string> got = [.. found.Select(d => Describe(source, d.Id, d.Location.SourceSpan) + (d.Id.StartsWith("ATL", StringComparison.Ordinal) ? "" : " " + d.GetMessage())).Order(StringComparer.Ordinal)];
        Assert.Equal(want, got);
    }

    /// <summary>The diagnostics of one id in a source without markup.</summary>
    public static List<Diagnostic> Find(string source, string id) => [.. Analyze(Compilation(Usings + source)).Where(d => d.Id == id)];

    private static string Describe(string source, string id, TextSpan span) {
        int line = source.Take(span.Start).Count(c => c == '\n') + 1;
        return $"{id} line {line}: {source.Substring(span.Start, span.Length)}";
    }
}
