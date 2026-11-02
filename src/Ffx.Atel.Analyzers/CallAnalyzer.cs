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
///     <c>call</c>: recursion (ATL3001) and depth (ATL3002) over the calls the compilation makes to
///     its own functions and helpers (docs/plan.md, "Three kinds of code"). A function worker
///     (<c>Worker0</c>, <c>Call&lt;W&gt;()</c>) is one node; every other method its own.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CallAnalyzer : DiagnosticAnalyzer {
    /// <summary>Return addresses a worker level keeps (<c>Script_Run</c>, level <c>+0x1d</c>).</summary>
    public const int MaxDepth = 3;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.Recursion, Rules.CallDepth];

    private readonly struct Edge(ISymbol from, ISymbol to, Location where) {
        public ISymbol From { get; } = from;
        public ISymbol To { get; } = to;
        public Location Where { get; } = where;
    }

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start => {
            if (AtelModel.Create(start.Compilation) is not AtelModel model) return;
            ConcurrentBag<Edge> edges = [];
            start.RegisterOperationAction(c => {
                IInvocationOperation i = (IInvocationOperation)c.Operation;
                if (c.ContainingSymbol is not IMethodSymbol caller) return;
                ISymbol? callee = null;
                if (model.IsWorkerMember(i.TargetMethod, "Call") && i.TargetMethod.TypeArguments.Length == 1) {
                    callee = i.TargetMethod.TypeArguments[0].OriginalDefinition;
                }
                else if (model.IsCallable(i.TargetMethod)) {
                    callee = Node(model, i.TargetMethod.OriginalDefinition);
                }
                if (callee is not null) edges.Add(new Edge(Node(model, caller.OriginalDefinition), callee, i.Syntax.GetLocation()));
            }, OperationKind.Invocation);
            start.RegisterCompilationEndAction(c => Report(c, edges));
        });
    }

    /// <summary>The node a method's code belongs to: its function worker, or itself.</summary>
    private static ISymbol Node(AtelModel model, IMethodSymbol method) =>
        model.IsFunctionWorker(method.ContainingType) ? method.ContainingType.OriginalDefinition : method;

    private static void Report(CompilationAnalysisContext c, IEnumerable<Edge> all) {
        List<Edge> edges = [.. all.OrderBy(e => e.Where.SourceTree?.FilePath, System.StringComparer.Ordinal).ThenBy(e => e.Where.SourceSpan.Start)];
        if (edges.Count == 0) return;
        Dictionary<ISymbol, List<Edge>> outgoing = new(SymbolEqualityComparer.Default);
        foreach (Edge e in edges) {
            if (!outgoing.TryGetValue(e.From, out List<Edge>? list)) outgoing[e.From] = list = [];
            list.Add(e);
        }
        List<Edge> Out(ISymbol n) => outgoing.TryGetValue(n, out List<Edge>? l) ? l : [];

        // ATL3001: an edge whose target reaches back to its source.
        HashSet<ISymbol> cyclic = new(SymbolEqualityComparer.Default);
        foreach (Edge e in edges) {
            if (PathTo(e.To, e.From, Out) is List<ISymbol> back) {
                cyclic.Add(e.From);
                string chain = string.Join(" → ", new[] { e.From }.Concat(back).Select(Name));
                c.ReportDiagnostic(Diagnostic.Create(Rules.Recursion, e.Where, chain));
            }
        }

        // ATL3002: from code that is not itself called, the deepest chain through each call.
        HashSet<ISymbol> called = new(edges.Select(e => e.To), SymbolEqualityComparer.Default);
        Dictionary<ISymbol, List<ISymbol>> deepest = new(SymbolEqualityComparer.Default);
        List<ISymbol> Chain(ISymbol n, HashSet<ISymbol> onPath) {
            if (deepest.TryGetValue(n, out List<ISymbol>? known)) return known;
            List<ISymbol> best = [n];
            if (onPath.Add(n)) {
                foreach (Edge e in Out(n)) {
                    if (onPath.Contains(e.To) || cyclic.Contains(e.To)) continue;
                    List<ISymbol> sub = Chain(e.To, onPath);
                    if (sub.Count + 1 > best.Count) best = [n, .. sub];
                }
                onPath.Remove(n);
            }
            return deepest[n] = best;
        }
        foreach (Edge e in edges) {
            if (called.Contains(e.From) || cyclic.Contains(e.To)) continue;
            List<ISymbol> chain = Chain(e.To, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
            if (chain.Count > MaxDepth) {
                c.ReportDiagnostic(Diagnostic.Create(Rules.CallDepth, e.Where, chain.Count, string.Join(" → ", new[] { e.From }.Concat(chain).Select(Name))));
            }
        }
    }

    private static List<ISymbol>? PathTo(ISymbol from, ISymbol to, System.Func<ISymbol, List<Edge>> outgoing) {
        Dictionary<ISymbol, ISymbol?> parent = new(SymbolEqualityComparer.Default) { [from] = null };
        Queue<ISymbol> queue = new([from]);
        while (queue.Count > 0) {
            ISymbol n = queue.Dequeue();
            if (SymbolEqualityComparer.Default.Equals(n, to)) {
                List<ISymbol> path = [];
                for (ISymbol? p = n; p is not null; p = parent[p]) path.Add(p);
                path.Reverse();
                return path;
            }
            foreach (Edge e in outgoing(n)) {
                if (!parent.ContainsKey(e.To)) {
                    parent[e.To] = n;
                    queue.Enqueue(e.To);
                }
            }
        }
        return null;
    }

    private static string Name(ISymbol s) => s is IMethodSymbol m ? $"{m.ContainingType.Name}.{m.Name}" : s.Name;
}
