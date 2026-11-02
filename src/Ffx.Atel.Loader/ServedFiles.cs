// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;

namespace Ffx.Atel.Loader;

/// <summary>
///     The files the loader serves (what it wrote into its <c>efl/x</c> folder) and their sizes, looked
///     up by the paths the PS2 file layer uses (<c>host0:/ffx/master/…</c>).
/// </summary>
/// <remarks>
///     The file layer sizes a file before reading it. On PC that is <c>CDfileSize_PC</c>, which
///     Fahrenheit makes read the size from disk; while an Eternal Calm map is the graphic map it is
///     <c>CDfileSize_CD</c>, which reads the CD's sector table. The loader answers that one itself for
///     its own files (docs/research/loader.md).
/// </remarks>
public sealed class ServedFiles {
    private readonly Dictionary<string, uint> _sizes;

    private ServedFiles(Dictionary<string, uint> sizes) => _sizes = sizes;

    /// <summary>No files.</summary>
    public static ServedFiles Empty { get; } = new(new Dictionary<string, uint>(StringComparer.Ordinal));

    /// <summary>How many files are served.</summary>
    public int Count => _sizes.Count;

    /// <summary>
    ///     Every file under <paramref name="eflDir"/> (the mod's <c>efl/x</c>), keyed by its archive path
    ///     (<c>ffx_ps2/…</c>, lower case), with its size rounded up to 16 bytes.
    /// </summary>
    public static ServedFiles Scan(string eflDir) {
        Dictionary<string, uint> sizes = new(StringComparer.Ordinal);
        if (!Directory.Exists(eflDir)) return new(sizes);
        foreach (string file in Directory.GetFiles(eflDir, "*", SearchOption.AllDirectories)) {
            string key  = Normalize(Path.GetRelativePath(eflDir, file));
            long   size = new FileInfo(file).Length;
            if (RoundUp(size) is { } rounded) sizes[key] = rounded;
        }
        return new(sizes);
    }

    /// <summary>
    ///     The size the game should use for <paramref name="gamePath"/> (<c>host0:/ffx/master/…</c> or
    ///     <c>cdrom0:/…</c>), or null when the loader does not serve that file.
    /// </summary>
    public uint? SizeOf(string? gamePath) =>
        _sizes.Count > 0 && ArchivePathOf(gamePath) is { } key && _sizes.TryGetValue(key, out uint size) ? size : null;

    /// <summary>Files per map in the event module: file <c>map * 18</c> is its <c>.ebp</c>, <c>map * 18 + 3</c> its <c>.mgrp</c>.</summary>
    public const int FilesPerMap = 18;

    /// <summary>
    ///     The path <c>Cd_QueueRead</c> reads PS2 file <paramref name="number"/> from: the file table's name
    ///     (<c>CDfileName_PC</c>), or, when the table has none, the built-in name of an Eternal Calm map's
    ///     <c>.ebp</c> (file <c>map * 18</c>) or <c>.mgrp</c> (<c>map * 18 + 3</c>). The table entries of
    ///     maps 393-399 are empty, so their own files only have the built-in names.
    /// </summary>
    /// <param name="number">The file number within the current module.</param>
    /// <param name="fileName">The table's name, or null (<c>CDfileName_PC</c>).</param>
    /// <param name="sceneName">An Eternal Calm map's <c>.ebp</c>, or null for other maps (<c>getEternalCalmSceneName</c>).</param>
    /// <param name="mgrpName">An Eternal Calm map's <c>.mgrp</c>, or null for other maps.</param>
    public static string? GamePathOf(int number, Func<int, string?> fileName, Func<int, string?> sceneName, Func<int, string?> mgrpName) =>
        fileName(number) ?? (number % FilesPerMap) switch {
            0 => sceneName(number / FilesPerMap),
            3 => mgrpName(number / FilesPerMap),
            _ => null,
        };

    /// <summary>
    ///     The archive path (<c>ffx_ps2/…</c>, lower case, forward slashes) of a path of the PS2 file
    ///     layer: <c>host0:/ffx/master/jppc/…</c> is <c>ffx_ps2/ffx/master/jppc/…</c>. Null for a path
    ///     on another device.
    /// </summary>
    public static string? ArchivePathOf(string? gamePath) {
        if (gamePath is null) return null;
        string path = Normalize(gamePath);
        string? rest = path.StartsWith("host0:", StringComparison.Ordinal) ? path["host0:".Length..]
                     : path.StartsWith("cdrom0:", StringComparison.Ordinal) ? path["cdrom0:".Length..]
                     : null;
        if (rest is null) return null;
        rest = rest.TrimStart('/');
        return rest.Length == 0 ? null : "ffx_ps2/" + rest;
    }

    /// <summary>
    ///     A size rounded up to 16 bytes, as <c>CDfileSize_PC</c> rounds what <c>sceFileSize</c> returns.
    ///     Null when it does not fit the game's 32-bit sizes.
    /// </summary>
    public static uint? RoundUp(long size) {
        if (size < 0) return null;
        long rounded = (size + 15) & ~15L;
        return rounded <= int.MaxValue ? (uint)rounded : null;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();
}
