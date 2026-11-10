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
        AddNamingSiblings(ordered, classes, classUsers, Declared);

        // The hand names, the class with most users first: two bodies of the same code (one written with
        // a name, or async, where the other's script has none) share a signature, and the smaller gets _2.
        HashSet<string> taken = new(StringComparer.Ordinal);
        foreach (int i in Enumerable.Range(0, classes.Count).OrderByDescending(i => classes[i].Users.Count).ThenBy(i => i)) {
            if (classes[i].Signature is not string signature || !names.Names.TryGetValue(signature, out string? given)) continue;
            string name = given;
            for (int n = 2; !taken.Add(name); n++) name = string.Create(CultureInfo.InvariantCulture, $"{given}_{n}");
            classes[i] = classes[i] with { Name = name, Summary = names.Summaries.GetValueOrDefault(signature) };
        }
        // The others by rule, from their users (AutoName); a name taken twice gets _2, _3, … in order of first use.
        HashSet<string> clash = new(Naming.WorkerNaming.ReservedWith([]), StringComparer.Ordinal);
        foreach (List<(ScriptSource Script, WorkerSource Worker)> users in classUsers) {
            foreach (ScriptSource s in users.Select(u => u.Script).Distinct()) clash.UnionWith(Declared(s));
        }
        for (int i = 0; i < classes.Count; i++) {
            if (classes[i].Summary is not null || taken.Contains(classes[i].Name) || AutoName(classes[i], classUsers[i], clash) is not string auto) continue;
            string name = auto;
            for (int n = 2; taken.Contains(name) || clash.Contains(name); n++) name = string.Create(CultureInfo.InvariantCulture, $"{auto}_{n}");
            taken.Add(name);
            classes[i] = classes[i] with { Name = name };
        }
        // A body that names a sibling's library class (the second pass) gets the class's final name.
        for (int i = 0; i < classes.Count; i++) {
            if (classes[i].Body.Contains(SiblingMark, StringComparison.Ordinal)) classes[i] = classes[i] with { Body = Resolve(classes[i].Body, classes) };
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

    /// <summary>What stands for library class <c>n</c> in a body until the classes have their names: <c>__AtelLibrary17__</c>.</summary>
    private const string SiblingMark = "__AtelLibrary";

    private static string Mark(int cls) => string.Create(CultureInfo.InvariantCulture, $"{SiblingMark}{cls}__");

    private static string Resolve(string body, List<SharedWorker> classes) {
        StringBuilder sb = new(body.Length);
        int at = 0;
        for (int i = body.IndexOf(SiblingMark, StringComparison.Ordinal); i >= 0; i = body.IndexOf(SiblingMark, at, StringComparison.Ordinal)) {
            int end = body.IndexOf("__", i + SiblingMark.Length, StringComparison.Ordinal);
            int cls = int.Parse(body.AsSpan(i + SiblingMark.Length, end - i - SiblingMark.Length), CultureInfo.InvariantCulture);
            sb.Append(body, at, i - at).Append(classes[cls].Name);
            at = end + 2;
        }
        return sb.Append(body, at, body.Length - at).ToString();
    }

    /// <summary>
    ///     The second pass (docs/plan.md, Phase 6, "Library names"): a worker whose body names other workers of
    ///     its script (<c>SetMoveTalkDefaults.Run()</c>, <c>W59.Say98Async()</c>, <c>Tidus.Pos</c>) is shared when
    ///     each of them is the one worker of the script that derives from a library class, written as that class
    ///     (<c>MoveTalkDefaults.Run()</c>): the compiler resolves a library class's name to the script's worker
    ///     deriving from it (<c>ScriptCompiler</c>, "library aliases"). Bodies equal after that, with nothing else
    ///     of their script named, become classes as in the first pass (or users of an equal first-pass class).
    /// </summary>
    private static void AddNamingSiblings(List<ScriptSource> ordered, List<SharedWorker> classes, List<List<(ScriptSource Script, WorkerSource Worker)>> classUsers,
                                          Func<ScriptSource, HashSet<string>> declared) {
        Dictionary<(ScriptSource, int), int> classOf = new(new ScriptWorkerComparer());
        for (int i = 0; i < classes.Count; i++) {
            foreach (var u in classUsers[i]) classOf[(u.Script, u.Worker.Number)] = i;
        }
        Dictionary<(string Base, string Body), int> existing = [];
        for (int i = 0; i < classes.Count; i++) existing.TryAdd((classes[i].Base, classes[i].Body), i);
        Dictionary<(string Base, string Body), List<(ScriptSource Script, WorkerSource Worker)>> groups = [];
        List<(string Base, string Body)> order = [];
        foreach (ScriptSource s in ordered) {
            // The workers of the script that are the one user of their class in it, by name.
            Dictionary<string, int> alias = new(StringComparer.Ordinal);
            foreach (IGrouping<int, WorkerSource> g in s.Workers.Where(w => classOf.ContainsKey((s, w.Number))).GroupBy(w => classOf[(s, w.Number)])) {
                if (g.Count() == 1) alias[g.First().Name] = g.Key;
            }
            if (alias.Count == 0) continue;
            foreach (WorkerSource w in s.Workers) {
                if (classOf.ContainsKey((s, w.Number)) || w.Body.Length == 0) continue;
                string body = Rename(w.Body, alias, w.Name);
                if (ReferenceEquals(body, w.Body)) continue;
                if (!groups.TryGetValue((w.Base, body), out var list)) {
                    groups[(w.Base, body)] = list = [];
                    order.Add((w.Base, body));
                }
                list.Add((s, w));
            }
        }
        foreach ((string Base, string Body) key in order) {
            HashSet<string> used = Identifiers(key.Body);
            List<(ScriptSource Script, WorkerSource Worker)> users = [.. groups[key].Where(u => !used.Overlaps(declared(u.Script)))];
            if (existing.TryGetValue(key, out int known)) {
                // Only the same code: a first-pass class keeps one signature, the key of its hand name.
                users = [.. users.Where(u => u.Worker.Signature == classes[known].Signature)];
                classUsers[known].AddRange(users);
                classes[known] = classes[known] with { Users = [.. classes[known].Users, .. users.Select(u => $"{u.Script.Names.Namespace}.{u.Worker.Name}")] };
                continue;
            }
            if (users.Count < 2) continue;
            string name = string.Create(CultureInfo.InvariantCulture, $"Shared{classes.Count + 1:D4}");
            // The same text calls the siblings by their numbers, which differ between scripts: then the users'
            // code differs, the class has no signature and so no hand name (its rule name still applies).
            string? signature = users.Select(u => u.Worker.Signature).Distinct().Count() == 1 ? users[0].Worker.Signature : null;
            classes.Add(new SharedWorker(name, key.Base, key.Body, [.. users.Select(u => $"{u.Script.Names.Namespace}.{u.Worker.Name}")]) { Signature = signature });
            classUsers.Add(users);
        }
    }

    /// <summary>The body with each identifier in <paramref name="alias"/> written as its class's mark; the same string when none is.</summary>
    private static string Rename(string body, Dictionary<string, int> alias, string self) {
        StringBuilder? sb = null;
        int at = 0;
        SyntaxKind previous = SyntaxKind.None;
        foreach (SyntaxToken t in SyntaxFactory.ParseTokens(body)) {
            SyntaxKind before = previous;
            previous = t.Kind();
            // Not a member's name (Target.Tidus): only a name that starts an expression or a type argument.
            if (!t.IsKind(SyntaxKind.IdentifierToken) || before is SyntaxKind.DotToken || t.ValueText == self || !alias.TryGetValue(t.ValueText, out int cls)) continue;
            sb ??= new StringBuilder(body.Length);
            sb.Append(body, at, t.SpanStart - at).Append(Mark(cls));
            at = t.Span.End;
        }
        return sb is null ? body : sb.Append(body, at, body.Length - at).ToString();
    }

    /// <summary>
    ///     A library class's name by rule (docs/source-repo.md, "Library names"): where its users are, then
    ///     what they are. Where: the one script all users are in (<c>Bsvr0000</c>; the blitzball copies
    ///     <c>cn_</c>, <c>psv_</c>, <c>psvcn_</c> count as their script), else the one area
    ///     (<c>ThunderPlains</c>), else nothing; users in variants (test maps, other builds' copies) count only when
    ///     all are. What: the name the decompiler gave all its users (after their
    ///     model, their role or the battle handler they serve, without a number: <c>CameraLine</c>,
    ///     <c>SystemRequests2</c>, <c>Kimahri</c>), or, when they all kept their number, <c>Idle</c> and the
    ///     worker type for code that does nothing (empty methods, <c>Main</c> waiting forever). Null otherwise.
    ///     Without a place, a name some worker has takes the category in front (<c>BattleSystemRequests2</c>).
    /// </summary>
    private static string? AutoName(SharedWorker c, List<(ScriptSource Script, WorkerSource Worker)> users, HashSet<string> clash) {
        static string Strip(string name) {
            int u = name.LastIndexOf('_');
            return u > 0 && u < name.Length - 1 && name[(u + 1)..].All(char.IsAsciiDigit) ? name[..u] : name;
        }
        static bool IsDefault(string name) => name.Length > 1 && name[0] == 'W' && name[1..].All(char.IsAsciiDigit);
        string[] roles = [.. users.Select(u => Strip(u.Worker.Name)).Where(n => !IsDefault(n)).Distinct(StringComparer.Ordinal)];
        string? what = roles.Length == 1 ? roles[0]
            : roles.Length == 0 && IsIdle(c.Body) ? "Idle" + c.Base
            : null;
        if (what is null) return null;
        // The place of the scripts the game loads; copies in test maps and other builds go with them.
        List<(ScriptSource Script, WorkerSource Worker)> loaded = [.. users.Where(u => !ProjectLayout.IsVariant(u.Script.Names))];
        if (loaded.Count > 0) users = loaded;
        string[] scripts = [.. users.Select(u => BaseScript(u.Script.Names.ClassName)).Distinct(StringComparer.Ordinal)];
        if (scripts.Length == 1) return scripts[0] + what;
        string[] areas = [.. users.Select(u => u.Script.Names.Namespace.Split('.')).Select(p => p.Length > 2 ? p[1] : "").Distinct(StringComparer.Ordinal)];
        if (areas is [string area] && area.Length > 0) {
            int under = area.IndexOf('_', StringComparison.Ordinal);
            return (under >= 0 ? area[(under + 1)..] : area) + what;
        }
        // Without a place, a name a worker has (SystemRequests2) takes the category: BattleSystemRequests2.
        if (!clash.Contains(what)) return what;
        string[] categories = [.. users.Select(u => u.Script.Names.Namespace.Split('.')[0]).Distinct(StringComparer.Ordinal)];
        return categories is [string category] && category.EndsWith('s') ? category[..^1] + what : what + "Base";
    }

    /// <summary>A script's class without a variant's prefix: <c>Cn_bltz0000</c> → <c>Bltz0000</c>.</summary>
    private static string BaseScript(string className) {
        foreach (string prefix in (string[])["Psvcn_", "Psv_", "Cn_"]) {
            if (className.StartsWith(prefix, StringComparison.Ordinal) && className.Length > prefix.Length) {
                return char.ToUpperInvariant(className[prefix.Length]) + className[(prefix.Length + 1)..];
            }
        }
        return className;
    }

    /// <summary>Whether a body does nothing: its methods are empty, return, or wait forever (registers and attributes aside).</summary>
    private static bool IsIdle(string body) {
        foreach (string raw in body.Split('\n')) {
            string line = raw.Trim();
            if (line.Length == 0 || line is "{" or "}" or "return;" or "Wait.Forever();" || line.StartsWith('[') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.EndsWith(')') && !line.EndsWith(';') && (line.StartsWith("protected override ", StringComparison.Ordinal) || line.StartsWith("public static async Task ", StringComparison.Ordinal)
                                                             || line.StartsWith("void ", StringComparison.Ordinal) || line.StartsWith("async void ", StringComparison.Ordinal))) continue;
            return false;
        }
        return true;
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
