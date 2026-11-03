// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace Ffx.Atel.Decompiler.Layout;

/// <summary>A worker's source: the attributes, the class line's parts and the class body.</summary>
/// <param name="Name">The class (<c>W03</c>).</param>
/// <param name="Number">Its position in <c>[Workers]</c>.</param>
/// <param name="Attributes">The lines before the class (<c>[Worker(…)]</c>, <c>[PrivateData(…)]</c>), each ending in <c>\n</c>.</param>
/// <param name="Base">The base class: <c>Worker1</c>, or a library class that holds the body.</param>
/// <param name="Body">The members (register fields, entry methods), indented, each line ending in <c>\n</c>; empty when the base holds them.</param>
public sealed record WorkerSource(string Name, int Number, string Attributes, string Base, string Body) {
    /// <summary>
    ///     The class, as it stands in a single file: <c>partial</c>, so that a mod can add to it
    ///     (<c>partial class W03 { … }</c>, docs/formats/mod-package.md).
    /// </summary>
    public string Text => Attributes + Declaration(partial: true) + Body + "}\n";

    internal string Declaration(bool partial) =>
        string.Create(CultureInfo.InvariantCulture, $"public sealed {(partial ? "partial " : "")}class {Name} : {Base}\n{{\n");
}

/// <summary>
///     A decompiled script in parts: the script class, its workers and its data areas. <see cref="SingleFile"/>
///     puts them in one file (Phase 2's shape); <see cref="SourceLayout"/> gives each its own file.
/// </summary>
/// <param name="Names">Namespace, class and file names.</param>
/// <param name="ShortPath">The script's archive path, without the common prefix (for the comment at the top).</param>
/// <param name="ScriptClass">The script class with its attributes and variables.</param>
/// <param name="Workers">The workers, in <c>[Workers]</c> order.</param>
/// <param name="Data">The second part of the script class, with the data areas; null when the script has none.</param>
public sealed record ScriptSource(SourceNames Names, string ShortPath, string ScriptClass, IReadOnlyList<WorkerSource> Workers, string? Data) {
    /// <summary>The lines every file starts with: where it comes from, the usings and the namespace.</summary>
    public string Prelude(params IEnumerable<string> extraUsings) {
        StringBuilder sb = new();
        sb.Append(CultureInfo.InvariantCulture, $"// {ShortPath}, decompiled by atel.\n");
        // A script the PC build does not load, or loads only behind a debug switch (data/areas.json).
        if (AreaNames.ScriptNote(Names.Name) is string note) sb.Append(CultureInfo.InvariantCulture, $"// {note}\n");
        sb.Append("using Ffx;\nusing Ffx.Atel;\n");
        foreach (string u in extraUsings) sb.Append(CultureInfo.InvariantCulture, $"using {u};\n");
        sb.Append(CultureInfo.InvariantCulture, $"using static {Names.Namespace}.{Names.ClassName};\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"namespace {Names.Namespace};\n\n");
        return sb.ToString();
    }

    /// <summary>The namespaces of the library classes the workers derive from (<c>using Library;</c>).</summary>
    public IReadOnlyList<string> LibraryNamespaces { get; init; } = [];

    /// <summary>The whole script as one file: the script class, the workers, the data areas.</summary>
    public string SingleFile() {
        StringBuilder sb = new(Prelude(LibraryNamespaces));
        sb.Append(ScriptClass);
        foreach (WorkerSource w in Workers) sb.Append('\n').Append(w.Text);
        if (Data is not null) sb.Append('\n').Append(Data);
        return sb.ToString();
    }
}
