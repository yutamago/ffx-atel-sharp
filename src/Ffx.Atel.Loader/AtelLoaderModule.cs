// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;

using Fahrenheit;

namespace Ffx.Atel.Loader;

/// <summary>
///     The Atel loader: gives the game rebuilt scripts in place of the vanilla ones. Phase 2
///     rebuilds edited disassembly (<c>scripts/*.asm</c>) onto the player's own files; the link
///     step for compiled mod packages comes later (docs/plan.md).
/// </summary>
/// <remarks>
///     The files are written in the constructor, into this mod's <c>efl/x</c> folder: Fahrenheit
///     runs every mod's constructors before any <c>init</c>, and its file loader indexes the
///     <c>efl</c> folders in its <c>init</c>, so it serves them like any other replacement file.
///     Fahrenheit's own <c>CDfileSize_PC</c> hook makes the game size them from disk; the loader
///     hooks <c>CDfileSize_CD</c>, which Fahrenheit leaves alone and which the game uses while an
///     Eternal Calm map is loaded, so a script that grew is read whole there too
///     (<see cref="CdFileSizeHook"/>, docs/research/loader.md).
/// </remarks>
[FhLoad(FhGameId.FFX)]
public sealed class AtelLoaderModule : FhModule {
    private readonly List<BuiltScript> _built = [];
    private readonly ServedFiles       _served = ServedFiles.Empty;
    private readonly string?           _failure;

    public AtelLoaderModule() {
        try {
            string modDir  = Path.GetDirectoryName(typeof(AtelLoaderModule).Assembly.Location)
                ?? throw new IOException("cannot tell where the mod is");
            string gameDir = Path.GetDirectoryName(Environment.ProcessPath)
                ?? throw new IOException("cannot tell where the game is");
            string eflDir  = Path.Combine(modDir, "efl", "x");
            _built.AddRange(ScriptBuilder.Build(modDir, gameDir, eflDir));
            _served = ServedFiles.Scan(eflDir);
        }
        catch (Exception ex) {
            // A failed build must not stop the game: it then runs with the vanilla scripts.
            _failure = ex.ToString();
        }
    }

    public override bool init(FhModContext mod_context, FileStream global_state_file) {
        if (_failure is not null) {
            _logger.Error($"Atel loader: no scripts built: {_failure}");
            return true;
        }
        if (_built.Count == 0) {
            _logger.Info($"Atel loader: nothing in {ScriptBuilder.SourceDir}/, the game runs its own scripts.");
        }
        foreach (BuiltScript s in _built) {
            if (s.Error is not null) {
                _logger.Error($"Atel loader: {s.Source}: skipped ({s.ArchivePath ?? "no path"}): {s.Error}");
                continue;
            }
            string growth = s.Grew
                ? $"script {s.ScriptSize} bytes in a section of {s.ScriptRoom}, file grew {s.VanillaSize} -> {s.RebuiltSize}"
                : $"script {s.ScriptSize} bytes in a section of {s.ScriptRoom}, file {s.RebuiltSize} bytes";
            _logger.Info($"Atel loader: {s.Source} -> {s.ArchivePath} ({growth})");
        }
        // Only with files to serve: without them the game's own sizes are right.
        if (_served.Count > 0) {
            if (new CdFileSizeHook(_served, _logger).Install(this)) {
                _logger.Info($"Atel loader: CDfileSize_CD hooked for {_served.Count} served file(s).");
            }
            else {
                _logger.Error("Atel loader: cannot hook CDfileSize_CD; a grown file read on an Eternal Calm map (393-399) would be cut.");
            }
        }
        return true;
    }
}
