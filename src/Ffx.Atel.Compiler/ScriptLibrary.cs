// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

/// <summary>
///     Library classes workers derive from (docs/plan.md, "Shared behaviour"): <c>class W03 : Shared0001 { }</c>
///     takes its type, register fields and entry methods from <c>Shared0001</c>, laid out before its own.
///     Shared by all the scripts compiled with it; parsed once.
/// </summary>
public sealed class ScriptLibrary {
    private readonly Lazy<CSharpCompilation> _compilation;
    private readonly Lazy<MetadataReference> _reference;

    private ScriptLibrary(IReadOnlyList<SyntaxTree> trees) {
        Trees = [.. trees.OrderBy(t => t.FilePath, SourceOrder.Comparer)];
        Classes = Trees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()).ToLookup(c => c.Identifier.ValueText, StringComparer.Ordinal);
        _compilation = new(() => ScriptCompiler.Compilation("AtelLibrary", Trees, []));
        _reference = new(() => _compilation.Value.ToMetadataReference());
    }

    /// <summary>No library classes.</summary>
    public static ScriptLibrary Empty { get; } = new([]);

    public IReadOnlyList<SyntaxTree> Trees { get; }

    /// <summary>The classes by name, their parts in <see cref="SourceOrder"/>.</summary>
    internal ILookup<string, ClassDeclarationSyntax> Classes { get; }

    /// <summary>The library as a reference for checking scripts against it.</summary>
    internal MetadataReference Reference => _reference.Value;

    public static ScriptLibrary FromTrees(IEnumerable<SyntaxTree> trees) => new([.. trees]);

    /// <summary>The library in a folder (every <c>.cs</c> but <c>obj/</c>, <c>bin/</c>).</summary>
    public static ScriptLibrary FromFolder(string folder) => new(ScriptCompiler.ParseFolder(folder));

    /// <summary>
    ///     The library of a script folder: the nearest <c>Library</c> folder beside it or above it (the root
    ///     <c>atel decompile</c> writes), or <see cref="Empty"/>.
    /// </summary>
    public static ScriptLibrary Find(string scriptFolder) {
        for (DirectoryInfo? d = new DirectoryInfo(scriptFolder).Parent; d is not null; d = d.Parent) {
            string candidate = Path.Combine(d.FullName, "Library");
            if (Directory.Exists(candidate) && !Path.GetFullPath(scriptFolder).StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
                return FromFolder(candidate);
            }
        }
        return Empty;
    }

    /// <summary>The library's own C# diagnostics (warnings and errors).</summary>
    public IReadOnlyList<Diagnostic> Check() => ScriptCompiler.Diagnose(_compilation.Value);
}
