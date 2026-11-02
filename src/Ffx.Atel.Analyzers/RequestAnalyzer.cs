// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Ffx.Atel.Analyzers;

/// <summary>Requests between workers (docs/plan.md, "Requests between workers"), ATL2001-ATL2007.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RequestAnalyzer : DiagnosticAnalyzer {
    /// <summary>The level entry 0 (<c>Init</c>) runs at.</summary>
    public const int InitLevel = 7;

    /// <summary>The level entry 1 (<c>Main</c>) runs at.</summary>
    public const int MainLevel = 0;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [
        Rules.RequestNotAwaited, Rules.RequestToNonAsync, Rules.AwaitNothing, Rules.SelfRequestDeadlock, Rules.SelfRequest,
        Rules.Priority, Rules.AsyncReturn,
    ];

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start => {
            if (AtelModel.Create(start.Compilation) is not AtelModel model) return;
            start.RegisterOperationAction(c => ExpressionStatement(c, model), OperationKind.ExpressionStatement);
            start.RegisterOperationAction(c => Invocation(c, model), OperationKind.Invocation);
            start.RegisterOperationAction(c => Await(c, model), OperationKind.Await);
            start.RegisterOperationAction(PriorityValue, OperationKind.FieldReference);
            start.RegisterSymbolAction(c => AsyncMethod(c, model), SymbolKind.Method);
        });
    }

    // ----- ATL2001 ----------------------------------------------------------------------------

    private static void ExpressionStatement(OperationAnalysisContext c, AtelModel model) {
        if (((IExpressionStatementOperation)c.Operation).Operation is IInvocationOperation i && model.IsTask(i.Type)) {
            c.ReportDiagnostic(Diagnostic.Create(Rules.RequestNotAwaited, i.Syntax.GetLocation(), i.TargetMethod.Name));
        }
    }

    // ----- ATL2002, ATL2004, ATL2006 --------------------------------------------------------------

    private static void Invocation(OperationAnalysisContext c, AtelModel model) {
        IInvocationOperation i = (IInvocationOperation)c.Operation;
        IMethodSymbol target = i.TargetMethod;

        // Calling an entry is requesting it, and only async entries can be requested.
        if (AtelModel.IsSource(target) && model.IsEntry(target) && !target.IsAsync && !model.IsTask(target.ReturnType)
            && !IsBaseCall(i, c.ContainingSymbol)) {
            c.ReportDiagnostic(Diagnostic.Create(Rules.RequestToNonAsync, i.Syntax.GetLocation(), target.Name));
        }

        bool request = model.IsWorkerMember(target, "Request") || model.IsWorkerMember(target, "RequestWaitStart")
            || model.IsWorkerMember(target, "RequestWaitEnd") || model.IsWorkerMember(target, "RequestParty");
        if (!request) return;
        IArgumentOperation? level = i.Arguments.FirstOrDefault(a => a.Parameter?.Name == "level");
        if (level?.Value.ConstantValue is { HasValue: true, Value: int l } && l is 0 or >= 8) {
            c.ReportDiagnostic(Diagnostic.Create(Rules.Priority, level.Syntax.GetLocation(), l, LevelUse(l)));
        }
        if (target.Name is "RequestWaitStart" or "RequestWaitEnd") SelfRequest(c, model, i);
    }

    /// <summary><c>base.Main()</c> from an override runs the base class's code, not a request.</summary>
    private static bool IsBaseCall(IInvocationOperation i, ISymbol caller) => i.Instance is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }
        && i.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } };

    private static string LevelUse(int level) => level switch {
        0 => "Main's level",
        7 => "Init's and the battle system's level",
        8 => "unused by the game",
        _ => "outside the game's levels 0-8",
    };

    /// <summary>
    ///     ATL2004 for the raw forms the decompiler writes: <c>RequestWaitEnd(level, worker, entry)</c>
    ///     with constant operands, to the caller's own worker, from code whose level is known (entry 0,
    ///     <c>Init</c>, level 7; entry 1, <c>Main</c>, level 0).
    /// </summary>
    private static void SelfRequest(OperationAnalysisContext c, AtelModel model, IInvocationOperation i) {
        if (c.ContainingSymbol is not IMethodSymbol caller || caller.ContainingType is not INamedTypeSymbol type) return;
        if (model.IsFunctionWorker(type) || !model.WorkerIndex.TryGetValue(type, out int own)) return;
        if (Constant(i, "level") is not int level || Constant(i, "worker") is not int worker || Constant(i, "entry") is not int entry) return;
        if (worker != own) return;
        foreach ((int callerLevel, string what) in KnownLevels(model, caller, i.Syntax)) {
            if (level <= callerLevel) {
                c.ReportDiagnostic(Diagnostic.Create(Rules.SelfRequestDeadlock, i.Syntax.GetLocation(), entry, level, callerLevel, what));
                return;
            }
        }
    }

    private static int? Constant(IInvocationOperation i, string parameter) =>
        i.Arguments.FirstOrDefault(a => a.Parameter?.Name == parameter)?.Value.ConstantValue is { HasValue: true, Value: int v } ? v : null;

    /// <summary>
    ///     The levels code at <paramref name="at"/> runs at, where known: those of the entries the method
    ///     starts (<c>[Entry(n)]</c>, an <c>Init</c> or <c>Main</c> override) and of the <c>Entry(n);</c>
    ///     marks before it.
    /// </summary>
    private static IEnumerable<(int Level, string What)> KnownLevels(AtelModel model, IMethodSymbol caller, SyntaxNode at) {
        List<int> entries = [.. model.EntryNumbers(caller)];
        if (caller.IsOverride && caller.Name == "Init") yield return (InitLevel, "Init");
        if (caller.IsOverride && caller.Name == "Main") yield return (MainLevel, "Main");
        MethodDeclarationSyntax? method = at.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (method?.Body is not null) {
            foreach (InvocationExpressionSyntax mark in method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>()) {
                if (mark.SpanStart >= at.SpanStart) break;
                if (mark is { Expression: IdentifierNameSyntax { Identifier.ValueText: "Entry" }, ArgumentList.Arguments.Count: 1 }
                    && mark.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax { Token.Value: int n } && mark.Parent is ExpressionStatementSyntax) {
                    entries.Add(n);
                }
            }
        }
        if (entries.Contains(0)) yield return (InitLevel, "entry 0, Init");
        if (entries.Contains(1)) yield return (MainLevel, "entry 1, Main");
    }

    // ----- ATL2003, ATL2005 -------------------------------------------------------------------------

    private static void Await(OperationAnalysisContext c, AtelModel model) {
        IAwaitOperation a = (IAwaitOperation)c.Operation;
        IOperation operand = a.Operation;
        while (operand is IConversionOperation { IsImplicit: true } or IParenthesizedOperation) {
            operand = operand is IConversionOperation conv ? conv.Operand : ((IParenthesizedOperation)operand).Operand;
        }
        // The request: the Task itself, or its .Finished / .Started.
        IOperation request = operand is IPropertyReferenceOperation { Property.Name: "Finished" or "Started" } p && model.IsTask(p.Instance?.Type)
            ? p.Instance!
            : operand;
        if (!model.IsTask(request.Type)) {
            // A .NET awaitable is the base library's (ATL1003); anything else completes at once.
            if (operand.Type is not null && !AtelModel.IsBaseLibrary(operand.Type) && operand.Type.TypeKind != TypeKind.Error) {
                c.ReportDiagnostic(Diagnostic.Create(Rules.AwaitNothing, a.Operation.Syntax.GetLocation(), a.Operation.Syntax.ToString()));
            }
            return;
        }
        if (request is IInvocationOperation i && c.ContainingSymbol is IMethodSymbol caller
            && SymbolEqualityComparer.Default.Equals(i.TargetMethod.ContainingType?.OriginalDefinition, caller.ContainingType?.OriginalDefinition)
            && !model.IsFunctionWorker(caller.ContainingType)) {
            c.ReportDiagnostic(Diagnostic.Create(Rules.SelfRequest, a.Syntax.GetLocation(), i.TargetMethod.Name));
        }
    }

    // ----- ATL2006 for the Priority enum -----------------------------------------------------------

    /// <summary><c>Priority.P0_Main</c>, <c>P7_…</c>, <c>P8_…</c>: an enum named <c>Priority</c> of the reference assembly.</summary>
    private static void PriorityValue(OperationAnalysisContext c) {
        IFieldReferenceOperation f = (IFieldReferenceOperation)c.Operation;
        if (f.Field.ContainingType is not { TypeKind: TypeKind.Enum, Name: "Priority" } e || AtelModel.IsSource(e) || AtelModel.IsBaseLibrary(e)) return;
        if (!f.Field.HasConstantValue || f.Field.ConstantValue is null) return;
        int level = System.Convert.ToInt32(f.Field.ConstantValue, System.Globalization.CultureInfo.InvariantCulture);
        if (level is 0 or >= 7) c.ReportDiagnostic(Diagnostic.Create(Rules.Priority, f.Syntax.GetLocation(), f.Field.Name, LevelUse(level)));
    }

    // ----- ATL2007 ------------------------------------------------------------------------------

    private static void AsyncMethod(SymbolAnalysisContext c, AtelModel model) {
        IMethodSymbol m = (IMethodSymbol)c.Symbol;
        if (!m.IsAsync || model.IsTask(m.ReturnType)) return;
        // An entry the engine runs (not requested by name) may be `async void` so it can await requests:
        // nothing waits on its result.
        if (m.ReturnsVoid && (m.IsOverride || model.EntryNumbers(m).Any())) return;
        // A .NET Task is reported by name (ATL1003).
        if (!m.ReturnsVoid && AtelModel.IsBaseLibrary(m.ReturnType)) return;
        foreach (Location l in m.Locations) c.ReportDiagnostic(Diagnostic.Create(Rules.AsyncReturn, l, m.Name));
    }
}
