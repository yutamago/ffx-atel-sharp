// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Ffx.Atel.Bytecode;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Decompiler.Layout;

/// <summary>A source file: its path relative to the source root (with <c>/</c>), and its text.</summary>
public sealed record SourceFile(string RelativePath, string Text);

/// <summary>
///     The folder layout of a script (docs/plan.md, "Scripts, files and order"): the script is a folder
///     and a namespace, <c>Events/Bj/Bjyt0400/</c>, holding
///     <list type="bullet">
///         <item><c>Bjyt0400.cs</c>: the script class with its attributes and variables;</item>
///         <item>one file per worker, <c>W03.cs</c>; a big worker as <c>partial</c> parts, <c>W36.cs</c>,
///         <c>W36.2.cs</c>, … split between members (the parts' order is the order of their paths
///         without <c>.cs</c>, which is the order the compiler lays the methods out in);</item>
///         <item><c>Bjyt0400.Data.cs</c>: the data areas, a second part of the script class.</item>
///     </list>
///     The worker order is <c>[Workers]</c>'s, not the files'.
/// </summary>
public static class SourceLayout {
    /// <summary>Workers with more lines than this are split into parts.</summary>
    public const int SplitLines = 4000;

    /// <summary>The lines a part holds at least before the next member starts a new one.</summary>
    public const int PartLines = 2000;

    /// <summary>The script's folder, relative to the source root: its namespace as folders.</summary>
    public static string Folder(SourceNames names) => string.Join('/', names.Namespace.Split('.'));

    /// <summary>
    ///     The script's files, in <see cref="SourceOrder"/>. A worker of more than <paramref name="splitLines"/>
    ///     lines is split into parts of at least <paramref name="partLines"/> lines each (the last aside).
    /// </summary>
    public static List<SourceFile> Files(ScriptSource script, int splitLines = SplitLines, int partLines = PartLines) {
        string folder = Folder(script.Names);
        string cls = script.Names.ClassName;
        List<SourceFile> files = [new($"{folder}/{cls}.cs", script.Prelude() + script.ScriptClass)];
        foreach (WorkerSource w in script.Workers) {
            string prelude = script.Prelude(w.Body.Length == 0 ? script.LibraryNamespaces : []);
            List<string> parts = Split(w.AnnotatedBody, splitLines, partLines);
            if (parts.Count == 1) {
                files.Add(new($"{folder}/{w.Name}.cs", prelude + w.Text));
                continue;
            }
            int digits = parts.Count.ToString(CultureInfo.InvariantCulture).Length;
            for (int p = 0; p < parts.Count; p++) {
                StringBuilder sb = new(prelude);
                if (p == 0) sb.Append(w.Attributes).Append(w.Declaration(partial: true));
                else sb.Append(CultureInfo.InvariantCulture, $"public sealed partial class {w.Name}\n{{\n");
                sb.Append(parts[p]).Append("}\n");
                string suffix = p == 0 ? "" : "." + (p + 1).ToString("D" + digits, CultureInfo.InvariantCulture);
                files.Add(new($"{folder}/{w.Name}{suffix}.cs", sb.ToString()));
            }
        }
        if (script.Data is not null) files.Add(new($"{folder}/{cls}.Data.cs", script.Prelude() + script.Data));
        files.Sort((a, b) => SourceOrder.Compare(a.RelativePath, b.RelativePath));
        return files;
    }

    /// <summary>A class body cut between members into parts of at least <paramref name="partLines"/> lines.</summary>
    internal static List<string> Split(string body, int splitLines, int partLines) {
        if (Lines(body) <= splitLines) return [body];
        ClassDeclarationSyntax c = CSharpSyntaxTree.ParseText("class C\n{\n" + body + "}\n").GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        List<string> members = [.. c.Members.Select(m => m.ToFullString())];
        // The members are the whole body (but whitespace before the closing brace); if not, keep it whole.
        string all = string.Concat(members);
        if (members.Count < 2 || !body.StartsWith(all, StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(body[all.Length..])) return [body];
        List<string> parts = [];
        StringBuilder part = new();
        int lines = 0;
        foreach (string member in members) {
            string text = member;
            if (lines >= partLines) {
                parts.Add(part.ToString());
                part.Clear();
                lines = 0;
                text = text.TrimStart('\n');
            }
            part.Append(text);
            lines += Lines(text);
        }
        parts.Add(part.ToString());
        parts[^1] += body[all.Length..];
        return parts;
    }

    private static int Lines(string s) => s.Count(ch => ch == '\n');
}
