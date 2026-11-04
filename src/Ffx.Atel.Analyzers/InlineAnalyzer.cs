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
///     through others (ATL3003), an argument that does more than give a value (ATL3004), a <c>[Constant]</c>
///     parameter's argument that is no constant (ATL3005), and a <c>Wait.Seconds</c> shorter than a frame (ATL3006).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InlineAnalyzer : DiagnosticAnalyzer {
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.InlineRecursion, Rules.InlineArgument, Rules.ConstantArgument, Rules.WaitBelowOneFrame];

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
                    // An [Eager] parameter's argument is evaluated once, first: it may call.
                    bool eager = a.Parameter?.GetAttributes().Any(x => x.AttributeClass?.Name == "EagerAttribute") == true;
                    if (a.ArgumentKind == ArgumentKind.Explicit && HasEffect(a.Value, eager)) {
                        c.ReportDiagnostic(Diagnostic.Create(Rules.InlineArgument, a.Value.Syntax.GetLocation(), $"{i.TargetMethod.ContainingType.Name}.{i.TargetMethod.Name}"));
                    }
                    else if (a.ArgumentKind == ArgumentKind.Explicit && IsConstantParameter(a.Parameter)) {
                        CheckConstant(c, i.TargetMethod, a);
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

    /// <summary>A parameter with <c>[Constant]</c> (<c>Ffx.Atel.ConstantAttribute</c>).</summary>
    private static bool IsConstantParameter(IParameterSymbol? p) => p?.GetAttributes().Any(x =>
        x.AttributeClass is { Name: "ConstantAttribute", ContainingNamespace: { Name: "Atel", ContainingNamespace.Name: "Ffx" } }) == true;

    /// <summary>ATL3005 for an argument without a constant value; for <c>Wait.Seconds</c>, ATL3006 when it is no frame.</summary>
    private static void CheckConstant(OperationAnalysisContext c, IMethodSymbol method, IArgumentOperation a) {
        Optional<object?> value = a.Value.ConstantValue;
        if (!value.HasValue || value.Value is not (float or double or int or long or short or byte or sbyte or ushort or uint)) {
            c.ReportDiagnostic(Diagnostic.Create(Rules.ConstantArgument, a.Value.Syntax.GetLocation(), $"{method.ContainingType.Name}.{method.Name}"));
            return;
        }
        if (method.Name != "Seconds" || method.ContainingType is not { Name: "Wait", ContainingNamespace: { Name: "Ffx", ContainingNamespace.IsGlobalNamespace: true } }) return;
        double seconds = System.Convert.ToDouble(value.Value, System.Globalization.CultureInfo.InvariantCulture);
        if (FramesOf(seconds) is null) {
            string frames = double.IsNaN(seconds) || double.IsInfinity(seconds) ? "no number of" : (seconds * 30).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            c.ReportDiagnostic(Diagnostic.Create(Rules.WaitBelowOneFrame, a.Value.Syntax.GetLocation(), a.Value.Syntax.ToString(), frames));
        }
    }

    /// <summary>The compiler's count (StatementReader.FramesOf): seconds × 30, halves away from zero; null below 1 frame or past int.</summary>
    internal static int? FramesOf(double seconds) {
        double frames = System.Math.Round(seconds * 30, System.MidpointRounding.AwayFromZero);
        return !double.IsNaN(frames) && !double.IsInfinity(frames) && frames >= 1 && frames <= int.MaxValue ? (int)frames : null;
    }

    /// <summary>
    ///     Whether an argument does something besides giving a value: a call (but <c>Text[n]</c>, <c>Text.New</c>,
    ///     the no-code conversions; and any call for an <c>[Eager]</c> parameter), an assignment, an increment.
    /// </summary>
    private static bool HasEffect(IOperation value, bool eager) => value.DescendantsAndSelf().Any(o => o switch {
        IInvocationOperation call => !eager && (call.TargetMethod.Name is not ("New" or "AsBool" or "AsInt") || call.TargetMethod.ContainingType.Name is not ("TextTable" or "Worker")),
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
