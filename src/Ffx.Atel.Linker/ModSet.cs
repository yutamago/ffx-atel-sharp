// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace Ffx.Atel.Linker;

/// <summary>
///     An Atel mod on disk: a folder with <c>mod.toml</c> and its packages under <c>atel/</c>, one
///     <c>&lt;archive path&gt;.json</c> per script (docs/formats/mod-package.md).
/// </summary>
public sealed class AtelMod {
    /// <summary>The folder of packages inside a mod.</summary>
    public const string PackageDir = "atel";

    public const string ManifestFile = "mod.toml";

    public required string Id { get; init; }
    public ModManifest Manifest { get; init; } = ModManifest.Empty;

    /// <summary>Packages by archive path (lower case).</summary>
    public Dictionary<string, ScriptPackage> Packages { get; } = new(StringComparer.Ordinal);

    /// <summary>What could not be read.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Whether the folder holds an Atel mod (it has an <c>atel/</c> folder).</summary>
    public static bool IsAtelMod(string dir) => Directory.Exists(Path.Combine(dir, PackageDir));

    /// <summary>Reads a mod's manifest and packages; problems go to <see cref="Errors"/>.</summary>
    public static AtelMod Load(string id, string dir) {
        string manifestPath = Path.Combine(dir, ManifestFile);
        ModManifest manifest = ModManifest.Empty;
        List<string> errors = [];
        if (File.Exists(manifestPath)) {
            try {
                manifest = ModManifest.Parse(File.ReadAllText(manifestPath));
            }
            catch (Exception ex) when (ex is FormatException or IOException) {
                errors.Add($"{ManifestFile}: {ex.Message}");
            }
        }
        AtelMod mod = new() { Id = id, Manifest = manifest };
        mod.Errors.AddRange(errors);
        mod.AddPackages(Path.Combine(dir, PackageDir));
        return mod;
    }

    /// <summary>
    ///     A mod made of the packages in a folder (every <c>*.json</c> under it), without a manifest: the
    ///     loader's <c>inject/</c> folder (docs/formats/debug.md).
    /// </summary>
    public static AtelMod FromFolder(string id, string packages) {
        AtelMod mod = new() { Id = id };
        mod.AddPackages(packages);
        return mod;
    }

    private void AddPackages(string packages) {
        AtelMod mod = this;
        if (!Directory.Exists(packages)) return;
        string[] files = Directory.GetFiles(packages, "*.json", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        foreach (string f in files) {
            string rel = Path.GetRelativePath(packages, f).Replace('\\', '/');
            try {
                ScriptPackage p = ScriptPackage.FromJson(File.ReadAllText(f));
                string path = p.File.Replace('\\', '/').ToLowerInvariant();
                if (path.Length == 0) { mod.Errors.Add($"{rel}: names no file"); continue; }
                if (!mod.Packages.TryAdd(path, p)) mod.Errors.Add($"{rel}: a second package for {path}");
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) {
                mod.Errors.Add($"{rel}: {ex.Message}");
            }
        }
    }

    /// <summary>The texts of every package's <c>Debug.Print</c> calls.</summary>
    public IEnumerable<string> DebugStrings => Packages.Values.SelectMany(p => p.DebugStrings ?? []);
}

/// <summary>One linked file of a <see cref="ModSet"/>.</summary>
/// <param name="Path">The archive path (<c>ffx_ps2/…</c>).</param>
/// <param name="Result">The linked file and its report; null when the vanilla file could not be read.</param>
public sealed record LinkedFile(string Path, LinkResult? Result, string? Error);

/// <summary>The enabled Atel mods in load order, linked file by file.</summary>
public static class ModSet {
    /// <summary>
    ///     Checks the mods' manifests against each other (<c>requires</c>, <c>incompatible</c>,
    ///     <c>after</c>) and returns the ones that link, in load order, with what was left out and why.
    /// </summary>
    public static List<AtelMod> Resolve(IReadOnlyList<AtelMod> loadOrder, List<LinkMessage> messages) {
        List<AtelMod> kept = [];
        HashSet<string> enabled = [.. loadOrder.Select(m => m.Id)];
        foreach (AtelMod mod in loadOrder) {
            foreach (string e in mod.Errors) messages.Add(new(LinkSeverity.Error, mod.Id, e));
            if (mod.Manifest.Requires.FirstOrDefault(r => !enabled.Contains(r)) is string missing) {
                messages.Add(new(LinkSeverity.Error, mod.Id, $"requires {missing}, which is not enabled; not linked"));
                enabled.Remove(mod.Id);
                continue;
            }
            if (kept.FirstOrDefault(k => mod.Manifest.Incompatible.Contains(k.Id) || k.Manifest.Incompatible.Contains(mod.Id)) is AtelMod clash) {
                messages.Add(new(LinkSeverity.Conflict, mod.Id, $"is incompatible with {clash.Id}, which loads first; not linked"));
                enabled.Remove(mod.Id);
                continue;
            }
            foreach (string after in mod.Manifest.After) {
                if (loadOrder.Any(m => m.Id == after) && !kept.Any(k => k.Id == after)) {
                    messages.Add(new(LinkSeverity.Warning, mod.Id, $"wants to load after {after}; fix the load order"));
                }
            }
            kept.Add(mod);
        }
        return kept;
    }

    /// <summary>Links every file any mod has a package for, onto the vanilla file <paramref name="readVanilla"/> gives.</summary>
    public static List<LinkedFile> Link(IReadOnlyList<AtelMod> mods, Func<string, byte[]?> readVanilla) {
        List<LinkedFile> results = [];
        IEnumerable<string> paths = mods.SelectMany(m => m.Packages.Keys).Distinct().Order(StringComparer.Ordinal);
        foreach (string path in paths) {
            byte[]? vanilla;
            try {
                vanilla = readVanilla(path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) {
                results.Add(new(path, null, ex.Message));
                continue;
            }
            if (vanilla is null) {
                results.Add(new(path, null, "not in the game's data"));
                continue;
            }
            List<ModScript> scripts = [.. mods.Where(m => m.Packages.ContainsKey(path)).Select(m => new ModScript(m.Id, m.Packages[path]))];
            try {
                results.Add(new(path, ScriptLinker.Link(vanilla, scripts), null));
            }
            catch (Bytecode.Script.ScriptFormatException ex) {
                results.Add(new(path, null, $"not a script file: {ex.Message}"));
            }
        }
        return results;
    }
}
