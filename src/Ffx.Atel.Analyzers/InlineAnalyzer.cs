// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Ffx.Atel.Analyzers;

/// <summary>
///     Inline methods (<c>[Inline]</c>, docs/language.md "Inline methods"): one that calls itself, directly or
///     through others (ATL3003), and an argument that does more than give a value (ATL3004).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InlineAnalyzer : DiagnosticAnalyzer {
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.InlineRecursion, Rules.InlineArgument];

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start => {
            if (AtelModel.Create(start.Compilation) is not AtelModel model || model.InlineAttribute is null) return;
            ConcurrentBag<(IMethodSymbol From, IMethodSymbol To, Location Where)> edges = [];
            start.RegisterOperationAction(c => {
                IInvocationOperation i = (IInvocationOperation)c.Operation;
                if (!model.IsInline(i.TargetMethod)) return;
                foreach (IArgumentOperation a in i.Arguments) {
                    if (a.ArgumentKind == ArgumentKind.Explicit && HasEffect(a.Value)) {
                        c.ReportDiagnostic(Diagnostic.Create(Rules.InlineArgument, a.Value.Syntax.GetLocation(), $"{i.TargetMethod.ContainingType.Name}.{i.TargetMethod.Name}"));
                    }
                }
                if (c.ContainingSymbol is IMethodSymbol caller && model.IsInline(caller)) {
                    edges.Add((caller.OriginalDefinition, i.TargetMethod.OriginalDefinition, i.Syntax.GetLocation()));
                }
            }, OperationKind.Invocation);
            start.RegisterCompilationEndAction(c => {
                foreach ((IMethodSymbol from, IMethodSymbol to, Location where) in edges.OrderBy(e => e.Where.SourceTree?.FilePath, System.StringComparer.Ordinal).ThenBy(e => e.Where.SourceSpan.Start)) {
                    if (PathTo(to, from, edges) is List<IMethodSymbol> back) {
                        string chain = string.Join(" → ", new[] { from }.Concat(back).Select(m => $"{m.ContainingType.Name}.{m.Name}"));
                        c.ReportDiagnostic(Diagnostic.Create(Rules.InlineRecursion, where, chain));
                    }
                }
            });
        });
    }

    /// <summary>Whether an argument does something besides giving a value: a call (but <c>Text[n]</c>, <c>Text.New</c>, the no-code conversions), an assignment, an increment.</summary>
    private static bool HasEffect(IOperation value) => value.DescendantsAndSelf().Any(o => o switch {
        IInvocationOperation call => call.TargetMethod.Name is not ("New" or "AsBool" or "AsInt") || call.TargetMethod.ContainingType.Name is not ("TextTable" or "Worker"),
        IAssignmentOperation or IIncrementOrDecrementOperation or IConditionalOperation or IAwaitOperation => true,
        IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr } => true,
        _ => false,
    });

    /// <summary>The chain of inline calls from <paramref name="from"/> to <paramref name="to"/>; null if there is none.</summary>
    private static List<IMethodSymbol>? PathTo(IMethodSymbol from, IMethodSymbol to, IEnumerable<(IMethodSymbol From, IMethodSymbol To, Location Where)> edges) {
        Dictionary<IMethodSymbol, IMethodSymbol?> parent = new(SymbolEqualityComparer.Default) { [from] = null };
        Queue<IMethodSymbol> queue = new([from]);
        while (queue.Count > 0) {
            IMethodSymbol m = queue.Dequeue();
            if (SymbolEqualityComparer.Default.Equals(m, to)) {
                List<IMethodSymbol> path = [];
                for (IMethodSymbol? p = m; p is not null; p = parent[p]) path.Add(p);
                path.Reverse();
                return path;
            }
            foreach ((IMethodSymbol f, IMethodSymbol callee, Location _) in edges) {
                if (SymbolEqualityComparer.Default.Equals(f, m) && !parent.ContainsKey(callee)) {
                    parent[callee] = m;
                    queue.Enqueue(callee);
                }
            }
        }
        return null;
    }
}
