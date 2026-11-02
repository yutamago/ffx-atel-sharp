// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Compiler;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Cli;

/// <summary><c>atel compile-mod</c> and <c>atel link</c> (docs/formats/mod-package.md).</summary>
internal static class ModCommands {
    public const string Usage = """
          compile-mod <mod dir> --vanilla <dir> -o <out dir> [--id <id>] [--check]
                                           compile a mod's C# (partial classes of the decompiled
                                           scripts under --vanilla, the root `atel decompile -o`
                                           wrote) into packages: <out>/atel/<archive path>.json, its
                                           mod.toml, and with --id a Fahrenheit manifest if none
          link <mod dir>... -o <out dir>   link mods (in this order) onto the game's files as the
                                           loader does: <out>/<archive path>, and the report
        """;

    public static int CompileMod(VbfArchive archive, List<string> args, string? output, string? vanilla, string? id, bool check) {
        if (args.Count != 1 || output is null || vanilla is null) throw new ArgumentException("compile-mod <mod dir> --vanilla <dir> -o <out dir>");
        string modDir = args[0];
        if (!Directory.Exists(modDir)) throw new ArgumentException($"no folder {modDir}");
        string fullOut = Path.GetFullPath(output);
        List<string> files = [.. ScriptCompiler.SourceFiles(modDir).Where(f => !Path.GetFullPath(f).StartsWith(fullOut + Path.DirectorySeparatorChar, StringComparison.Ordinal))];
        if (files.Count == 0) throw new ArgumentException($"no .cs files in {modDir}");

        // A script per namespace: the mod's files for Monsters.M209 go with <vanilla>/Monsters/M209.
        Dictionary<string, List<SyntaxTree>> byScript = new(StringComparer.Ordinal);
        foreach (string f in files) {
            SyntaxTree tree = ScriptCompiler.Parse(File.ReadAllText(f), f);
            string ns = tree.GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString()
                ?? throw new ArgumentException($"{f}: no namespace; a mod's file is in the namespace of the script it changes");
            if (!byScript.TryGetValue(ns, out List<SyntaxTree>? list)) byScript[ns] = list = [];
            list.Add(tree);
        }
        int count = 0;
        foreach ((string ns, List<SyntaxTree> modTrees) in byScript.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
            string folder = Path.Combine([vanilla, .. ns.Split('.')]);
            if (!Directory.Exists(folder)) throw new ArgumentException($"{ns}: no vanilla source in {folder}");
            List<SyntaxTree> vanillaTrees = ScriptCompiler.ParseFolder(folder);
            ScriptLibrary library = ScriptLibrary.Find(folder);
            if (check) {
                IReadOnlyList<Diagnostic> diagnostics = ScriptCompiler.Check([.. vanillaTrees, .. modTrees], library);
                foreach (Diagnostic d in diagnostics) Console.Error.WriteLine(d.ToString());
                if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) return 1;
            }
            CompiledMod mod = ScriptCompiler.CompileMod(vanillaTrees, modTrees, library);
            string path = GameData.Find(archive, mod.Category, mod.Name) ?? throw new ArgumentException($"the game has no {mod.Category} {mod.Name}");
            mod.Package.File = path;
            string target = Path.Combine([output, AtelMod.PackageDir, .. (path + ".json").Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, mod.Package.ToJson());
            ScriptPackage p = mod.Package;
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{mod.Name}: {p.Patches.Count} entries and patches, {p.Workers.Count} new workers, {p.Variables.Count} new variables -> {target}"));
            count++;
        }
        string toml = Path.Combine(modDir, AtelMod.ManifestFile);
        if (File.Exists(toml)) {
            ModManifest.Parse(File.ReadAllText(toml));    // fails early on a broken one
            File.Copy(toml, Path.Combine(output, AtelMod.ManifestFile), overwrite: true);
        }
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

    public static int Link(VbfArchive archive, List<string> args, string? output) {
        if (args.Count == 0 || output is null) throw new ArgumentException("link <mod dir>... -o <out dir>");
        List<LinkMessage> messages = [];
        List<AtelMod> mods = ModSet.Resolve([.. args.Select(d => AtelMod.Load(Path.GetFileName(Path.TrimEndingDirectorySeparator(d)), d))], messages);
        List<LinkedFile> files = ModSet.Link(mods, path => archive.Find(path) is { } e ? archive.Read(e) : null);
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
