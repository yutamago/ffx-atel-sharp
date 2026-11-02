// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

public static partial class ScriptCompiler {
    /// <summary>
    ///     The entries a method places: its <c>[Entry(n)]</c> numbers in order, or for an <c>Init</c> or
    ///     <c>Main</c> override without <c>[Entry]</c> entry 0 or 1 (the engine's entries).
    /// </summary>
    private static List<int> EntriesOf(MethodDeclarationSyntax method, bool functionWorker = false) {
        List<int> entries = [];
        foreach (AttributeSyntax e in method.AttributeLists.SelectMany(l => l.Attributes)) {
            if (NameOf(e) == "Entry") entries.Add(I32(Positional(e, 0), e));
        }
        // A function worker's one entry is `public static void Run()` (docs/plan.md, "Three kinds of code").
        if (entries.Count == 0 && functionWorker && method.Identifier.ValueText == "Run" && method.Modifiers.Any(SyntaxKind.StaticKeyword)) entries.Add(0);
        if (entries.Count == 0 && method.Modifiers.Any(SyntaxKind.OverrideKeyword)) {
            switch (method.Identifier.ValueText) {
                case "Init": entries.Add(0); break;
                case "Main": entries.Add(1); break;
            }
        }
        return entries;
    }

    /// <summary>
    ///     For each worker, its methods by name with the first entry each places: a request written as a
    ///     call (<c>_ = Lulu.E5Async();</c>) names entry 5 of worker <c>Lulu</c> (docs/plan.md, "Requests
    ///     between workers").
    /// </summary>
    private static Dictionary<string, Dictionary<string, int>> EntryMethods(
        ILookup<string, ClassDeclarationSyntax> classes, IReadOnlyDictionary<string, int> workers, ScriptLibrary library) {
        Dictionary<string, Dictionary<string, int>> byWorker = new(StringComparer.Ordinal);
        foreach (string worker in workers.Keys) {
            Dictionary<string, int> methods = new(StringComparer.Ordinal);
            // The worker's own methods, then its library base classes' (an inherited static method is
            // named through the worker: `W03.E5Async()`).
            List<ClassDeclarationSyntax> parts = [.. classes[worker]];
            HashSet<string> seen = new(StringComparer.Ordinal);
            for (List<ClassDeclarationSyntax> level = parts; level.Count > 0;) {
                foreach (MethodDeclarationSyntax m in level.SelectMany(c => c.Members.OfType<MethodDeclarationSyntax>())) {
                    if (EntriesOf(m) is [int first, ..]) methods.TryAdd(m.Identifier.ValueText, first);
                }
                BaseTypeSyntax? b = level.Select(p => p.BaseList?.Types.FirstOrDefault()).LastOrDefault(x => x is not null);
                level = b is not null && seen.Add(SimpleName(b.Type)) ? [.. library.Classes[SimpleName(b.Type)]] : [];
            }
            byWorker[worker] = methods;
        }
        return byWorker;
    }
}
