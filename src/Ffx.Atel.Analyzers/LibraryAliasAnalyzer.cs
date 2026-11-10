// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Ffx.Atel.Analyzers;

/// <summary>
///     ATL3007: a library class (namespace <c>Library</c>, abstract, a worker) named as a worker in a script's
///     code (<c>MoveTalkDefaults.Run()</c>, <c>Call&lt;MoveTalkDefaults&gt;()</c>, a request) stands for the one
///     worker of that script deriving from it (the compiler's <c>WorkerTable</c>); none or several is an error.
///     The script is the namespace the code is in, its workers that namespace's classes (vanilla and mod alike).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LibraryAliasAnalyzer : DiagnosticAnalyzer {
    public const string LibraryNamespace = "Library";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.LibraryClassWorker];

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start => {
            if (AtelModel.Create(start.Compilation) is not AtelModel model) return;
            start.RegisterSyntaxNodeAction(c => Check(c, model), SyntaxKind.IdentifierName);
        });
    }

    private static void Check(SyntaxNodeAnalysisContext c, AtelModel model) {
        IdentifierNameSyntax id = (IdentifierNameSyntax)c.Node;
        // As a worker: the receiver of a member access (L.Run(), L.E5Async(), L.Pos) or a type argument (Call<L>()).
        // Library.SetDefaults counts as SetDefaults.
        SyntaxNode node = id;
        while (node.Parent is MemberAccessExpressionSyntax q && q.Name == node || node.Parent is QualifiedNameSyntax n && n.Right == node) node = node.Parent!;
        bool receiver = node.Parent is MemberAccessExpressionSyntax m && m.Expression == node;
        bool typeArgument = node.Parent is TypeArgumentListSyntax;
        if (!receiver && !typeArgument) return;
        if (c.SemanticModel.GetSymbolInfo(id, c.CancellationToken).Symbol is not INamedTypeSymbol type) return;
        if (!type.IsAbstract || !model.IsWorker(type) || type.ContainingNamespace?.ToDisplayString() != LibraryNamespace) return;
        INamespaceSymbol? script = c.ContainingSymbol?.ContainingNamespace;
        if (script is null || script.IsGlobalNamespace || script.ToDisplayString() == LibraryNamespace) return;
        List<string> deriving = [.. script.GetTypeMembers().Where(t => !t.IsAbstract && Derives(t, type)).Select(t => t.Name).Distinct().OrderBy(n => n, System.StringComparer.Ordinal)];
        if (deriving.Count == 1) return;
        string why = deriving.Count == 0 ? "none does" : $"{deriving.Count} do ({string.Join(", ", deriving)}): name the worker";
        c.ReportDiagnostic(Diagnostic.Create(Rules.LibraryClassWorker, id.GetLocation(), type.Name, why));
    }

    private static bool Derives(INamedTypeSymbol type, INamedTypeSymbol library) {
        for (INamedTypeSymbol? t = type.BaseType; t is not null; t = t.BaseType) {
            if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, library)) return true;
        }
        return false;
    }
}
