// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Ffx.Atel.Analyzers.CodeFixes;
using Ffx.Atel.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace Ffx.Atel.Analyzers.Tests;

/// <summary>The code fixes, applied in a workspace; and the analyzers in <c>atel compile --check</c>.</summary>
public sealed class CodeFixTests {
    /// <summary>Applies the fix titled <paramref name="title"/> to the first <paramref name="id"/> diagnostic and returns the new source.</summary>
    private static async System.Threading.Tasks.Task<string> Fix(string source, string id, string title) {
        source = Harness.Usings + source;
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using AdhocWorkspace workspace = new();
        Compilation template = Harness.Compilation(source);
        Project project = workspace.AddProject("Test", LanguageNames.CSharp)
            .WithCompilationOptions(template.Options)
            .WithParseOptions(template.SyntaxTrees.First().Options)
            .WithMetadataReferences(template.References);
        Document document = project.AddDocument("Test.cs", SourceText.From(source));
        Compilation compilation = (await document.Project.GetCompilationAsync(cancel))!;
        ImmutableArray<Diagnostic> found = Harness.Analyze(compilation);
        Diagnostic d = found.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException(string.Join("\n", found));
        List<CodeAction> actions = [];
        CodeFixContext context = new(document, d, (a, _) => actions.Add(a), cancel);
        await new AtelCodeFixes().RegisterCodeFixesAsync(context);
        CodeAction action = actions.Single(a => a.Title == title);
        ImmutableArray<CodeActionOperation> operations = await action.GetOperationsAsync(cancel);
        Solution changed = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        SourceText text = await changed.GetDocument(document.Id)!.GetTextAsync(cancel);
        // The formatter writes the platform's line ending (\r\n on Windows); compare with \n.
        return text.ToString()[Harness.Usings.Length..].Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task ShortCircuitBecomesBothSides() {
        string fixedSource = await Fix("class W : Worker1 { int I; void E() { if (I > 1 && I < 3) { } } }", "ATL1011", "Use '&' (evaluates both sides)");
        Assert.Equal("class W : Worker1 { int I; void E() { if (I > 1 & I < 3) { } } }", fixedSource);
    }

    [Fact]
    public async System.Threading.Tasks.Task DoubleLiteralBecomesFloat() {
        string fixedSource = await Fix("class W : Worker1 { float F; void E() { F = (float)1.5; } }", "ATL1009", "Make it a float literal");
        Assert.Equal("class W : Worker1 { float F; void E() { F = (float)1.5f; } }", fixedSource);
    }

    [Fact]
    public async System.Threading.Tasks.Task CheckedIsRemoved() {
        string fixedSource = await Fix("class W : Worker1 { int I; void E() { I = checked(I + 1); } }", "ATL1010", "Remove 'checked'");
        Assert.Equal("class W : Worker1 { int I; void E() { I = (I + 1); } }", fixedSource);
    }

    private const string Request = """
        class W : Worker1
        {
            public static V Lulu = null!;
            async Task EAsync()
            {
                Lulu.LookAsync();
            }
        }
        class V : Worker1 { public async Task LookAsync() { } }
        """;

    [Fact]
    public async System.Threading.Tasks.Task RequestIsDiscarded() =>
        Assert.Contains("        _ = Lulu.LookAsync();\n", await Fix(Request, "ATL2001", "Queue it: '_ ='"), StringComparison.Ordinal);

    [Fact]
    public async System.Threading.Tasks.Task RequestIsAwaited() =>
        Assert.Contains("        await Lulu.LookAsync();\n", await Fix(Request, "ATL2001", "Wait for it: 'await'"), StringComparison.Ordinal);

    /// <summary><c>atel compile --check</c> runs the analyzers.</summary>
    [Fact]
    public void CheckReportsAtlDiagnostics() {
        string source = """
            using Ffx;
            using Ffx.Atel;
            [Event("test")]
            [Workers(typeof(W00))]
            public static partial class Test { }
            public sealed class W00 : Worker1
            {
                [Register(0)] public int I0;
                [Entry(0)]
                void E0()
                {
                    if (I0 > 1 && I0 < 3) { }
                    long x = 1;
                    I0 = (int)x;
                }
            }
            """;
        IReadOnlyList<Diagnostic> found = ScriptCompiler.Check([ScriptCompiler.Parse(source)]);
        Assert.Contains(found, d => d.Id == "ATL1011" && d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(found, d => d.Id == "ATL1009" && d.Severity == DiagnosticSeverity.Error);
    }
}
