// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Linker;

namespace Ffx.Atel.Loader;

/// <summary>What linking the enabled Atel mods did.</summary>
/// <param name="Mods">The Atel mods found, in load order.</param>
/// <param name="Written">The archive paths of the files written to <c>efl/x</c>.</param>
/// <param name="Messages">The link report: per mod, what was applied, conflicts and errors.</param>
public sealed record LinkRun(List<string> Mods, List<string> Written, List<LinkMessage> Messages);

/// <summary>
///     Links the enabled Atel mods onto the player's files and writes the results to the loader's
///     <c>efl/x</c> (docs/formats/mod-package.md).
/// </summary>
/// <remarks>
///     The mods and their order are Fahrenheit's: the lines of <c>mods/loadorder</c>, each a folder
///     <c>mods/&lt;id&gt;/</c>; a mod is an Atel mod when it has an <c>atel/</c> folder. The loader reads
///     the file itself because it links in its constructor, before Fahrenheit's mod list is published.
/// </remarks>
public static class ModLoader {
    /// <summary>Fahrenheit's load order file in its <c>mods</c> folder.</summary>
    public const string LoadOrderFile = "loadorder";

    /// <summary>The ids in Fahrenheit's load order (blank lines and surrounding space ignored).</summary>
    public static List<string> LoadOrder(string modsDir) {
        string path = Path.Combine(modsDir, LoadOrderFile);
        if (!File.Exists(path)) return [];
        return [.. File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0)];
    }

    /// <summary>
    ///     Links the Atel mods of <paramref name="modsDir"/> in load order and writes every changed file to
    ///     <paramref name="eflDir"/>, except those in <paramref name="taken"/> (files the loader's own
    ///     <c>scripts/</c> already rebuilt).
    /// </summary>
    /// <param name="readVanilla">The vanilla file of an archive path, or null when the game has none.</param>
    public static LinkRun Link(string modsDir, string eflDir, Func<string, byte[]?> readVanilla, IReadOnlySet<string> taken) {
        List<LinkMessage> messages = [];
        List<AtelMod> found = [];
        foreach (string id in LoadOrder(modsDir)) {
            string dir = Path.Combine(modsDir, id);
            if (AtelMod.IsAtelMod(dir)) found.Add(AtelMod.Load(id, dir));
        }
        List<AtelMod> mods = ModSet.Resolve(found, messages);
        List<string> written = [];
        if (mods.Count > 0 && mods.Any(m => m.Packages.Count > 0)) {
            foreach (LinkedFile f in ModSet.Link(mods, readVanilla)) {
                if (f.Result is null) {
                    messages.Add(new(LinkSeverity.Error, "", $"{f.Path}: {f.Error}"));
                    continue;
                }
                messages.AddRange(f.Result.Messages);
                if (!f.Result.Changed) continue;
                if (taken.Contains(f.Path)) {
                    messages.Add(new(LinkSeverity.Conflict, string.Join(", ", f.Result.Applied),
                        $"{f.Path}: the loader's own {ScriptBuilder.SourceDir}/ already rebuilds this file; the mods are not linked onto it"));
                    continue;
                }
                string target = Path.Combine(eflDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, f.Result.File);
                written.Add(f.Path);
            }
        }
        return new LinkRun([.. found.Select(m => m.Id)], written, messages);
    }

    /// <summary><see cref="Link(string, string, Func{string, byte[]}, IReadOnlySet{string})"/> with the vanilla files from the game's <c>data/FFX_Data.vbf</c>, opened only when a mod has a package.</summary>
    public static LinkRun Link(string modsDir, string gameDir, string eflDir, IReadOnlySet<string> taken) {
        VbfArchive? archive = null;
        try {
            return Link(modsDir, eflDir, path => {
                archive ??= GameData.OpenArchive(gameDir);
                return archive.Find(path) is { } entry ? archive.Read(entry) : null;
            }, taken);
        }
        finally {
            archive?.Dispose();
        }
    }
}
