// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Ffx.Atel.Analyzers;

/// <summary>
///     A mod's <c>[Saved]</c> fields (docs/language.md, "Saved fields"): one that cannot be saved (ATL4003), and a write to
///     another mod's (ATL4004). The compiler checks the same when it builds the mod; these say it in the IDE.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SavedAnalyzer : DiagnosticAnalyzer {
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.SavedField, Rules.ForeignSavedWrite];

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start => {
            if (AtelModel.Create(start.Compilation) is not AtelModel model) return;
            // Per key, the field declared first (in source order): a later one with its key is the one reported, whatever
            // order the fields are analyzed in.
            System.Lazy<Dictionary<string, IFieldSymbol>> keys = new(() => FirstByKey(start.Compilation));
            start.RegisterSymbolAction(c => Field(c, model, keys.Value), SymbolKind.Field);
            start.RegisterOperationAction(Write, OperationKind.SimpleAssignment, OperationKind.CompoundAssignment, OperationKind.Increment, OperationKind.Decrement);
        });
    }

    /// <summary>The field's <c>[Saved]</c>, or null.</summary>
    private static AttributeData? SavedOf(ISymbol symbol) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass is { Name: "SavedAttribute", ContainingNamespace: { Name: "Atel", ContainingNamespace.Name: "Ffx" } });

    private static string? Named(AttributeData a, string name) =>
        a.NamedArguments.FirstOrDefault(kv => kv.Key == name).Value.Value as string;

    // ----- ATL4003 ------------------------------------------------------------------------------

    /// <summary>The full key of a <c>[Saved]</c> field as written (<c>mod/key</c> for another mod's); null when it names none.</summary>
    private static string? FullKey(IFieldSymbol field, AttributeData saved) {
        string? key = Named(saved, "Key"), mod = Named(saved, "Mod");
        if (mod is not null && key is null) return null;
        string ns = field.ContainingType.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : "";
        key ??= $"{ns}{field.ContainingType.Name}.{field.Name}";
        return mod is null ? key : $"{mod}/{key}";
    }

    private static Dictionary<string, IFieldSymbol> FirstByKey(Compilation compilation) {
        List<(string Key, IFieldSymbol Field, Location At)> all = [];
        Stack<INamespaceOrTypeSymbol> todo = new([compilation.Assembly.GlobalNamespace]);
        while (todo.Count > 0) {
            INamespaceOrTypeSymbol n = todo.Pop();
            foreach (ISymbol m in n.GetMembers()) {
                if (m is INamespaceOrTypeSymbol inner) todo.Push(inner);
                else if (m is IFieldSymbol f && SavedOf(f) is AttributeData a && FullKey(f, a) is string k && f.Locations.FirstOrDefault(l => l.IsInSource) is Location at) all.Add((k, f, at));
            }
        }
        Dictionary<string, IFieldSymbol> first = new(System.StringComparer.Ordinal);
        foreach ((string key, IFieldSymbol field, _) in all.OrderBy(x => x.At.SourceTree?.FilePath, System.StringComparer.Ordinal).ThenBy(x => x.At.SourceSpan.Start)) {
            if (!first.ContainsKey(key)) first[key] = field;
        }
        return first;
    }

    private static void Field(SymbolAnalysisContext c, AtelModel model, Dictionary<string, IFieldSymbol> keys) {
        IFieldSymbol field = (IFieldSymbol)c.Symbol;
        if (SavedOf(field) is not AttributeData saved) return;
        Location where = field.Locations.FirstOrDefault() ?? Location.None;
        void Report(string message) => c.ReportDiagnostic(Diagnostic.Create(Rules.SavedField, where, message));

        if (!field.IsStatic || field.IsConst || field.IsReadOnly) { Report($"{field.Name}: a [Saved] field is static, and neither const nor readonly"); return; }
        if (field.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name is "VarAttribute" or "RegisterAttribute") is AttributeData other) {
            Report($"{field.Name}: a [Saved] field has no [{other.AttributeClass!.Name.Replace("Attribute", "")}]: the loader gives it its place");
            return;
        }
        INamedTypeSymbol type = field.ContainingType;
        if (type.ContainingType is not null) { Report($"{field.Name}: a [Saved] field is not in a nested class"); return; }
        if (model.IsWorker(type)) { Report($"{field.Name}: a [Saved] field is in a mod's static class or in its part of the script class, not in a worker"); return; }
        if (!IsScriptClass(type) && !type.IsStatic) { Report($"{field.Name}: {type.Name} holds [Saved] fields, so it is a static class (or the script class)"); return; }
        if (ElementProblem(field.Type) is string typeProblem) { Report($"{field.Name}: {typeProblem}"); return; }

        string? key = Named(saved, "Key"), mod = Named(saved, "Mod");
        if (mod is not null && key is null) { Report($"{field.Name}: another mod's field is named by its Key: [Saved(Mod = \"{mod}\", Key = \"Namespace.Class.Field\")]"); return; }
        if (key is not null && (key.Length == 0 || key.IndexOf('/') >= 0 || key.Any(char.IsWhiteSpace))) { Report($"{field.Name}: a key is not empty and has no / or space"); return; }
        string ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : "";
        key ??= $"{ns}{type.Name}.{field.Name}";

        VariableDeclaratorSyntax? declarator = field.DeclaringSyntaxReferences.Select(r => r.GetSyntax(c.CancellationToken)).OfType<VariableDeclaratorSyntax>().FirstOrDefault();
        ExpressionSyntax? init = declarator?.Initializer?.Value;
        if (field.Type is IArrayTypeSymbol && !HasSize(init)) { Report($"{field.Name}: a saved array has its size: = new T[n] or an initializer"); return; }
        if (mod is not null && init is not null && !(init is ArrayCreationExpressionSyntax { Initializer: null })) {
            Report($"{field.Name} is {mod}'s field: its initial value is that mod's (an array keeps only its size, = new T[n])");
            return;
        }
        string full = mod is null ? key : $"{mod}/{key}";
        if (keys.TryGetValue(full, out IFieldSymbol? first) && !SymbolEqualityComparer.Default.Equals(first, field)) {
            Report(mod is null ? $"{field.Name}: another [Saved] field has the key {key}; give one its own Key" : $"{field.Name}: {mod}'s saved field {key} is declared twice");
        }
    }

    private static bool IsScriptClass(INamedTypeSymbol type) =>
        type.GetAttributes().Any(a => a.AttributeClass?.Name is "EventAttribute" or "MonsterAttribute" or "BattleAttribute");

    private static string? ElementProblem(ITypeSymbol type) {
        if (type is IArrayTypeSymbol array) {
            if (array.Rank > 2) return "a saved array has 1 or 2 dimensions";
            type = array.ElementType;
        }
        return type.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_UInt16 or SpecialType.System_Int16
            or SpecialType.System_UInt32 or SpecialType.System_Int32 or SpecialType.System_Single
            ? null
            : $"a saved field is byte, sbyte, ushort, short, uint, int or float, or an array of one, not {type.ToDisplayString()}";
    }

    private static bool HasSize(ExpressionSyntax? init) => init switch {
        ArrayCreationExpressionSyntax a => a.Initializer is not null || a.Type.RankSpecifiers.Count > 0 && a.Type.RankSpecifiers[0].Sizes.All(s => s is not OmittedArraySizeExpressionSyntax),
        ImplicitArrayCreationExpressionSyntax or InitializerExpressionSyntax or CollectionExpressionSyntax => true,
        _ => false,
    };

    // ----- ATL4004 ------------------------------------------------------------------------------

    private static void Write(OperationAnalysisContext c) {
        IOperation? target = c.Operation switch {
            IAssignmentOperation a => a.Target,
            IIncrementOrDecrementOperation i => i.Target,
            _ => null,
        };
        if (target is IArrayElementReferenceOperation element) target = element.ArrayReference;
        if (target is not IFieldReferenceOperation { Field: IFieldSymbol field }) return;
        if (SavedOf(field) is not AttributeData saved || Named(saved, "Mod") is not string mod) return;
        c.ReportDiagnostic(Diagnostic.Create(Rules.ForeignSavedWrite, c.Operation.Syntax.GetLocation(), field.Name, mod));
    }
}
