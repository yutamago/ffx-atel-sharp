// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Ffx.Atel.Analyzers;

/// <summary>
///     The object-oriented surface (docs/language.md, "Members"): a member is its native's call, read or write, so a
///     property takes a plain assignment (ATL1012: <c>Actors[Target.Own].Hp += 10</c> would be two natives), and what
///     holds members (<c>Actors[Target.AllPly]</c>, <c>Self</c>, <c>Turn</c>, <c>Workers[n]</c>) is only ever written
///     before one of them (ATL1013: no object exists to keep in a local or pass on).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MemberAnalyzer : DiagnosticAnalyzer {
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.MemberCompound, Rules.MemberHolder];

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start => {
            if (AtelModel.Create(start.Compilation) is null) return;
            start.RegisterOperationAction(Compound, OperationKind.CompoundAssignment, OperationKind.Increment, OperationKind.Decrement, OperationKind.CoalesceAssignment);
            start.RegisterOperationAction(Holder, OperationKind.PropertyReference);
        });
    }

    /// <summary>A setter that is a native (<c>[NativeMember]</c> on its accessor).</summary>
    private static bool IsNativeSetter(IPropertySymbol p) =>
        p.SetMethod is IMethodSymbol set && set.GetAttributes().Any(a => AtelModel.IsNativeMember(a.AttributeClass));

    private static void Compound(OperationAnalysisContext c) {
        IOperation? target = c.Operation switch {
            ICompoundAssignmentOperation x => x.Target,
            IIncrementOrDecrementOperation x => x.Target,
            ICoalesceAssignmentOperation x => x.Target,
            _ => null,
        };
        if (target is IPropertyReferenceOperation { Property: IPropertySymbol p } && IsNativeSetter(p)) {
            c.ReportDiagnostic(Diagnostic.Create(Rules.MemberCompound, c.Operation.Syntax.GetLocation(), p.Name));
        }
    }

    /// <summary>
    ///     A class of the surface that holds members: one of the reference assembly's whose members are natives
    ///     (<c>PickedActors</c>, <c>OwnActor</c>, <c>TurnControl</c>), or a list of them (<c>ActorList</c>, <c>WorkerList</c>).
    /// </summary>
    private static bool IsHolder(ITypeSymbol? t) {
        if (t is not INamedTypeSymbol { TypeKind: TypeKind.Class } type || type.Locations.Any(l => l.IsInSource)) return false;
        if (!AtelModel.InNamespace(type, "Ffx")) return false;
        if (type.Name is "WorkerList" or "WorkerHandle") return true;
        foreach (ISymbol m in type.GetMembers()) {
            switch (m) {
                case IMethodSymbol method when method.GetAttributes().Any(a => AtelModel.IsNativeMember(a.AttributeClass)):
                    return true;
                case IPropertySymbol { IsIndexer: true } indexer when IsHolder(indexer.Type):
                    return true;
            }
        }
        return false;
    }

    /// <summary><c>var a = Actors[Target.AllPly];</c>, <c>F(Self)</c>: a holder anywhere but before one of its members.</summary>
    private static void Holder(OperationAnalysisContext c) {
        IPropertyReferenceOperation r = (IPropertyReferenceOperation)c.Operation;
        if (!IsHolder(r.Type)) return;
        IOperation? parent = r.Parent;
        while (parent is IParenthesizedOperation) parent = parent.Parent;
        bool used = parent switch {
            // Actors[…].Hp, Self.Hp, Turn.Start(…), Actors[…] (the indexer on the list).
            IPropertyReferenceOperation p => p.Instance is not null && Unwrap(p.Instance) == r,
            IInvocationOperation i => i.Instance is not null && Unwrap(i.Instance) == r,
            // `Motion = 0x71050`: a component that takes a value.
            ISimpleAssignmentOperation a => a.Target == r,
            _ => false,
        };
        if (!used) c.ReportDiagnostic(Diagnostic.Create(Rules.MemberHolder, r.Syntax.GetLocation(), r.Syntax.ToString()));
    }

    private static IOperation Unwrap(IOperation o) {
        while (o is IParenthesizedOperation p) o = p.Operand;
        return o;
    }
}
