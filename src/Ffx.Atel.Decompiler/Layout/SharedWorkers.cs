// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Decompiler.Layout;

/// <summary>A library class: the body several workers share, and who uses it.</summary>
/// <param name="Name"><c>Shared0001</c>, or its name in <see cref="LibraryNames"/> (<c>EventCamera</c>).</param>
/// <param name="Base">The worker type's class it derives from (<c>Worker1</c>).</param>
/// <param name="Body">The members, as the workers had them.</param>
/// <param name="Users">The workers that derive from it: <c>Events.Bj.Bjyt0400.W03</c>.</param>
public sealed record SharedWorker(string Name, string Base, string Body, IReadOnlyList<string> Users) {
    /// <summary>The <see cref="WorkerSignature"/> of its first user's code, the key of its name; null when unknown.</summary>
    public string? Signature { get; init; }

    /// <summary>What it does, from <see cref="LibraryNames"/>; null for a numbered class.</summary>
    public string? Summary { get; init; }
}

/// <summary>The scripts with their shared workers replaced by library classes, and the library.</summary>
public sealed record SharedLibrary(IReadOnlyList<ScriptSource> Scripts, IReadOnlyList<SharedWorker> Classes) {
    /// <summary>The library's folder and namespace.</summary>
    public const string Namespace = "Library";

    /// <summary>Workers that derive from a library class.</summary>
    public int SharedWorkers => Classes.Sum(c => c.Users.Count);

    /// <summary>Source characters the library saves: each class's body once instead of once per user.</summary>
    public long SavedChars => Classes.Sum(c => (long)c.Body.Length * (c.Users.Count - 1));

    /// <summary>The library's files: <c>Library/Shared0001.cs</c>, … (big classes in parts, as workers).</summary>
    public List<SourceFile> Files(int splitLines = SourceLayout.SplitLines, int partLines = SourceLayout.PartLines) {
        List<SourceFile> files = [];
        foreach (SharedWorker c in Classes) {
            StringBuilder head = new();
            head.Append(CultureInfo.InvariantCulture, $"// The code of {c.Users.Count} workers, decompiled by atel:\n");
            foreach (string u in c.Users.Take(8)) head.Append("//   ").Append(u).Append('\n');
            if (c.Users.Count > 8) head.Append(CultureInfo.InvariantCulture, $"//   and {c.Users.Count - 8} more.\n");
            if (c.Signature is not null) head.Append(CultureInfo.InvariantCulture, $"// Signature {c.Signature} (the key of its name in ffx-atel-sharp's data/library-names.toml).\n");
            head.Append("using Ffx;\nusing Ffx.Atel;\n\n").Append(CultureInfo.InvariantCulture, $"namespace {Namespace};\n\n");
            List<string> parts = SourceLayout.Split(c.Body, splitLines, partLines);
            int digits = parts.Count.ToString(CultureInfo.InvariantCulture).Length;
            for (int p = 0; p < parts.Count; p++) {
                StringBuilder sb = new(head.ToString());
                string partial = parts.Count > 1 ? "partial " : "";
                if (p == 0 && c.Summary is not null) sb.Append("/// <summary>").Append(c.Summary.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)).Append("</summary>\n");
                sb.Append(p == 0 ? $"public abstract {partial}class {c.Name} : {c.Base}\n{{\n" : $"public abstract partial class {c.Name}\n{{\n");
                sb.Append(parts[p]).Append("}\n");
                string suffix = p == 0 ? "" : "." + (p + 1).ToString("D" + digits, CultureInfo.InvariantCulture);
                files.Add(new($"{Namespace}/{c.Name}{suffix}.cs", sb.ToString()));
            }
        }
        return files;
    }
}

/// <summary>
///     Library base classes for duplicated workers (docs/plan.md, "Shared behaviour"): workers whose
///     source body is the same text in several places become <c>class W03 : Shared0001 { }</c>, the body
///     in <c>Library/Shared0001.cs</c>, and the compiler copies it back.
/// </summary>
/// <remarks>
///     Two workers share a class only when their type and body text are equal and the body names nothing
///     its script declares (variables, workers, the script class): the library compiles on its own, and
///     the same text compiles to the same code in every script, since everything else it names
///     (natives, registers, constants by value, request numbers) means the same everywhere. Attributes
///     (<c>[Worker]</c>, <c>[PrivateData]</c>) stay with each worker, in its script's layout and data parts.
/// </remarks>
public static class SharedWorkers {
    /// <summary>
    ///     Finds the shared workers of <paramref name="scripts"/> and rewrites them. A class is named after
    ///     <paramref name="names"/> (by default this repo's <c>data/library-names.toml</c>) by its first user's
    ///     signature, else numbered (<c>Shared0108</c>) in order of first use, named classes counted too, so
    ///     that naming one does not renumber the others.
    /// </summary>
    public static SharedLibrary Build(IReadOnlyList<ScriptSource> scripts, LibraryNames? names = null) {
        names ??= LibraryNames.Default;
        // Groups by (base, body), in order of first use: scripts in path order, workers in order.
        List<ScriptSource> ordered = [.. scripts.OrderBy(s => SourceLayout.Folder(s.Names), StringComparer.Ordinal)];
        Dictionary<(string Base, string Body), List<(ScriptSource Script, WorkerSource Worker)>> groups = [];
        List<(string Base, string Body)> order = [];
        foreach (ScriptSource s in ordered) {
            foreach (WorkerSource w in s.Workers) {
                if (!groups.TryGetValue((w.Base, w.Body), out var list)) {
                    groups[(w.Base, w.Body)] = list = [];
                    order.Add((w.Base, w.Body));
                }
                list.Add((s, w));
            }
        }
        Dictionary<ScriptSource, HashSet<string>> declared = new(ReferenceEqualityComparer.Instance);
        HashSet<string> Declared(ScriptSource s) {
            if (!declared.TryGetValue(s, out HashSet<string>? names)) declared[s] = names = DeclaredNames(s);
            return names;
        }

        List<SharedWorker> classes = [];
        List<List<(ScriptSource Script, WorkerSource Worker)>> classUsers = [];
        foreach ((string Base, string Body) key in order) {
            var group = groups[key];
            if (group.Count < 2) continue;
            HashSet<string> used = Identifiers(key.Body);
            List<(ScriptSource Script, WorkerSource Worker)> users = [.. group.Where(u => !used.Overlaps(Declared(u.Script)))];
            if (users.Count < 2) continue;
            string name = string.Create(CultureInfo.InvariantCulture, $"Shared{classes.Count + 1:D4}");
            classes.Add(new SharedWorker(name, key.Base, key.Body, [.. users.Select(u => $"{u.Script.Names.Namespace}.{u.Worker.Name}")]) { Signature = users[0].Worker.Signature });
            classUsers.Add(users);
        }

        // The hand names, the class with most users first: two bodies of the same code (one written with
        // a name, or async, where the other's script has none) share a signature, and the smaller gets _2.
        HashSet<string> taken = new(StringComparer.Ordinal);
        foreach (int i in Enumerable.Range(0, classes.Count).OrderByDescending(i => classes[i].Users.Count).ThenBy(i => i)) {
            if (classes[i].Signature is not string signature || !names.Names.TryGetValue(signature, out string? given)) continue;
            string name = given;
            for (int n = 2; !taken.Add(name); n++) name = string.Create(CultureInfo.InvariantCulture, $"{given}_{n}");
            classes[i] = classes[i] with { Name = name, Summary = names.Summaries.GetValueOrDefault(signature) };
        }
        Dictionary<(ScriptSource, int), string> replaced = new(new ScriptWorkerComparer());
        for (int i = 0; i < classes.Count; i++) {
            foreach (var u in classUsers[i]) replaced[(u.Script, u.Worker.Number)] = classes[i].Name;
        }

        List<ScriptSource> result = [];
        foreach (ScriptSource s in scripts) {
            if (!s.Workers.Any(w => replaced.ContainsKey((s, w.Number)))) {
                result.Add(s);
                continue;
            }
            WorkerSource[] workers = [.. s.Workers.Select(w => replaced.TryGetValue((s, w.Number), out string? name)
                ? w with { Base = name, Body = "", Notes = [], Attributes = w.ClassNotes + w.Attributes }
                : w)];
            result.Add(s with { Workers = workers, LibraryNamespaces = [SharedLibrary.Namespace] });
        }
        return new SharedLibrary(result, classes);
    }

    /// <summary>The identifiers in a body's code (not in comments or strings).</summary>
    private static HashSet<string> Identifiers(string body) =>
        [.. SyntaxFactory.ParseTokens(body).Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText)];

    /// <summary>What a script declares that its workers can name: the script class, its fields, its workers.</summary>
    private static HashSet<string> DeclaredNames(ScriptSource s) {
        HashSet<string> names = [s.Names.ClassName, .. s.Workers.Select(w => w.Name)];
        SyntaxNode root = CSharpSyntaxTree.ParseText(s.ScriptClass + s.Layout + (s.Data ?? "")).GetRoot();
        foreach (MemberDeclarationSyntax m in root.DescendantNodes().OfType<ClassDeclarationSyntax>().SelectMany(c => c.Members)) {
            switch (m) {
                case FieldDeclarationSyntax f: names.UnionWith(f.Declaration.Variables.Select(v => v.Identifier.ValueText)); break;
                case PropertyDeclarationSyntax p: names.Add(p.Identifier.ValueText); break;
                case MethodDeclarationSyntax x: names.Add(x.Identifier.ValueText); break;
                case BaseTypeDeclarationSyntax t: names.Add(t.Identifier.ValueText); break;
            }
        }
        return names;
    }

    /// <summary>A script by reference (records compare by value, which hashes whole sources) and a worker number.</summary>
    private sealed class ScriptWorkerComparer : IEqualityComparer<(ScriptSource Script, int Worker)> {
        public bool Equals((ScriptSource Script, int Worker) x, (ScriptSource Script, int Worker) y) => ReferenceEquals(x.Script, y.Script) && x.Worker == y.Worker;

        public int GetHashCode((ScriptSource Script, int Worker) obj) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Script), obj.Worker);
    }
}
