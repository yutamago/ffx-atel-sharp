// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace Ffx.Atel.Decompiler.Layout;

/// <summary>A worker's source: the attributes, the class line's parts and the class body.</summary>
/// <param name="Name">The class (<c>W03</c>).</param>
/// <param name="Number">Its position in <c>[Workers]</c>.</param>
/// <param name="Attributes">
///     The lines before the class in its own file (comments, such as who requests the entries of a worker whose
///     body is a library class's), each ending in <c>\n</c>. The layout attributes are <see cref="Pins"/> and
///     <see cref="PrivateData"/>.
/// </param>
/// <param name="Base">The base class: <c>Worker1</c>, or a library class that holds the body.</param>
/// <param name="Body">The members (register fields, entry methods), indented, each line ending in <c>\n</c>; empty when the base holds them.</param>
public sealed record WorkerSource(string Name, int Number, string Attributes, string Base, string Body) {
    /// <summary>
    ///     The class, as it stands in a single file: <c>partial</c>, so that a mod can add to it
    ///     (<c>partial class W03 { … }</c>, docs/formats/mod-package.md).
    /// </summary>
    public string Text => Attributes + Pins + PrivateData + ClassText;

    /// <summary>The class in its own file (<see cref="SourceLayout"/>): <see cref="Text"/> without the pins and the private data.</summary>
    public string FileText => Attributes + ClassText;

    private string ClassText => Declaration(partial: true) + AnnotatedBody + "}\n";

    /// <summary>
    ///     <c>[Worker(…)]</c> when the worker's header has more than its type (<c>PrivateLength</c>, <c>Word0C</c>),
    ///     ending in <c>\n</c>; else empty. In a folder it goes to the script's layout part (<see cref="SourceLayout"/>).
    /// </summary>
    public string Pins { get; init; } = "";

    /// <summary>
    ///     <c>[PrivateData(…)]</c>, the worker's private data area, ending in <c>\n</c>; else empty. In a folder it goes to
    ///     the script's data part.
    /// </summary>
    public string PrivateData { get; init; } = "";

    /// <summary>A part of the class that holds only <paramref name="attributes"/> (in the layout and data parts).</summary>
    internal string AttributePart(string attributes) =>
        string.Create(CultureInfo.InvariantCulture, $"{attributes}partial class {Name}\n{{\n}}\n");

    /// <summary>The worker's code as a hash (<see cref="WorkerSignature"/>), the key of a library class's name; null when unknown.</summary>
    public string? Signature { get; init; }

    /// <summary>
    ///     Comments that go into <see cref="Body"/> (who requests an entry, <see cref="FlowNotes"/>): each at an
    ///     offset into it where a method starts, in order, its lines indented and ending in <c>\n</c>. Kept out
    ///     of <see cref="Body"/>, so that workers whose code is the same still share a library class.
    /// </summary>
    public IReadOnlyList<(int Offset, string Text)> Notes { get; init; } = [];

    /// <summary>The same comments for the class line, for when the body moves to a library class.</summary>
    public string ClassNotes { get; init; } = "";

    /// <summary><see cref="Body"/> with the <see cref="Notes"/> in it.</summary>
    public string AnnotatedBody {
        get {
            if (Notes.Count == 0) return Body;
            StringBuilder sb = new(Body.Length + Notes.Sum(n => n.Text.Length));
            int at = 0;
            foreach ((int offset, string text) in Notes) {
                sb.Append(Body, at, offset - at).Append(text);
                at = offset;
            }
            return sb.Append(Body, at, Body.Length - at).ToString();
        }
    }

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
/// <param name="Layout">
///     The part of the script class with the attributes that only pin the layout: <c>[Header]</c>, <c>[Pin]</c>,
///     <c>[Patch]</c>, the constant pools.
/// </param>
/// <param name="Data">The part of the script class with the data areas; null when the script has none.</param>
public sealed record ScriptSource(SourceNames Names, string ShortPath, string ScriptClass, IReadOnlyList<WorkerSource> Workers, string Layout, string? Data) {
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

    /// <summary>The whole script as one file: the script class, the workers (with their attributes), the layout, the data areas.</summary>
    public string SingleFile() {
        StringBuilder sb = new(Prelude(LibraryNamespaces));
        sb.Append(ScriptClass);
        foreach (WorkerSource w in Workers) sb.Append('\n').Append(w.Text);
        sb.Append('\n').Append(Layout);
        if (Data is not null) sb.Append('\n').Append(Data);
        return sb.ToString();
    }

    /// <summary>
    ///     The layout file's text (after the prelude): <see cref="Layout"/>, then a part per worker that has
    ///     <see cref="WorkerSource.Pins"/>.
    /// </summary>
    public string LayoutPart() {
        StringBuilder sb = new(Layout);
        foreach (WorkerSource w in Workers.Where(w => w.Pins.Length > 0)) sb.Append('\n').Append(w.AttributePart(w.Pins));
        return sb.ToString();
    }

    /// <summary>
    ///     The data file's text (after the prelude): <see cref="Data"/>, then a part per worker that has
    ///     <see cref="WorkerSource.PrivateData"/>; null when there is neither.
    /// </summary>
    public string? DataPart() {
        List<WorkerSource> own = [.. Workers.Where(w => w.PrivateData.Length > 0)];
        if (Data is null && own.Count == 0) return null;
        StringBuilder sb = new(Data ?? DataComment);
        foreach (WorkerSource w in own) sb.Append(sb.Length > 0 ? "\n" : "").Append(w.AttributePart(w.PrivateData));
        return sb.ToString();
    }

    /// <summary>The comment at the top of the data part.</summary>
    internal const string DataComment = "// The data areas: the bytes the script's code reads, as the game has them.\n";
}
