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
///     Fahrenheit's own <c>CDfileSize_PC</c> hook makes the game size them from disk, so scripts
///     that grow need nothing more (docs/research/loader.md).
/// </remarks>
[FhLoad(FhGameId.FFX)]
public sealed class AtelLoaderModule : FhModule {
    private readonly List<BuiltScript> _built = [];
    private readonly string?           _failure;

    public AtelLoaderModule() {
        try {
            string modDir  = Path.GetDirectoryName(typeof(AtelLoaderModule).Assembly.Location)
                ?? throw new IOException("cannot tell where the mod is");
            string gameDir = Path.GetDirectoryName(Environment.ProcessPath)
                ?? throw new IOException("cannot tell where the game is");
            _built.AddRange(ScriptBuilder.Build(modDir, gameDir, Path.Combine(modDir, "efl", "x")));
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
        return true;
    }
}
