// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Compiler;
using Ffx.Atel.Decompiler.Layout;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Cli;

/// <summary><c>atel compile-mod</c> and <c>atel link</c> (docs/formats/mod-package.md).</summary>
internal static class ModCommands {
    public const string Usage = """
          compile-mod <mod dir> --vanilla <dir> -o <out dir> [--id <id>] [--check] [--debug <mode>]
                                           compile a mod's C# (partial classes of the decompiled
                                           scripts under --vanilla, the root `atel decompile -o`
                                           wrote) into packages: <out>/atel/<archive path>.json, its
                                           mod.toml, and with --id a Fahrenheit manifest if none.
                                           A package is also a runtime injection package: put it
                                           in the loader's inject/ folder (docs/formats/debug.md)
          link <mod dir>... -o <out dir>   link mods (in this order) onto the game's files as the
                                           loader does: <out>/<archive path>, and the report
        """;

    public static int CompileMod(VbfArchive archive, List<string> args, string? output, string? vanilla, string? id, bool check, CompileOptions options) {
        if (args.Count != 1 || output is null || vanilla is null) throw new ArgumentException("compile-mod <mod dir> --vanilla <dir> -o <out dir>");
        string modDir = args[0];
        if (!Directory.Exists(modDir)) throw new ArgumentException($"no folder {modDir}");
        string fullOut = Path.GetFullPath(output);
        List<string> files = [.. ScriptCompiler.SourceFiles(modDir).Where(f => !Path.GetFullPath(f).StartsWith(fullOut + Path.DirectorySeparatorChar, StringComparison.Ordinal))];
        if (files.Count == 0) throw new ArgumentException($"no .cs files in {modDir}");

        // A script per namespace: the mod's files for Monsters.M209 go with <vanilla>/Monsters/M209. A file
        // with only assembly attributes ([assembly: EnableCut(…)]) needs no namespace.
        Dictionary<string, List<SyntaxTree>> byScript = new(StringComparer.Ordinal);
        List<string> cuts = [];
        List<SyntaxTree> all = [], savedOnly = [];
        foreach (string f in files) {
            SyntaxTree tree = ScriptCompiler.Parse(File.ReadAllText(f), f);
            all.Add(tree);
            cuts.AddRange(EnabledCuts(tree, f));
            CompilationUnitSyntax root = (CompilationUnitSyntax)tree.GetRoot();
            string? ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
            if (ns is null && root.Members.Count == 0 && root.AttributeLists.Count > 0) continue;
            // A file of the mod's [Saved] classes only (plain static classes) changes no script.
            if (IsSavedOnly(root)) {
                savedOnly.Add(tree);
                continue;
            }
            if (ns is null) throw new ArgumentException($"{f}: no namespace; a mod's file is in the namespace of the script it changes");
            if (!byScript.TryGetValue(ns, out List<SyntaxTree>? list)) byScript[ns] = list = [];
            list.Add(tree);
        }
        // Saved fields: the mod's own go to atel/saved.json; another mod's it reads need that mod in mod.toml's requires.
        SavedDeclarations saved = ScriptCompiler.ReadSaved(all);
        string toml = Path.Combine(modDir, AtelMod.ManifestFile);
        ModManifest modToml = File.Exists(toml) ? ModManifest.Parse(File.ReadAllText(toml)) : ModManifest.Empty;    // fails early on a broken one
        if (saved.ReadMods.FirstOrDefault(m => !modToml.Requires.Contains(m)) is string missing) {
            throw new ArgumentException($"{modDir}: reads {missing}'s saved fields; add requires = [\"{missing}\"] to its {AtelMod.ManifestFile}");
        }
        if (byScript.Count == 0 && cuts.Count == 0 && saved.Fields.Count == 0) throw new ArgumentException($"{modDir}: no script changes, no [Saved] fields and no [assembly: EnableCut(…)]");
        Dictionary<string, ScriptPackage> packages = new(StringComparer.Ordinal);
        foreach ((string ns, List<SyntaxTree> modTrees) in byScript.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
            string folder = Path.Combine([vanilla, .. ns.Split('.')]);
            if (!Directory.Exists(folder)) throw new ArgumentException($"{ns}: no vanilla source in {folder}");
            List<SyntaxTree> vanillaTrees = ScriptCompiler.ParseFolder(folder);
            ScriptLibrary library = ScriptLibrary.Find(folder);
            if (check) {
                IReadOnlyList<Diagnostic> diagnostics = ScriptCompiler.Check([.. vanillaTrees, .. modTrees, .. savedOnly], library);
                foreach (Diagnostic d in diagnostics) Console.Error.WriteLine(d.ToString());
                if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) return 1;
            }
            CompiledMod mod = ScriptCompiler.CompileMod(vanillaTrees, modTrees, library, options, saved);
            foreach (string w in mod.Warnings) Console.Error.WriteLine($"warning: {w}");
            string path = GameData.Find(archive, mod.Category, mod.Name) ?? throw new ArgumentException($"the game has no {mod.Category} {mod.Name}");
            mod.Package.File = path;
            // Data edits: the hashes of the areas they change, from the player's file.
            DataAreas.Attach(mod.Package, archive.Read(archive.Find(path)!));
            packages[path] = mod.Package;
        }
        foreach (string name in cuts.Distinct(StringComparer.Ordinal)) AddCut(archive, packages, name);
        int count = 0;
        foreach ((string path, ScriptPackage p) in packages.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
            string target = Path.Combine([output, AtelMod.PackageDir, .. (path + ".json").Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, p.ToJson());
            string switched = p.Cuts is { Count: > 0 } c ? $", cuts {string.Join(", ", c.Select(x => $"{x.Name} ({x.Blocks} block(s))"))}" : "";
            string data = p.Data is { Count: > 0 } d ? $", {d.Count} data edits" : "";
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{p.Script}: {p.Patches.Count} entries and patches, {p.Workers.Count} new workers, {p.Variables.Count} new variables{data}{switched} -> {target}"));
            count++;
        }
        if (saved.ToFile() is SavedFile savedFile) {
            string target = Path.Combine(output, AtelMod.PackageDir, SavedFile.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, savedFile.ToJson());
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{savedFile.Fields.Count} saved field(s), {savedFile.Fields.Sum(f => f.Size)} of {SaveArea.Length} bytes -> {target}"));
        }
        if (File.Exists(toml)) File.Copy(toml, Path.Combine(output, AtelMod.ManifestFile), overwrite: true);
        if (id is not null && !Directory.EnumerateFiles(output, "*.manifest.json").Any()) {
            // A file-only Fahrenheit mod: its manifest, depending on the loader.
            var manifest = new Dictionary<string, object> {
                ["Id"] = id, ["Name"] = id, ["Desc"] = "An Atel mod.", ["Authors"] = "", ["Version"] = "0.1.0", ["Link"] = "",
                ["Dependencies"] = new[] { "yuta.atel" }, ["Flags"] = "NONE",
            };
            File.WriteAllText(Path.Combine(output, $"{id}.manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.Error.WriteLine($"{count} script package(s) in {output}");
        return 0;
    }

    /// <summary>Whether a file declares only the mod's classes of <c>[Saved]</c> fields (plain static classes).</summary>
    internal static bool IsSavedOnly(CompilationUnitSyntax root) {
        List<BaseTypeDeclarationSyntax> types = [.. root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()];
        return types.Count > 0 && types.All(t => t is ClassDeclarationSyntax c && SavedDeclarations.IsSavedClass(c));
    }

    /// <summary>
    ///     The names a file's <c>[assembly: EnableCut(nameof(Cut.Name))]</c> attributes switch on (docs/research/dead-code.md,
    ///     "Switching a block on"); the argument is a <c>nameof</c> of a <c>Cut</c> constant or a string.
    /// </summary>
    internal static IEnumerable<string> EnabledCuts(SyntaxTree tree, string file) {
        CompilationUnitSyntax root = (CompilationUnitSyntax)tree.GetRoot();
        foreach (AttributeListSyntax list in root.AttributeLists.Where(l => l.Target?.Identifier.Text == "assembly")) {
            foreach (AttributeSyntax a in list.Attributes) {
                string attribute = a.Name.ToString();
                if (attribute.Split('.')[^1] is not ("EnableCut" or "EnableCutAttribute")) continue;
                ExpressionSyntax? arg = a.ArgumentList?.Arguments.Count == 1 ? a.ArgumentList.Arguments[0].Expression : null;
                string? name = arg switch {
                    InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } } call when call.ArgumentList.Arguments.Count == 1 =>
                        call.ArgumentList.Arguments[0].Expression.ToString().Split('.')[^1].Trim(),
                    LiteralExpressionSyntax lit when lit.Token.Value is string text => text,
                    _ => null,
                };
                yield return name ?? throw new ArgumentException($"{file}: EnableCut takes one name: nameof(Cut.Name)");
            }
        }
    }

    /// <summary>
    ///     Puts the switch for the named dead block <paramref name="name"/> into the package of each script its
    ///     blocks are in (<c>data/cut-names.toml</c>), with their signatures and how many the player's script has.
    /// </summary>
    internal static void AddCut(VbfArchive archive, Dictionary<string, ScriptPackage> packages, string name) {
        CutNames names = CutNames.Default;
        if (names.SignaturesOf(name).Length == 0) {
            throw new ArgumentException($"EnableCut({name}): no dead block has that name (data/cut-names.toml; the names are the constants of Ffx.Cut)");
        }
        if (!names.Scripts.TryGetValue(name, out string[]? scripts)) throw new ArgumentException($"EnableCut({name}): data/cut-names.toml lists no script for it");
        foreach (string script in scripts) {
            string path = GameData.Resolve(archive, script) ?? throw new ArgumentException($"EnableCut({name}): the game has no script {script}");
            ScriptFile file = ScriptFile.Parse(archive.Read(archive.Find(path)!));
            CutSwitch cut = names.Switch(name, file.Script)
                ?? throw new ArgumentException($"EnableCut({name}): the game's {script} has none of its blocks (another game version?)");
            if (!packages.TryGetValue(path, out ScriptPackage? p)) packages[path] = p = new ScriptPackage { File = path, Script = script };
            p.Cuts ??= [];
            p.Cuts.Add(cut);
            p.Format = Math.Max(p.Format, ScriptPackage.CutsFormat);
        }
    }

    public static int Link(VbfArchive archive, List<string> args, string? output) {
        if (args.Count == 0 || output is null) throw new ArgumentException("link <mod dir>... -o <out dir>");
        List<LinkMessage> messages = [];
        List<AtelMod> mods = ModSet.Resolve([.. args.Select(d => AtelMod.Load(Path.GetFileName(Path.TrimEndingDirectorySeparator(d)), d))], messages);
        SavedLayout saved = SavedLayout.Build(mods, messages, out mods);
        List<LinkedFile> files = ModSet.Link(mods, path => archive.Find(path) is { } e ? archive.Read(e) : null, saved);
        bool failed = false;
        foreach (LinkedFile f in files) {
            if (f.Result is null) {
                Console.Error.WriteLine($"{f.Path}: {f.Error}");
                failed = true;
                continue;
            }
            messages.AddRange(f.Result.Messages);
            if (!f.Result.Changed) continue;
            string target = Path.Combine([output, .. f.Path.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, f.Result.File);
            Console.Error.WriteLine($"{f.Path}: {string.Join(", ", f.Result.Applied)}");
        }
        foreach (LinkMessage m in messages) Console.WriteLine(m.ToString());
        return failed || messages.Any(m => m.Severity == LinkSeverity.Error) ? 1 : 0;
    }
}
