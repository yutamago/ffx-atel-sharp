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
///     The C# that ATEL cannot run (docs/plan.md, "Control flow and semantics": what is not
///     available), ATL1001-ATL1010 (ATL1011, `&&` and `||`, is retired: they compile to branches).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SubsetAnalyzer : DiagnosticAnalyzer {
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [
        Rules.ObjectCreation, Rules.String, Rules.BaseLibrary, Rules.Collection, Rules.Linq, Rules.Exceptions,
        Rules.Delegates, Rules.Generics, Rules.Type, Rules.Checked,
    ];

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start => {
            if (AtelModel.Create(start.Compilation) is null) return;

            start.RegisterOperationAction(ObjectCreation, OperationKind.ObjectCreation, OperationKind.AnonymousObjectCreation, OperationKind.TypeParameterObjectCreation);
            start.RegisterOperationAction(ArrayCreation, OperationKind.ArrayCreation);
            start.RegisterOperationAction(CollectionExpression, OperationKind.CollectionExpression);
            start.RegisterOperationAction(StringValue, OperationKind.Literal, OperationKind.InterpolatedString, OperationKind.NameOf, OperationKind.Binary);
            start.RegisterOperationAction(MethodGroup, OperationKind.DelegateCreation);

            // Names are judged by what they bind to, from the bound operations and the declared symbols
            // (binding every identifier again through the semantic model costs as much as compiling).
            start.RegisterOperationAction(MemberReference, OperationKind.Invocation, OperationKind.MethodReference, OperationKind.PropertyReference,
                OperationKind.FieldReference, OperationKind.EventReference);
            start.RegisterOperationAction(TypeInCode, OperationKind.VariableDeclaration, OperationKind.Conversion, OperationKind.TypeOf,
                OperationKind.SizeOf, OperationKind.IsType, OperationKind.DeclarationPattern, OperationKind.TypePattern, OperationKind.DefaultValue);
            start.RegisterSymbolAction(DeclaredTypes, SymbolKind.Field, SymbolKind.Property, SymbolKind.Method, SymbolKind.NamedType, SymbolKind.Event);
            start.RegisterSyntaxNodeAction(c => Report(c, Rules.Linq, c.Node.GetFirstToken(), "query expression"), SyntaxKind.QueryExpression);
            start.RegisterSyntaxNodeAction(c => Report(c, Rules.Exceptions, ((TryStatementSyntax)c.Node).TryKeyword, "try"), SyntaxKind.TryStatement);
            start.RegisterSyntaxNodeAction(c => Report(c, Rules.Exceptions, c.Node.GetFirstToken(), "throw"), SyntaxKind.ThrowStatement, SyntaxKind.ThrowExpression);
            start.RegisterSyntaxNodeAction(Function, SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression,
                SyntaxKind.AnonymousMethodExpression, SyntaxKind.LocalFunctionStatement, SyntaxKind.DelegateDeclaration, SyntaxKind.FunctionPointerType);
            start.RegisterSyntaxNodeAction(TypeParameters, SyntaxKind.TypeParameterList);
            start.RegisterSyntaxNodeAction(c => Report(c, Rules.Checked, c.Node.GetFirstToken()), SyntaxKind.CheckedExpression, SyntaxKind.CheckedStatement);
            start.RegisterSyntaxNodeAction(c => Report(c, Rules.ObjectCreation, c.Node.GetFirstToken(), "'stackalloc'"), SyntaxKind.StackAllocArrayCreationExpression);
        });
    }

    private static void Report(SyntaxNodeAnalysisContext c, DiagnosticDescriptor rule, SyntaxToken at, params object[] args) =>
        c.ReportDiagnostic(Diagnostic.Create(rule, at.GetLocation(), args));

    private static void Report(OperationAnalysisContext c, DiagnosticDescriptor rule, SyntaxNode at, params object[] args) =>
        c.ReportDiagnostic(Diagnostic.Create(rule, at.GetLocation(), args));

    /// <summary>Inside an attribute, a <c>using</c> directive or <c>nameof</c>: names there are compile-time only.</summary>
    private static bool CompileTimeOnly(SyntaxNode node) {
        foreach (SyntaxNode n in node.AncestorsAndSelf()) {
            switch (n) {
                case AttributeListSyntax or UsingDirectiveSyntax or ExternAliasDirectiveSyntax:
                    return true;
                case InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } when n != node:
                    return true;
                case MemberDeclarationSyntax or StatementSyntax:
                    return false;
            }
        }
        return false;
    }

    // ----- ATL1001: new -------------------------------------------------------------------------

    private static void ObjectCreation(OperationAnalysisContext c) {
        ITypeSymbol? type = c.Operation.Type;
        if (CompileTimeOnly(c.Operation.Syntax)) return;
        // `Pos = new Vector3(x, y, z)`: no object, the property's native takes the three values (docs/language.md, "Members").
        if (AtelModel.IsVector3(type)) {
            IOperation value = c.Operation;
            while (value.Parent is IConversionOperation { IsImplicit: true } conversion) value = conversion;
            if (value.Parent is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation } assignment && assignment.Value == value) return;
        }
        // A .NET type is reported as what it is (ATL1003, ATL1004), a delegate as ATL1007; at the
        // type where it is written (`new()` takes the declared type, reported there).
        if (type is not null && (AtelModel.IsBaseLibrary(type) || type.TypeKind == TypeKind.Delegate)) {
            if (c.Operation.Syntax is ObjectCreationExpressionSyntax explicitNew) CheckType(c.ReportDiagnostic, type, explicitNew.Type);
            return;
        }
        string what = c.Operation switch {
            IAnonymousObjectCreationOperation => "An anonymous object",
            _ => $"Creating '{type?.ToDisplayString() ?? "an object"}'",
        };
        Report(c, Rules.ObjectCreation, c.Operation.Syntax, what);
    }

    /// <summary>Arrays are fields: an array created anywhere but a field's initializer has no storage.</summary>
    private static void ArrayCreation(OperationAnalysisContext c) {
        if (IsFieldInitializer(c.Operation) || CompileTimeOnly(c.Operation.Syntax)) return;
        Report(c, Rules.ObjectCreation, c.Operation.Syntax, "An array created at run time");
    }

    private static void CollectionExpression(OperationAnalysisContext c) {
        if (CompileTimeOnly(c.Operation.Syntax)) return;
        if (c.Operation.Type is IArrayTypeSymbol) {
            if (!IsFieldInitializer(c.Operation)) Report(c, Rules.ObjectCreation, c.Operation.Syntax, "An array created at run time");
        }
        else if (c.Operation.Type is not null) {
            Report(c, Rules.Collection, c.Operation.Syntax, c.Operation.Type.ToDisplayString());
        }
    }

    private static bool IsFieldInitializer(IOperation operation) {
        for (IOperation? o = operation.Parent; o is not null; o = o.Parent) {
            switch (o) {
                case IConversionOperation or IArrayInitializerOperation or IArrayCreationOperation:
                    continue;
                case IFieldInitializerOperation or IPropertyInitializerOperation:
                    return true;
                default:
                    return false;
            }
        }
        return false;
    }

    // ----- ATL1002: string ----------------------------------------------------------------------

    private static void StringValue(OperationAnalysisContext c) {
        IOperation o = c.Operation;
        if (o is IBinaryOperation && o.Type?.SpecialType != SpecialType.System_String) return;
        if (o.Type?.SpecialType != SpecialType.System_String) {
            if (o is ILiteralOperation && CompileTimeOnly(o.Syntax)) return;
            if (o is ILiteralOperation { Type.SpecialType: SpecialType.System_Double or SpecialType.System_Decimal or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Char } lit) {
                Report(c, Rules.Type, lit.Syntax, lit.Type!.ToDisplayString());
            }
            return;
        }
        if (o.ConstantValue is { HasValue: true, Value: null }) return; // null
        if (CompileTimeOnly(o.Syntax)) return;
        // Nested parts of a string (an interpolation's text, the operands of a concatenation) are
        // judged once, at the outermost string.
        if (o.Parent is IInterpolatedStringContentOperation or IInterpolationOperation or IInterpolatedStringOperation) return;
        if (o.Parent is IBinaryOperation { Type.SpecialType: SpecialType.System_String }) return;
        IOperation top = o;
        while (top.Parent is IBinaryOperation { Type.SpecialType: SpecialType.System_String } or IParenthesizedOperation
               or IConversionOperation { Type.SpecialType: SpecialType.System_String }) {
            top = top.Parent;
        }
        // A string passed to a reference-assembly member that takes one: Debug.*, Asm, Text.New.
        if (top.Parent is IArgumentOperation { Parameter: { Type.SpecialType: SpecialType.System_String } p }
            && !AtelModel.IsSource(p) && !AtelModel.IsBaseLibrary(p.ContainingSymbol)) {
            return;
        }
        Report(c, Rules.String, o.Syntax);
    }

    // ----- ATL1003-1005, ATL1009: the base library and types ----------------------------------

    /// <summary>A member of a .NET type: <c>Math.Abs(x)</c>, <c>V.Length</c>, <c>V.Select(…)</c>.</summary>
    private static void MemberReference(OperationAnalysisContext c) {
        IOperation o = c.Operation;
        if (o.IsImplicit) return;
        ISymbol? member = o switch {
            IInvocationOperation i => i.TargetMethod.ReducedFrom ?? i.TargetMethod,
            IMethodReferenceOperation m => m.Method.ReducedFrom ?? m.Method,
            IPropertyReferenceOperation p => p.Property,
            IFieldReferenceOperation { Field.IsConst: false } f => f.Field, // int.MaxValue is a constant
            IEventReferenceOperation e => e.Event,
            _ => null,
        };
        if (member?.ContainingType is not INamedTypeSymbol type || !AtelModel.IsBaseLibrary(type)) return;
        SyntaxNode at = NameOf(o.Syntax);
        if (CompileTimeOnly(at)) return;
        c.ReportDiagnostic(Diagnostic.Create(Classify(type, out string? what), at.GetLocation(), what ?? $"{type.Name}.{member.Name}"));
    }

    /// <summary>The name in <c>a.b</c>, <c>a.b(…)</c>, <c>b(…)</c>.</summary>
    private static SyntaxNode NameOf(SyntaxNode syntax) => syntax switch {
        InvocationExpressionSyntax inv => NameOf(inv.Expression),
        MemberAccessExpressionSyntax ma => ma.Name,
        MemberBindingExpressionSyntax mb => mb.Name,
        _ => syntax,
    };

    /// <summary>A type written in code: a local's declared type, a cast, <c>typeof</c>, <c>is T</c>, <c>default(T)</c>.</summary>
    private static void TypeInCode(OperationAnalysisContext c) {
        IOperation o = c.Operation;
        if (CompileTimeOnly(o.Syntax)) return;
        switch (o) {
            case IVariableDeclarationOperation d when d.Syntax is VariableDeclarationSyntax { Type.IsVar: false } v && d.Declarators.Length > 0:
                CheckType(c.ReportDiagnostic, d.Declarators[0].Symbol.Type, v.Type);
                break;
            case IConversionOperation { IsImplicit: false, Syntax: CastExpressionSyntax cast } conv:
                CheckType(c.ReportDiagnostic, conv.Type, cast.Type);
                break;
            case ITypeOfOperation t when t.Syntax is TypeOfExpressionSyntax ts:
                CheckType(c.ReportDiagnostic, t.TypeOperand, ts.Type);
                break;
            case ISizeOfOperation t when t.Syntax is SizeOfExpressionSyntax ss:
                CheckType(c.ReportDiagnostic, t.TypeOperand, ss.Type);
                break;
            case IIsTypeOperation t when t.Syntax is BinaryExpressionSyntax { Right: TypeSyntax ts }:
                CheckType(c.ReportDiagnostic, t.TypeOperand, ts);
                break;
            case IDeclarationPatternOperation { MatchedType: ITypeSymbol m, Syntax: DeclarationPatternSyntax dp }:
                CheckType(c.ReportDiagnostic, m, dp.Type);
                break;
            case ITypePatternOperation { MatchedType: ITypeSymbol m, Syntax: TypePatternSyntax tp }:
                CheckType(c.ReportDiagnostic, m, tp.Type);
                break;
            case IDefaultValueOperation { Type: ITypeSymbol t, Syntax: DefaultExpressionSyntax de }:
                CheckType(c.ReportDiagnostic, t, de.Type);
                break;
        }
    }

    /// <summary>The types of declarations: fields, properties, events, parameters, return types, base types.</summary>
    private static void DeclaredTypes(SymbolAnalysisContext c) {
        foreach (SyntaxReference r in c.Symbol.DeclaringSyntaxReferences) {
            SyntaxNode node = r.GetSyntax(c.CancellationToken);
            switch (c.Symbol, node) {
                case (IFieldSymbol f, VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax d }) when d.Variables[0] == node:
                    CheckType(c.ReportDiagnostic, f.Type, d.Type);
                    break;
                case (IEventSymbol e, VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax d }) when d.Variables[0] == node:
                    CheckType(c.ReportDiagnostic, e.Type, d.Type);
                    break;
                case (IPropertySymbol p, BasePropertyDeclarationSyntax pd):
                    CheckType(c.ReportDiagnostic, p.Type, pd.Type);
                    foreach ((IParameterSymbol ps, ParameterSyntax syntax) in Parameters(p.Parameters, (pd as IndexerDeclarationSyntax)?.ParameterList)) {
                        CheckType(c.ReportDiagnostic, ps.Type, syntax.Type);
                    }
                    break;
                case (IEventSymbol e, EventDeclarationSyntax ed):
                    CheckType(c.ReportDiagnostic, e.Type, ed.Type);
                    break;
                case (IMethodSymbol m, BaseMethodDeclarationSyntax md):
                    if (md is MethodDeclarationSyntax { ReturnType: TypeSyntax rt }) CheckType(c.ReportDiagnostic, m.ReturnType, rt);
                    if (md is ConversionOperatorDeclarationSyntax co) CheckType(c.ReportDiagnostic, m.ReturnType, co.Type);
                    if (md is OperatorDeclarationSyntax op) CheckType(c.ReportDiagnostic, m.ReturnType, op.ReturnType);
                    foreach ((IParameterSymbol ps, ParameterSyntax syntax) in Parameters(m.Parameters, md.ParameterList)) {
                        CheckType(c.ReportDiagnostic, ps.Type, syntax.Type);
                    }
                    break;
                case (INamedTypeSymbol t, BaseTypeDeclarationSyntax td) when td.BaseList is not null:
                    foreach (INamedTypeSymbol bt in t.Interfaces.Prepend(t.BaseType!).Where(b => b is not null && b.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum))) {
                        // The base list entry that names it (by its rightmost identifier).
                        BaseTypeSyntax? b = td.BaseList.Types.FirstOrDefault(x => Rightmost(x.Type) == bt.Name);
                        if (b is not null) CheckType(c.ReportDiagnostic, bt, b.Type);
                    }
                    break;
            }
        }
    }

    private static string Rightmost(TypeSyntax t) => t switch {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        SimpleNameSyntax n => n.Identifier.ValueText,
        _ => "",
    };

    private static IEnumerable<(IParameterSymbol, ParameterSyntax)> Parameters(ImmutableArray<IParameterSymbol> symbols, BaseParameterListSyntax? list) {
        if (list is null) yield break;
        for (int k = 0; k < symbols.Length && k < list.Parameters.Count; k++) {
            if (list.Parameters[k].Type is not null) yield return (symbols[k], list.Parameters[k]);
        }
    }

    /// <summary>Reports a type that is not Atel, once, at <paramref name="at"/>; looks into arrays, nullables and type arguments.</summary>
    private static void CheckType(System.Action<Diagnostic> report, ITypeSymbol? type, TypeSyntax? at) {
        if (type is null || at is null || type.TypeKind == TypeKind.Error) return;
        if (at is NullableTypeSyntax nt && type is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }) at = nt.ElementType;
        switch (type) {
            case IArrayTypeSymbol a:
                CheckType(report, a.ElementType, (at as ArrayTypeSyntax)?.ElementType ?? at);
                return;
            case IPointerTypeSymbol or IFunctionPointerTypeSymbol:
                report(Diagnostic.Create(Rules.Type, at.GetLocation(), type.ToDisplayString()));
                return;
            case { TypeKind: TypeKind.Dynamic }:
                report(Diagnostic.Create(Rules.Type, at.GetLocation(), "dynamic"));
                return;
        }
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n) {
            CheckType(report, n.TypeArguments[0], (at as NullableTypeSyntax)?.ElementType ?? at);
            return;
        }
        if (AtelModel.IsBaseLibrary(type)) {
            switch (type.SpecialType) {
                case SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16
                    or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single
                    or SpecialType.System_Void:
                    return;
                case SpecialType.System_String:
                    report(Diagnostic.Create(Rules.String, at.GetLocation()));
                    return;
                case SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double or SpecialType.System_Decimal
                    or SpecialType.System_Char or SpecialType.System_Object or SpecialType.System_IntPtr or SpecialType.System_UIntPtr:
                    report(Diagnostic.Create(Rules.Type, at.GetLocation(), type.ToDisplayString()));
                    return;
            }
            DiagnosticDescriptor rule = Classify(type, out string? what);
            report(Diagnostic.Create(rule, at.GetLocation(), what ?? type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
            return;
        }
        if (type is INamedTypeSymbol { IsGenericType: true } g) {
            SeparatedSyntaxList<TypeSyntax>? args = (at as GenericNameSyntax ?? (at as QualifiedNameSyntax)?.Right as GenericNameSyntax)?.TypeArgumentList.Arguments;
            for (int k = 0; k < g.TypeArguments.Length; k++) {
                CheckType(report, g.TypeArguments[k], args is { } list && k < list.Count ? list[k] : at);
            }
        }
    }

    /// <summary>Which rule a .NET type falls under; <paramref name="what"/> a description where the rule wants one.</summary>
    private static DiagnosticDescriptor Classify(ITypeSymbol type, out string? what) {
        what = null;
        if (AtelModel.InNamespace(type, "System.Linq")) return Rules.Linq;
        if (AtelModel.InNamespace(type, "System.Collections") || IsCollection(type)) return Rules.Collection;
        if (type.TypeKind == TypeKind.Delegate || type.SpecialType is SpecialType.System_Delegate or SpecialType.System_MulticastDelegate) {
            what = $"Delegate type '{type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}'";
            return Rules.Delegates;
        }
        if (IsException(type)) return Rules.Exceptions;
        return Rules.BaseLibrary;
    }

    private static bool IsCollection(ITypeSymbol? type) =>
        type is not null && type.SpecialType is not (SpecialType.System_Array or SpecialType.System_String)
        && type.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable);

    private static bool IsException(ITypeSymbol? type) {
        for (ITypeSymbol? t = type; t is not null; t = t.BaseType) {
            if (t.Name == "Exception" && t.ContainingNamespace?.ToDisplayString() == "System") return true;
        }
        return false;
    }

    // ----- ATL1007, ATL1008 -------------------------------------------------------------------------

    private static void Function(SyntaxNodeAnalysisContext c) {
        (string what, SyntaxToken at) = c.Node switch {
            LambdaExpressionSyntax l => ("A lambda", l.ArrowToken),
            AnonymousMethodExpressionSyntax a => ("An anonymous method", a.DelegateKeyword),
            LocalFunctionStatementSyntax f => ($"Local function '{f.Identifier.ValueText}'", f.Identifier),
            DelegateDeclarationSyntax d => ($"Delegate type '{d.Identifier.ValueText}'", d.Identifier),
            _ => ("A function pointer", c.Node.GetFirstToken()),
        };
        Report(c, Rules.Delegates, at, what);
    }

    /// <summary>A method group used as a value (<c>Action a = Foo;</c>).</summary>
    private static void MethodGroup(OperationAnalysisContext c) {
        IDelegateCreationOperation d = (IDelegateCreationOperation)c.Operation;
        if (d.Target is IMethodReferenceOperation && !d.Syntax.Ancestors().Any(a => a is AttributeListSyntax)) {
            Report(c, Rules.Delegates, d.Syntax, $"Method group '{((IMethodReferenceOperation)d.Target).Method.Name}' as a value");
        }
    }

    private static void TypeParameters(SyntaxNodeAnalysisContext c) {
        (string kind, SyntaxToken id) = c.Node.Parent switch {
            MethodDeclarationSyntax m => ("method", m.Identifier),
            TypeDeclarationSyntax t => ("type", t.Identifier),
            DelegateDeclarationSyntax => (null!, default),
            LocalFunctionStatementSyntax => (null!, default),
            _ => ("declaration", c.Node.GetFirstToken()),
        };
        if (kind is null) return; // reported as ATL1007
        Report(c, Rules.Generics, id, kind, id.ValueText);
    }
}
