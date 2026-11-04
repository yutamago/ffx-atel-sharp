// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Ffx.Atel.Analyzers;

/// <summary>
///     The reference assembly's types an analyzer needs, and what the language makes of the
///     compilation's own (docs/plan.md, "The language"). <see cref="Create"/> returns null for a
///     compilation that does not reference <c>Ffx.Atel.Game</c>: then it is not Atel, and no rule runs.
/// </summary>
internal sealed class AtelModel {
    public Compilation Compilation { get; }
    public INamedTypeSymbol Worker { get; }
    public INamedTypeSymbol? Worker0 { get; }
    public INamedTypeSymbol? Task { get; }
    public INamedTypeSymbol? TaskOfT { get; }
    public INamedTypeSymbol? NativeAttribute { get; }
    public INamedTypeSymbol? InlineAttribute { get; }
    public INamedTypeSymbol? EntryAttribute { get; }
    public INamedTypeSymbol? RegisterAttribute { get; }
    public INamedTypeSymbol? WorkersAttribute { get; }

    private readonly ConcurrentDictionary<IMethodSymbol, bool> polls = new(SymbolEqualityComparer.Default);
    private ImmutableDictionary<INamedTypeSymbol, int>? workerIndex;

    private AtelModel(Compilation compilation, INamedTypeSymbol worker) {
        Compilation = compilation;
        Worker = worker;
        Worker0 = compilation.GetTypeByMetadataName("Ffx.Atel.Worker0");
        Task = compilation.GetTypeByMetadataName("Ffx.Atel.Task");
        TaskOfT = compilation.GetTypeByMetadataName("Ffx.Atel.Task`1");
        NativeAttribute = compilation.GetTypeByMetadataName("Ffx.Atel.NativeAttribute");
        InlineAttribute = compilation.GetTypeByMetadataName("Ffx.Atel.InlineAttribute");
        EntryAttribute = compilation.GetTypeByMetadataName("Ffx.Atel.EntryAttribute");
        RegisterAttribute = compilation.GetTypeByMetadataName("Ffx.Atel.RegisterAttribute");
        WorkersAttribute = compilation.GetTypeByMetadataName("Ffx.Atel.WorkersAttribute");
    }

    public static AtelModel? Create(Compilation compilation) =>
        compilation.GetTypeByMetadataName("Ffx.Atel.Worker") is INamedTypeSymbol worker ? new AtelModel(compilation, worker) : null;

    // ----- Where symbols come from ----------------------------------------------------------

    /// <summary>Declared in the .NET base library (namespaces <c>System</c> and <c>Microsoft</c>).</summary>
    /// <summary>A member of the object-oriented surface's attribute (<c>Ffx.Atel.NativeMemberAttribute</c>, docs/language.md, "Members").</summary>
    public static bool IsNativeMember(INamedTypeSymbol? attribute) =>
        attribute is { Name: "NativeMemberAttribute", ContainingNamespace: { Name: "Atel", ContainingNamespace: { Name: "Ffx", ContainingNamespace.IsGlobalNamespace: true } } };

    /// <summary>The reference assembly's <c>Ffx.Vector3</c>: three floats, only ever the value of a property.</summary>
    public static bool IsVector3(ITypeSymbol? type) =>
        type is { TypeKind: TypeKind.Struct, Name: "Vector3", ContainingNamespace: { Name: "Ffx", ContainingNamespace.IsGlobalNamespace: true } };

    public static bool IsBaseLibrary(ISymbol symbol) {
        INamespaceSymbol? ns = symbol as INamespaceSymbol ?? symbol.ContainingNamespace;
        if (symbol is IArrayTypeSymbol) return false;
        while (ns is { IsGlobalNamespace: false, ContainingNamespace.IsGlobalNamespace: false }) ns = ns.ContainingNamespace;
        return ns is { IsGlobalNamespace: false, Name: "System" or "Microsoft" };
    }

    /// <summary>Namespace <paramref name="name"/> or one inside it.</summary>
    public static bool InNamespace(ISymbol symbol, string name) {
        string ns = (symbol as INamespaceSymbol ?? symbol.ContainingNamespace)?.ToDisplayString() ?? "";
        return ns == name || ns.StartsWith(name + ".", System.StringComparison.Ordinal);
    }

    /// <summary>Declared in this compilation's source (as opposed to the reference assembly: natives, base members).</summary>
    public static bool IsSource(ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);

    // ----- Workers and entries ----------------------------------------------------------------

    public bool IsWorker(ITypeSymbol? type) => DerivesFrom(type, Worker);

    /// <summary>A function worker: a <c>Worker0</c> (runs on the caller's worker through <c>call</c>).</summary>
    public bool IsFunctionWorker(ITypeSymbol? type) => Worker0 is not null && DerivesFrom(type, Worker0);

    private static bool DerivesFrom(ITypeSymbol? type, INamedTypeSymbol baseType) {
        for (ITypeSymbol? t = type; t is not null; t = t.BaseType) {
            if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, baseType)) return true;
        }
        return false;
    }

    /// <summary><c>Ffx.Atel.Task</c> or <c>Ffx.Atel.Task&lt;T&gt;</c>: a request.</summary>
    public bool IsTask(ITypeSymbol? type) =>
        type is not null && (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, Task) || SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, TaskOfT));

    /// <summary>
    ///     An entry: what the engine or a request runs on the worker's own level. <c>[Entry(n)]</c>, an
    ///     <c>override</c> of a base class entry, or a requestable <c>async</c> method.
    /// </summary>
    public bool IsEntry(IMethodSymbol method) =>
        method.MethodKind == MethodKind.Ordinary && (method.IsAsync || IsTask(method.ReturnType) || method.IsOverride || EntryNumbers(method).Any());

    /// <summary>The <c>[Entry(n)]</c> numbers on a method.</summary>
    public IEnumerable<int> EntryNumbers(IMethodSymbol method) {
        foreach (AttributeData a in method.GetAttributes()) {
            if (SymbolEqualityComparer.Default.Equals(a.AttributeClass, EntryAttribute) && a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is int n) {
                yield return n;
            }
        }
    }

    /// <summary>
    ///     Code ATEL runs with <c>call</c>: a method declared in source that is not an entry (a helper), or
    ///     a static method of a static class (a function).
    /// </summary>
    public bool IsCallable(IMethodSymbol method) =>
        method.MethodKind == MethodKind.Ordinary && IsSource(method) && !IsEntry(method) && !IsInline(method);

    /// <summary>An inline method (<c>[Inline]</c>): its code is written in place of each call, so a call is no <c>call</c>.</summary>
    public bool IsInline(IMethodSymbol method) =>
        InlineAttribute is not null && method.OriginalDefinition.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, InlineAttribute));

    /// <summary>A native: a method of the reference assembly with <c>[Native]</c>.</summary>
    /// <summary>A native that polls, or an inline method that waits (<c>[Inline(Waits = true)]</c>).</summary>
    public bool Polls(IMethodSymbol method) => polls.GetOrAdd(method.OriginalDefinition, m => m.GetAttributes().Any(a =>
        ((SymbolEqualityComparer.Default.Equals(a.AttributeClass, NativeAttribute) || IsNativeMember(a.AttributeClass))
            && a.NamedArguments.Any(n => n.Key == "Polls" && n.Value.Value is true))
        || (SymbolEqualityComparer.Default.Equals(a.AttributeClass, InlineAttribute) && a.NamedArguments.Any(n => n.Key == "Waits" && n.Value.Value is true))));

    /// <summary>Whether a <c>Worker</c> member named <paramref name="name"/> is <paramref name="method"/>.</summary>
    public bool IsWorkerMember(IMethodSymbol method, string name) =>
        method.Name == name && SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, Worker);

    /// <summary>
    ///     Whether an operation waits: <c>await</c>, a native that polls, or a request that waits
    ///     (<c>RequestWaitStart</c>, <c>RequestWaitEnd</c>).
    /// </summary>
    public bool Waits(IOperation operation) => operation switch {
        IAwaitOperation => true,
        IInvocationOperation i => Polls(i.TargetMethod) || IsWorkerMember(i.TargetMethod, "RequestWaitStart") || IsWorkerMember(i.TargetMethod, "RequestWaitEnd"),
        _ => false,
    };

    /// <summary>Each worker's number: its position in the script class's <c>[Workers(…)]</c>.</summary>
    public ImmutableDictionary<INamedTypeSymbol, int> WorkerIndex {
        get {
            if (workerIndex is not null) return workerIndex;
            ImmutableDictionary<INamedTypeSymbol, int>.Builder b = ImmutableDictionary.CreateBuilder<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
            if (WorkersAttribute is not null) {
                foreach (INamedTypeSymbol type in SourceTypes(Compilation.Assembly.GlobalNamespace)) {
                    foreach (AttributeData a in type.GetAttributes()) {
                        if (!SymbolEqualityComparer.Default.Equals(a.AttributeClass, WorkersAttribute) || a.ConstructorArguments.Length != 1) continue;
                        TypedConstant list = a.ConstructorArguments[0];
                        if (list.Kind != TypedConstantKind.Array) continue;
                        for (int k = 0; k < list.Values.Length; k++) {
                            if (list.Values[k].Value is INamedTypeSymbol w && !b.ContainsKey(w)) b.Add(w, k);
                        }
                    }
                }
            }
            return workerIndex = b.ToImmutable();
        }
    }

    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol ns) {
        foreach (INamespaceOrTypeSymbol member in ns.GetMembers()) {
            if (member is INamespaceSymbol inner) {
                foreach (INamedTypeSymbol t in SourceTypes(inner)) yield return t;
            }
            else if (member is INamedTypeSymbol type && IsSource(type)) {
                yield return type;
            }
        }
    }
}
