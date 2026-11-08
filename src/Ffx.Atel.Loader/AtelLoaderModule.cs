// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Fahrenheit;

using Ffx.Atel.Linker;

namespace Ffx.Atel.Loader;

/// <summary>
///     The Atel loader: links the enabled Atel mods onto the player's own scripts and gives the game
///     the results in place of the vanilla ones (<see cref="ModLoader"/>, docs/formats/mod-package.md).
///     For development it also rebuilds edited disassembly in its own folder (<c>scripts/*.asm</c>,
///     Phase 2's path), which takes precedence over mods for the same file.
/// </summary>
/// <remarks>
///     The files are written in the constructor, into this mod's <c>efl/x</c> folder: Fahrenheit
///     runs every mod's constructors before any <c>init</c>, and its file loader indexes the
///     <c>efl</c> folders in its <c>init</c>, so it serves them like any other replacement file.
///     Fahrenheit's own <c>CDfileSize_PC</c> hook makes the game size them from disk; the loader
///     hooks <c>CDfileSize_CD</c>, which Fahrenheit leaves alone and which the game uses while an
///     Eternal Calm map is loaded, so a script that grew is read whole there too
///     (<see cref="CdFileSizeHook"/>, docs/research/loader.md).
///     It also adds the debug natives (<see cref="DebugNatives"/>), watches its <c>inject/</c> folder for
///     runtime injection packages (<see cref="RuntimeInjector"/>, docs/formats/debug.md), and keeps the mods'
///     <c>[Saved]</c> fields with each save in its Fahrenheit local state (<see cref="SaveHooks"/>, docs/research/mod-saves.md).
/// </remarks>
[FhLoad(FhGameId.FFX)]
public sealed class AtelLoaderModule : FhModule {
    private readonly List<BuiltScript> _built = [];
    private readonly LinkRun?          _linked;
    private readonly ServedFiles       _served = ServedFiles.Empty;
    private readonly string?           _failure;
    private readonly string            _modDir = "", _gameDir = "", _eflDir = "";
    private readonly HashSet<string>   _taken = [];
    private readonly AtelMod?          _injected;
    private RuntimeInjector?           _injector;
    private SaveHooks?                 _saves;

    public AtelLoaderModule() {
        try {
            string modDir  = Path.GetDirectoryName(typeof(AtelLoaderModule).Assembly.Location)
                ?? throw new IOException("cannot tell where the mod is");
            string gameDir = Path.GetDirectoryName(Environment.ProcessPath)
                ?? throw new IOException("cannot tell where the game is");
            string eflDir  = Path.Combine(modDir, "efl", "x");
            (_modDir, _gameDir, _eflDir) = (modDir, gameDir, eflDir);
            _built.AddRange(ScriptBuilder.Build(modDir, gameDir, eflDir));
            HashSet<string> taken = [.. _built.Where(b => b.Error is null && b.ArchivePath is not null).Select(b => b.ArchivePath!)];
            _taken = taken;
            string modsDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(modDir)) ?? throw new IOException("cannot tell where the mods are");
            // The packages in inject/ link after every mod, so the files they touch are served (and can be replaced at run time).
            _injected = Injection.Read(modDir);
            _linked = ModLoader.Link(modsDir, gameDir, eflDir, taken, _injected);
            _served = ServedFiles.Scan(eflDir);
        }
        catch (Exception ex) {
            // A failed build must not stop the game: it then runs with the vanilla scripts.
            _failure = ex.ToString();
        }
    }

    /// <summary>After every save and autosave: the mods' saved fields, by key (<see cref="SaveHooks"/>).</summary>
    public override void save_local_state(FileStream local_state_file) => _saves?.Save(local_state_file);

    /// <summary>On a load, when the slot has a state: the mods' saved fields back into the save's mod area.</summary>
    public override void load_local_state(FileStream local_state_file, FhLocalStateInfo local_state_info) => _saves?.Load(local_state_file);

    public override bool init(FhModContext mod_context, FileStream global_state_file) {
        // The debug natives do not depend on the link: a script with Debug.Print may come from anywhere.
        if (!DebugNatives.Install(this, _logger)) _logger.Error("Atel loader: cannot hook AtelInitTotal; Debug.* calls would stop their worker.");
        // The saved fields' hooks do not depend on the link either: without mods they keep the state of absent mods and
        // still zero the save's tail in the file the game writes.
        _saves = new SaveHooks(new SavedState(_linked?.Saved ?? SavedLayout.Empty), _logger);
        _saves.Install(this);
        if (_failure is not null) {
            _logger.Error($"Atel loader: no scripts built: {_failure}");
            return true;
        }
        if (_linked is not null) {
            foreach (AtelMod m in _linked.Linked) {
                foreach ((string text, string kept) in DebugNatives.Strings.Add(m.DebugStrings)) {
                    _logger.Warning($"Atel loader: {m.Id}: debug text \"{text}\" has the id of \"{kept}\"; Debug.Print shows the latter");
                }
            }
            try {
                _injector = new RuntimeInjector(_modDir, _gameDir, _eflDir, _linked.Linked, new HashSet<string>(_linked.Written, StringComparer.Ordinal), _taken, _logger, _linked.Saved);
                _injector.Start(_injected ?? Injection.Read(_modDir));
                FhApi.Events.Common.GameLoop.PreUpdate.subscribe(_ => _injector.Update());
            }
            catch (Exception ex) {
                _logger.Error($"Atel loader: runtime injection is off: {ex}");
            }
        }
        if (_linked is not null) {
            _logger.Info(_linked.Mods.Count == 0
                ? "Atel loader: no Atel mods in the load order."
                : $"Atel loader: Atel mods in load order: {string.Join(", ", _linked.Mods)}; {_linked.Written.Count} file(s) linked.");
            foreach (LinkMessage m in _linked.Messages) {
                string line = $"Atel loader: {m}";
                if (m.Severity is LinkSeverity.Error) _logger.Error(line);
                else if (m.Severity is LinkSeverity.Conflict or LinkSeverity.Warning) _logger.Warning(line);
                else _logger.Info(line);
            }
            foreach (string path in _linked.Written) _logger.Info($"Atel loader: serving linked {path}");
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
