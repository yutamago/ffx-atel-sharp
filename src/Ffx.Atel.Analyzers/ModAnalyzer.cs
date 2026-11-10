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
///     What a mod writes on top of the vanilla source (docs/language.md, "Mods"): a static constructor that does more
///     than set initial values (ATL4001), and an <c>[Inject]</c> whose call is not in the method it names (ATL4002).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModAnalyzer : DiagnosticAnalyzer {
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.InitialValue, Rules.InjectAnchor];

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start => {
            if (AtelModel.Create(start.Compilation) is not AtelModel model) return;
            start.RegisterSyntaxNodeAction(c => StaticConstructor(c, model), SyntaxKind.ConstructorDeclaration);
            NativeNames natives = new(start.Compilation);
            start.RegisterSyntaxNodeAction(c => Inject(c, natives), SyntaxKind.Attribute);
        });
    }

    // ----- ATL4002: [Inject] -------------------------------------------------------------------

    /// <summary>
    ///     An <c>[Inject(nameof(M), Before = …, Occurrence = n)]</c> whose method has fewer than n places of its anchor,
    ///     counted as the compiler counts them (ScriptCompiler.AnchorPlaces): the calls named so as written, the members
    ///     written with that name, and the calls and members that compile to a native the anchor names (<c>nameof(Self.Hp)</c>
    ///     names <c>GetSelfProperty</c> and <c>SetActorProperty</c>, so <c>Btl.SetActorProperty(ActorProperty.Hp, 1)</c> is a
    ///     place of it). Without the compiler's lowering a place is found by its names, so this may count one the compiler
    ///     does not (another property's constant, a variable of the same name): it reports only what is surely missing.
    /// </summary>
    private static void Inject(SyntaxNodeAnalysisContext c, NativeNames natives) {
        AttributeSyntax a = (AttributeSyntax)c.Node;
        if (c.SemanticModel.GetSymbolInfo(a, c.CancellationToken).Symbol is not IMethodSymbol { ContainingType: { Name: "InjectAttribute", ContainingNamespace.Name: "Atel" } }) return;
        if (a.Parent?.Parent is not MethodDeclarationSyntax patch || c.SemanticModel.GetDeclaredSymbol(patch, c.CancellationToken) is not IMethodSymbol self) return;
        SeparatedSyntaxList<AttributeArgumentSyntax> args = a.ArgumentList?.Arguments ?? default;
        string? Constant(ExpressionSyntax e) => c.SemanticModel.GetConstantValue(e, c.CancellationToken) is { HasValue: true, Value: string v } ? v : null;
        string? method = args.FirstOrDefault(x => x.NameEquals is null && x.NameColon is null) is { } m ? Constant(m.Expression) : null;
        AttributeArgumentSyntax? before = args.FirstOrDefault(x => x.NameEquals?.Name.Identifier.ValueText == "Before");
        AttributeArgumentSyntax? after = args.FirstOrDefault(x => x.NameEquals?.Name.Identifier.ValueText == "After");
        AttributeArgumentSyntax? occurrence = args.FirstOrDefault(x => x.NameEquals?.Name.Identifier.ValueText == "Occurrence");
        if ((before is null) == (after is null)) {
            c.ReportDiagnostic(Diagnostic.Create(Rules.InjectAnchor, a.GetLocation(), "[Inject] runs Before = … or After = … a call or member, one of them"));
            return;
        }
        int n = occurrence is null ? 1 : c.SemanticModel.GetConstantValue(occurrence.Expression, c.CancellationToken) is { HasValue: true, Value: int k } ? k : 0;
        if (n < 1) {
            c.ReportDiagnostic(Diagnostic.Create(Rules.InjectAnchor, occurrence!.GetLocation(), "Occurrence counts from 1"));
            return;
        }
        ExpressionSyntax callArg = (before ?? after)!.Expression;
        ImmutableArray<ISymbol> anchors;
        string? call;
        if (callArg is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" }, ArgumentList.Arguments.Count: 1 } nameOf) {
            ExpressionSyntax named = nameOf.ArgumentList.Arguments[0].Expression;
            call = Dotted(named);
            SymbolInfo info = c.SemanticModel.GetSymbolInfo(named, c.CancellationToken);
            anchors = info.Symbol is ISymbol one ? [one] : info.CandidateSymbols;
        }
        else {
            call = Constant(callArg);
            SymbolInfo info = call is null ? default : c.SemanticModel.GetSpeculativeSymbolInfo(a.SpanStart, SyntaxFactory.ParseExpression(call), SpeculativeBindingOption.BindAsExpression);
            anchors = info.Symbol is ISymbol one ? [one] : info.CandidateSymbols.IsDefault ? [] : info.CandidateSymbols;
        }
        if (method is null || call is null) return;
        IMethodSymbol? target = self.ContainingType.GetMembers(method).OfType<IMethodSymbol>().FirstOrDefault(x => x.MethodKind == MethodKind.Ordinary);
        if (target is null) return;    // C# or the compiler says so
        // The names the anchor's natives are written as: the natives' own (Btl.SetActorProperty) and their members' (Hp).
        // A member anchor is its own name; a native's call is a place of a member picked by a constant (Self.Hp) when an
        // argument names it (ActorProperty.Hp).
        List<ISymbol> members = [.. anchors.Where(x => NativeAttributes(x).Any(IsMemberAttribute))];
        HashSet<string> nativeNames = [.. anchors.SelectMany(x => NativeAttributes(x).Select(Id)).SelectMany(natives.NativesOf)];
        List<(string Name, bool Alone)> memberList = [
            .. members.Select(x => (x.Name, IsWorkerMember(x))),
            .. anchors.Except(members, SymbolEqualityComparer.Default).SelectMany(x => NativeAttributes(x).Select(Id)).SelectMany(natives.MembersOf),
        ];
        HashSet<string> memberNames = [.. memberList.Select(x => x.Name)], aloneNames = [.. memberList.Where(x => x.Alone).Select(x => x.Name)];
        string? picked = members.FirstOrDefault(x => NativeAttributes(x).Any(a => a.NamedArguments.Any(na => na.Key == "Constant" && na.Value.Value is int k && k >= 0)))?.Name;
        bool property = anchors.Length > 0 && anchors.All(x => x is IPropertySymbol);
        int count = target.DeclaringSyntaxReferences.Select(r => r.GetSyntax(c.CancellationToken)).OfType<MethodDeclarationSyntax>()
            .SelectMany(d => d.DescendantNodes()).Count(x => IsPlace(x, call, nativeNames, memberNames, aloneNames, picked) && !IsEnumMember(x, c.Compilation));
        if (count < n) {
            string what = property ? "use(s) of" : "call(s) to";
            c.ReportDiagnostic(Diagnostic.Create(Rules.InjectAnchor, callArg.GetLocation(),
                count == 0 ? $"{method} has no {(property ? "use of" : "call to")} {call}" : $"{method} has {count} {what} {call}, not {n}"));
        }
    }

    /// <summary>A symbol's <c>[Native]</c> and <c>[NativeMember]</c> attributes (a property's on its accessors too).</summary>
    private static IEnumerable<AttributeData> NativeAttributes(ISymbol symbol) {
        IEnumerable<ISymbol> parts = symbol is IPropertySymbol p ? new ISymbol?[] { p, p.GetMethod, p.SetMethod }.OfType<ISymbol>() : [symbol];
        return parts.SelectMany(x => x.GetAttributes()).Where(IsNativeAttribute);
    }

    /// <summary>A member of the worker itself (<c>Ffx.Atel.Worker</c>'s <c>Yaw</c>), written alone; not a component's or a static group's.</summary>
    private static bool IsWorkerMember(ISymbol member) => member.ContainingType is { ContainingNamespace: { Name: "Atel", ContainingNamespace.Name: "Ffx" } };

    private static int Id(AttributeData a) => a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is int id ? id : -1;

    private static bool IsMemberAttribute(AttributeData a) => a.AttributeClass?.Name == "NativeMemberAttribute";

    private static bool IsNativeAttribute(AttributeData a) =>
        a.AttributeClass is { Name: "NativeAttribute" or "NativeMemberAttribute", ContainingNamespace: { Name: "Atel", ContainingNamespace.Name: "Ffx" } };

    /// <summary>
    ///     Whether a node of the vanilla method is a place of the anchor: a call named so, or one to a native the anchor
    ///     names; a member written with the anchor's name, or with a member name of one of its natives.
    /// </summary>
    /// <param name="picked">For a member picked by a constant, its name: a native's call is its place when an argument ends with it.</param>
    /// <param name="aloneNames">The member names that are the worker's own, written alone (<c>Yaw</c>, <c>Motion</c>); a component's (<c>Hp</c> of <c>Self.Hp</c>) is not: alone it is a variable.</param>
    private static bool IsPlace(SyntaxNode node, string name, HashSet<string> nativeNames, HashSet<string> memberNames, HashSet<string> aloneNames, string? picked) {
        switch (node) {
            case InvocationExpressionSyntax call:
                if (IsCallTo(call, name)) return true;
                if (nativeNames.Any(x => IsCallTo(call, x))
                    && (picked is null || call.ArgumentList.Arguments.Any(x => Dotted(x.Expression) is string t && (t == picked || t.EndsWith("." + picked, System.StringComparison.Ordinal))))) {
                    return true;
                }
                return memberNames.Count > 0 && call.Expression switch {
                    MemberAccessExpressionSyntax m => memberNames.Contains(m.Name.Identifier.ValueText),
                    IdentifierNameSyntax id => aloneNames.Contains(id.Identifier.ValueText),
                    _ => false,
                };
            case MemberAccessExpressionSyntax m when m.Parent is not InvocationExpressionSyntax inv || inv.Expression != m:
                return memberNames.Contains(m.Name.Identifier.ValueText) || IsNamed(Dotted(m), name);
            case IdentifierNameSyntax id when id.Parent switch {
                MemberAccessExpressionSyntax m => m.Expression == id && m.Parent is not InvocationExpressionSyntax,    // Pos in Pos.X
                InvocationExpressionSyntax inv => inv.Expression != id,
                _ => true,
            }:
                return aloneNames.Contains(id.Identifier.ValueText) || IsNamed(id.Identifier.ValueText, name);
            default:
                return false;
        }
    }

    /// <summary>A constant of the reference assembly's enums (<c>ActorProperty.Hp</c>, a native's argument), which no member is.</summary>
    private static bool IsEnumMember(SyntaxNode node, Compilation compilation) =>
        node is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax type } && compilation.GetTypeByMetadataName("Ffx." + type.Identifier.ValueText) is { TypeKind: TypeKind.Enum };

    /// <summary>The call's name as written (ScriptCompiler.Dotted).</summary>
    private static string? Dotted(ExpressionSyntax e) => e switch {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        MemberAccessExpressionSyntax m => Dotted(m.Expression) is string left ? $"{left}.{m.Name.Identifier.ValueText}" : m.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText,
        ThisExpressionSyntax => "this",
        _ => null,
    };

    /// <summary>Whether a name as written (<c>Ffx.Btl.UseCommand</c>, <c>Self.Hp</c>) is the anchor's (<c>Btl.UseCommand</c>, <c>UseCommand</c>, <c>Hp</c>).</summary>
    private static bool IsNamed(string? text, string name) =>
        text is not null && (text == name || text.EndsWith("." + name, System.StringComparison.Ordinal) || name.EndsWith("." + text, System.StringComparison.Ordinal) && !text.Contains('.'));

    private static bool IsCallTo(InvocationExpressionSyntax call, string name) => IsNamed(Dotted(call.Expression), name);

    /// <summary>
    ///     The reference assembly's natives by ID, with the names they are written as: the native's
    ///     (<c>Btl.SetActorProperty</c>) and its members' (<c>Hp</c>, <c>Yaw</c>, <c>Play</c>). Read once per compilation, when
    ///     an <c>[Inject]</c> needs it.
    /// </summary>
    private sealed class NativeNames(Compilation compilation) {
        private readonly System.Lazy<(Dictionary<int, HashSet<string>> Natives, Dictionary<int, HashSet<(string, bool)>> Members)> _names = new(() => Read(compilation));

        public IEnumerable<string> NativesOf(int id) => _names.Value.Natives.TryGetValue(id, out HashSet<string>? s) ? s : [];

        public IEnumerable<(string Name, bool Alone)> MembersOf(int id) => _names.Value.Members.TryGetValue(id, out HashSet<(string, bool)>? s) ? s : [];

        private static (Dictionary<int, HashSet<string>>, Dictionary<int, HashSet<(string, bool)>>) Read(Compilation compilation) {
            Dictionary<int, HashSet<string>> natives = [];
            Dictionary<int, HashSet<(string, bool)>> members = [];
            void Add<T>(Dictionary<int, HashSet<T>> to, int id, T name) {
                if (!to.TryGetValue(id, out HashSet<T>? set)) to[id] = set = [];
                set.Add(name);
            }
            void Type(INamedTypeSymbol t) {
                foreach (ISymbol member in t.GetMembers()) {
                    if (member is INamedTypeSymbol nested) {
                        Type(nested);
                        continue;
                    }
                    if (member is not IMethodSymbol and not IPropertySymbol) continue;
                    foreach (AttributeData a in member.GetAttributes().Where(IsNativeAttribute)) {
                        int id = Id(a);
                        if (id < 0) continue;
                        string name = member is IMethodSymbol { AssociatedSymbol: IPropertySymbol p } ? p.Name : member.Name;
                        if (a.AttributeClass!.Name == "NativeAttribute") Add(natives, id, $"{t.Name}.{name}");
                        else Add(members, id, (name, IsWorkerMember(member)));
                    }
                }
            }
            INamespaceSymbol? ffx = compilation.GlobalNamespace.GetNamespaceMembers().FirstOrDefault(x => x.Name == "Ffx");
            if (ffx is null) return (natives, members);
            foreach (INamespaceSymbol ns in new[] { ffx }.Concat(ffx.GetNamespaceMembers().Where(x => x.Name == "Atel"))) {
                foreach (INamedTypeSymbol t in ns.GetTypeMembers()) {
                    if (t.Locations.All(l => l.IsInMetadata)) Type(t);
                }
            }
            return (natives, members);
        }
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
