// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

using Fahrenheit;

using Hexa.NET.ImGui;

namespace Ffx.Atel.Cuts;

/// <summary>
///     The in-game switch for named dead blocks (<c>Cut.*</c>): a menu (F8) with a checkbox per name, which patches
///     the scripts the game has loaded, independent of the loader (<c>yuta.atel</c>; docs/research/dead-code.md,
///     "Switching a block on in game").
/// </summary>
/// <remarks>
///     <para>
///         Every frame, before the game's update, <see cref="CutTracker"/> looks at the blocks the ctrl works run,
///         finds the named blocks in the scripts that have them (by signature, as the linker does) and sets each
///         block's <c>jmp</c> byte (<c>b0</c> off, <c>f6</c> on) the way the player set the name. It works on the
///         block in memory, so it applies to whatever file the game read: the vanilla one, or one <c>yuta.atel</c>
///         linked or rebuilt.
///     </para>
///     <para>
///         The switches are saved in the module's state file whenever one changes, and applied again to every script
///         the game loads.
///     </para>
/// </remarks>
[FhLoad(FhGameId.FFX)]
public sealed class CutsModule : FhModule {
    private readonly CutCatalog? _catalog;
    private readonly string? _failure;
    private CutStates _states = new();
    private CutTracker? _tracker;
    private readonly GameMemory _memory = new();
    private FileStream? _stateFile;
    private readonly object _saveLock = new();
    private bool _open;
    private bool _tickFailed;

    public CutsModule() {
        try {
            _catalog = CutCatalog.Embedded();
        }
        catch (Exception ex) {
            _failure = ex.ToString();
        }
    }

    public override bool init(FhModContext mod_context, FileStream global_state_file) {
        if (_catalog is null) {
            _logger.Error($"Atel cuts: no names: {_failure}");
            return true;
        }
        _stateFile = global_state_file;
        try {
            List<string> dropped = [];
            using StreamReader reader = new(global_state_file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            _states = CutStates.FromJson(reader.ReadToEnd(), _catalog.Cuts.Select(c => c.Name).ToHashSet(StringComparer.Ordinal), dropped);
            foreach (string name in dropped) _logger.Warning($"Atel cuts: the state file names Cut.{name}, which this version does not have; dropped");
        }
        catch (Exception ex) when (ex is JsonException or IOException) {
            _logger.Error($"Atel cuts: cannot read the state file ({ex.Message}); every name starts as loaded");
        }
        _tracker = new CutTracker(_catalog, _states, m => _logger.Info(m), m => _logger.Error(m));
        FhApi.Events.Common.GameLoop.PreUpdate.subscribe(_ => Tick());
        string set = _states.All.Count == 0 ? "none set" : string.Join(", ", _states.All.Select(kv => $"{kv.Key} {(kv.Value ? "on" : "off")}"));
        _logger.Info($"Atel cuts: {_catalog.Cuts.Count} name(s) in {string.Join(", ", _catalog.Scripts.Order(StringComparer.Ordinal))}; {set}. F8 opens the menu.");
        return true;
    }

    /// <summary>Game thread, before each update.</summary>
    private void Tick() {
        if (_tracker is null || _tickFailed) return;
        try {
            _tracker.Tick(_memory);
        }
        catch (Exception ex) {
            // Never unwind into the game; stop switching instead.
            _tickFailed = true;
            _logger.Error($"Atel cuts: stopped: {ex}");
        }
    }

    private void Save() {
        if (_stateFile is null) return;
        try {
            lock (_saveLock) {
                byte[] json = Encoding.UTF8.GetBytes(_states.ToJson());
                _stateFile.SetLength(0);
                _stateFile.Position = 0;
                _stateFile.Write(json);
                _stateFile.Flush(flushToDisk: true);
            }
        }
        catch (IOException ex) {
            _logger.Error($"Atel cuts: cannot save the switches: {ex.Message}");
        }
    }

    private static readonly Vector4 Grey = new(0.7f, 0.7f, 0.7f, 1f);
    private static readonly Vector4 Red = new(1f, 0.45f, 0.4f, 1f);
    private static readonly Vector4 Yellow = new(1f, 0.85f, 0.3f, 1f);

    /// <summary>The menu. Render thread: it only reads the tracker's snapshot and sets switches.</summary>
    public override void render_imgui() {
        if (ImGui.IsKeyPressed(ImGuiKey.F8, false)) _open = !_open;
        if (!_open || _catalog is null) return;
        ImGui.SetNextWindowSize(new Vector2(620, 0), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Atel cuts###yuta.atel.cuts", ref _open)) {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(Yellow, "Switched-on blocks run code the game never ran. What they do can stay: story flags, "
                + "bits in the save (seen battle lines), items. Save to another slot before trying one.");
            ImGui.PopTextWrapPos();
            ImGui.Separator();
            CutStatus status = _tracker?.Status ?? CutStatus.Empty;
            foreach (CutInfo cut in _catalog.Cuts) Row(cut, status);
            ImGui.Separator();
            ImGui.TextColored(Grey, $"Scripts scanned: {_tracker?.Scans ?? 0}. F8 hides this window.");
        }
        ImGui.End();
    }

    private void Row(CutInfo cut, CutStatus status) {
        ImGui.PushID(cut.Name);
        bool? set = _states.Get(cut.Name);
        status.Loaded.TryGetValue(cut.Name, out IReadOnlyList<LoadedCut>? loaded);
        // Unset: the box shows what is loaded (on only when a linked mod switched every block on).
        bool on = set ?? (loaded is { Count: > 0 } && loaded.All(l => l.Blocks > 0 && l.LoadedOn == l.Blocks));
        if (ImGui.Checkbox(cut.Name, ref on)) {
            _states.Set(cut.Name, on);
            Save();
        }
        if (set is not null) {
            ImGui.SameLine();
            if (ImGui.SmallButton("as loaded")) {
                _states.Set(cut.Name, null);
                Save();
            }
        }
        ImGui.Indent();
        ImGui.PushTextWrapPos(0f);
        if (loaded is not { Count: > 0 }) {
            ImGui.TextColored(Grey, $"Not loaded ({string.Join(", ", cut.Scripts)}).{(set is null ? "" : $" Switched {(set.Value ? "on" : "off")} when it loads.")}");
        }
        else {
            foreach (LoadedCut l in loaded) {
                string mod = l.LoadedOn > 0 ? $"; {l.LoadedOn} on as loaded (a linked mod)" : "";
                ImGui.Text($"Loaded: {l.Script} (ctrl work {l.Ctrl}), {l.On} of {l.Blocks} block(s) on{mod}");
                if (l.Problem is not null) ImGui.TextColored(Red, l.Problem);
            }
        }
        if (cut.Summary.Length > 0) ImGui.TextColored(Grey, cut.Summary);
        ImGui.PopTextWrapPos();
        ImGui.Unindent();
        ImGui.Spacing();
        ImGui.PopID();
    }
}
