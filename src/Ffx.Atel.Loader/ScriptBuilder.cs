// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;

using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Loader;

/// <summary>One file the builder wrote, or could not.</summary>
/// <param name="Source">The <c>.asm</c> file it came from, relative to <c>scripts/</c>.</param>
/// <param name="ArchivePath">The archive path it replaces (<c>ffx_ps2/…</c>); null when the source names none.</param>
/// <param name="VanillaSize">The vanilla file's size in bytes.</param>
/// <param name="RebuiltSize">The rebuilt file's size in bytes.</param>
/// <param name="ScriptRoom">Bytes the vanilla script section had, up to the next section.</param>
/// <param name="ScriptSize">Bytes of the rebuilt script block.</param>
/// <param name="Error">Why it was skipped; null when it was written.</param>
public sealed record BuiltScript(
    string Source,
    string? ArchivePath,
    long VanillaSize,
    long RebuiltSize,
    int ScriptRoom,
    int ScriptSize,
    string? Error) {
    /// <summary>The script outgrew its section, so the later sections moved and the file grew.</summary>
    public bool Grew => RebuiltSize > VanillaSize;
}

/// <summary>
///     Phase 2's stand-in for the link step: rebuilds scripts from edited disassembly onto the
///     player's own vanilla files and writes them where Fahrenheit's file loader serves them.
/// </summary>
/// <remarks>
///     Every <c>*.asm</c> file in <c>scripts/</c> (the format of <c>atel disasm</c>, whose first line
///     is <c>; &lt;archive path&gt;</c>) is assembled onto the vanilla file of that path, read from
///     <c>data/FFX_Data.vbf</c>, and the whole file is written to <c>efl/x/&lt;archive path&gt;</c>.
///     The builder owns <c>efl/x</c>: it empties it first, so a removed source stops being served.
///     Sources are taken in path order; a second source for a file already rebuilt is skipped.
/// </remarks>
public static class ScriptBuilder {
    /// <summary>The folder of a mod that holds the edited disassembly.</summary>
    public const string SourceDir = "scripts";

    /// <summary>
    ///     Rebuilds every source under <paramref name="modDir"/> onto the vanilla files of
    ///     <paramref name="gameDir"/>, into <paramref name="eflDir"/> (the mod's <c>efl/x</c>).
    /// </summary>
    public static List<BuiltScript> Build(string modDir, string gameDir, string eflDir) {
        if (Directory.Exists(eflDir)) Directory.Delete(eflDir, recursive: true);
        Directory.CreateDirectory(eflDir);
        string sources = Path.Combine(modDir, SourceDir);
        List<BuiltScript> results = [];
        if (!Directory.Exists(sources)) return results;
        string[] files = Directory.GetFiles(sources, "*.asm", SearchOption.AllDirectories);
        if (files.Length == 0) return results;
        Array.Sort(files, StringComparer.Ordinal);
        using VbfArchive archive = GameData.OpenArchive(gameDir);
        Dictionary<string, string> built = new(StringComparer.Ordinal);
        foreach (string file in files) {
            string name = Path.GetRelativePath(sources, file).Replace(Path.DirectorySeparatorChar, '/');
            results.Add(BuildOne(archive, file, name, eflDir, built));
        }
        return results;
    }

    private static BuiltScript BuildOne(VbfArchive archive, string source, string name, string eflDir, Dictionary<string, string> built) {
        string text;
        try {
            text = File.ReadAllText(source);
        }
        catch (IOException ex) {
            return new(name, null, 0, 0, 0, 0, ex.Message);
        }
        string? path = ArchivePathOf(text);
        if (path is null) return new(name, null, 0, 0, 0, 0, "the first line must be '; <archive path>', as atel disasm writes it");
        if (archive.Find(path) is not { } entry) return new(name, path, 0, 0, 0, 0, "not in FFX_Data.vbf");
        if (built.TryGetValue(entry.Path, out string? first)) return new(name, entry.Path, 0, 0, 0, 0, $"{first} already rebuilds this file");
        try {
            ScriptFile vanilla = ScriptFile.Parse(archive.Read(entry));
            ScriptBlock rebuilt = Assembly.Assemble(vanilla.Script, text);
            ScriptFile file = vanilla.ReplaceScript(rebuilt);
            string target = Path.Combine(eflDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file.ToArray());
            built[entry.Path] = name;
            return new(name, entry.Path, vanilla.Bytes.Length, file.Bytes.Length, vanilla.SectionLength, rebuilt.Bytes.Length, null);
        }
        catch (Exception ex) when (ex is ScriptFormatException or AssemblyException or IOException or InvalidDataException) {
            return new(name, entry.Path, (long)entry.Size, 0, 0, 0, ex.Message);
        }
    }

    /// <summary>The archive path in the first line of a disassembly (<c>; ffx_ps2/…</c>), or null.</summary>
    public static string? ArchivePathOf(string text) {
        int end = text.IndexOf('\n');
        ReadOnlySpan<char> first = (end < 0 ? text : text[..end]).AsSpan().Trim();
        if (!first.StartsWith(";")) return null;
        string path = first[1..].Trim().ToString();
        return path.Length > 0 && (path.Contains('/') || path.Contains('\\')) ? VbfArchive.NormalizePath(path) : null;
    }
}
