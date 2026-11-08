// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Ffx.Atel.Analyzers;

/// <summary>
///     What a mod writes on top of the vanilla source (docs/language.md, "Mods"): a static constructor that does more
///     than set initial values (ATL4001), and an <c>[Inject]</c> whose call is not in the method it names (ATL4002).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModAnalyzer : DiagnosticAnalyzer {
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.InitialValue];

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start => {
            if (AtelModel.Create(start.Compilation) is not AtelModel model) return;
            start.RegisterSyntaxNodeAction(c => StaticConstructor(c, model), SyntaxKind.ConstructorDeclaration);
        });
    }

    // ----- ATL4001: initial values -------------------------------------------------------------

    private static void StaticConstructor(SyntaxNodeAnalysisContext c, AtelModel model) {
        ConstructorDeclarationSyntax ctor = (ConstructorDeclarationSyntax)c.Node;
        if (c.SemanticModel.GetDeclaredSymbol(ctor, c.CancellationToken) is not IMethodSymbol symbol) return;
        INamedTypeSymbol type = symbol.ContainingType;
        bool worker = model.IsWorker(type);
        string? script = ScriptKind(type);
        if (!worker && script is null) return;
        if (!symbol.IsStatic) {
            Report(c, ctor.Identifier.GetLocation(), $"a constructor of {type.Name} is not static; a mod's static constructor sets initial values");
            return;
        }
        string? kind = script ?? ScriptOf(type);
        foreach (StatementSyntax s in ctor.Body?.Statements ?? default) {
            if (s is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax asg } || !asg.IsKind(SyntaxKind.SimpleAssignmentExpression)) {
                Report(c, s.GetLocation(), "a static constructor holds initial values only: `Field = constant;`");
                continue;
            }
            if (c.SemanticModel.GetOperation(asg, c.CancellationToken) is not ISimpleAssignmentOperation op) continue;
            if (!op.Value.ConstantValue.HasValue) {
                Report(c, asg.Right.GetLocation(), "an initial value is a constant");
                continue;
            }
            if (Problem(op.Target, worker, kind) is string problem) Report(c, asg.Left.GetLocation(), problem);
        }
    }

    /// <summary>What is wrong with setting <paramref name="target"/> in this static constructor; null when nothing is.</summary>
    private static string? Problem(IOperation target, bool worker, string? kind) {
        switch (target) {
            case IPropertyReferenceOperation { Property.ContainingType: { Name: "MonsterStats" or "MonsterRewards", ContainingNamespace.Name: "Ffx" } t }:
                if (worker) return $"{t.Name} is set in the script class's static constructor";
                return kind == "Monster" ? null : $"{t.Name} is a monster's data: only a monster script has it";
            case IInvocationOperation { TargetMethod: { Name: "At", ContainingType: { Name: "Data", ContainingNamespace.Name: "Atel" } } } at:
                if (at.Arguments.Any(a => !a.Value.ConstantValue.HasValue)) return "Data.At takes a constant area and offset";
                bool privateArea = at.Arguments.Length > 0 && at.Arguments[0].Value.ConstantValue.Value is 4;    // DataArea.Private
                return PlaceProblem(privateArea ? 3 : 4, worker, kind);
            case IArrayElementReferenceOperation element:
                if (element.Indices.Any(i => !i.ConstantValue.HasValue)) return "an element by constant indices";
                return element.ArrayReference is IFieldReferenceOperation array ? FieldProblem(array.Field, worker, kind) : "an element of a variable of the script";
            case IFieldReferenceOperation f:
                return FieldProblem(f.Field, worker, kind);
            default:
                return "a static constructor sets a variable of the script, an element of one, a field of MonsterStats or MonsterRewards, or Data.At<T>(…)";
        }
    }

    private static string? FieldProblem(IFieldSymbol field, bool worker, string? kind) {
        if (ScriptKind(field.ContainingType) is null) return "a static constructor sets a variable of the script";
        AttributeData? var = field.GetAttributes().FirstOrDefault(a => a.AttributeClass is { Name: "VarAttribute", ContainingNamespace.Name: "Atel" });
        if (var is null) return $"{field.Name} is the mod's own variable: give it its initial value as an initializer";
        return var.ConstructorArguments.Length > 0 && var.ConstructorArguments[0].Value is int storage ? PlaceProblem(storage, worker, kind, field.Name) : null;
    }

    private static string? PlaceProblem(int storage, bool worker, string? kind, string what = "this") => storage switch {
        3 when !worker => $"{what} is private data (storage 3), each worker's own: set it in the worker's static constructor",
        3 when kind != "Event" => "in battle the game clears a worker's private data when it sets the worker up, so an initial value there has no effect",
        3 => null,
        4 or 6 when worker => "a worker's static constructor sets its private data; the script's data is set in the script class's",
        4 or 6 => null,
        _ => $"{what} is not in the script's data (storage {storage}): its value is the save's or the game's",
    };

    /// <summary>"Event", "Monster" or "Battle" for a script class (<c>[Event]</c>, <c>[Monster]</c>, <c>[Battle]</c> on any of its parts); else null.</summary>
    private static string? ScriptKind(INamedTypeSymbol? type) => type?.GetAttributes()
        .Select(a => a.AttributeClass?.Name).FirstOrDefault(n => n is "EventAttribute" or "MonsterAttribute" or "BattleAttribute")?.Replace("Attribute", "");

    /// <summary>The kind of the script a worker belongs to: the script class of its namespace.</summary>
    private static string? ScriptOf(INamedTypeSymbol worker) =>
        worker.ContainingNamespace?.GetTypeMembers().Select(t => ScriptKind(t)).FirstOrDefault(k => k is not null);

    private static void Report(SyntaxNodeAnalysisContext c, Location where, string message) => c.ReportDiagnostic(Diagnostic.Create(Rules.InitialValue, where, message));
}
