// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

using Fahrenheit;

using Hexa.NET.ImGui;

namespace Ffx.Atel.Loader;

/// <summary>
///     The on-screen lines of <c>Debug.*</c> (docs/formats/debug.md, "On screen"): the last few, top left, each with
///     its time and worker, fading out. F6 shows or hides them, Shift+F6 changes how many.
/// </summary>
/// <remarks>
///     It draws on ImGui's foreground draw list, not in a window: there is nothing to focus, hover or click, so the
///     game keeps every key and the mouse, in battle, on the field and in menus alike. The keys are read with
///     <c>ImGui.IsKeyPressed</c>, which only looks.
/// </remarks>
internal sealed class DebugOverlayView {
    public const ImGuiKey Key = ImGuiKey.F6;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly DebugLines _lines;
    private readonly FhLogger _log;
    private readonly object _saveLock = new();
    private FileStream? _stateFile;
    private volatile DebugOverlaySettings _settings = new();

    public DebugOverlayView(DebugLines lines, FhLogger log) {
        _lines = lines;
        _log = log;
    }

    /// <summary>The time lines are stamped and faded by.</summary>
    public static TimeSpan Now => Clock.Elapsed;

    public DebugOverlaySettings Settings => _settings;

    /// <summary>Reads the settings from the loader's global state file, and keeps the file to save them to.</summary>
    public void Load(FileStream stateFile) {
        _stateFile = stateFile;
        try {
            using StreamReader reader = new(stateFile, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            _settings = DebugOverlaySettings.FromJson(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is JsonException or IOException) {
            _log.Error($"Atel loader: cannot read the state file ({ex.Message}); the debug overlay starts with its defaults");
        }
        _log.Info($"Atel loader: debug overlay {(_settings.Enabled ? "on" : "off")} ({_settings.Lines} line(s), {_settings.Seconds:0.#} s); F6 shows or hides it, Shift+F6 changes the line count.");
    }

    private void Save() {
        if (_stateFile is null) return;
        try {
            lock (_saveLock) {
                byte[] json = Encoding.UTF8.GetBytes(_settings.ToJson());
                _stateFile.SetLength(0);
                _stateFile.Position = 0;
                _stateFile.Write(json);
                _stateFile.Flush(flushToDisk: true);
            }
        }
        catch (IOException ex) {
            _log.Error($"Atel loader: cannot save the debug overlay's settings: {ex.Message}");
        }
    }

    private void Note(string text) => _lines.Add(new DebugLine(Now, DateTime.Now, null, text));

    private bool _failed;

    /// <summary>Render thread, every frame. A failure turns the overlay off for the session rather than reach the game.</summary>
    public void Render() {
        if (_failed) return;
        try {
            Draw();
        }
        catch (Exception ex) {
            _failed = true;
            _log.Error($"Atel loader: debug overlay stopped: {ex}");
        }
    }

    private void Draw() {
        if (ImGui.IsKeyPressed(Key, false)) {
            if (ImGui.IsKeyDown(ImGuiKey.ModShift)) {
                _settings = _settings with { Lines = _settings.NextLines(), Enabled = true };
                Note($"Atel debug overlay: {_settings.Lines} lines (Shift+F6 changes, F6 hides)");
            }
            else {
                _settings = _settings with { Enabled = !_settings.Enabled };
                if (_settings.Enabled) Note("Atel debug overlay on (F6 hides, Shift+F6 changes the line count)");
            }
            _log.Info($"Atel loader: debug overlay {(_settings.Enabled ? "on" : "off")}, {_settings.Lines} line(s), {_settings.Seconds:0.#} s");
            Save();
        }
        if (!_settings.Enabled) return;
        List<VisibleLine> shown = _lines.Visible(Now, _settings);
        if (shown.Count == 0) return;

        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImDrawListPtr draw = ImGui.GetForegroundDrawList(viewport);
        float lineHeight = ImGui.GetTextLineHeight();
        const float pad = 4f, gap = 2f, margin = 12f;
        Vector2 at = viewport.WorkPos + new Vector2(margin, margin);
        foreach (VisibleLine v in shown) {
            string prefix = v.Line.Prefix + "  ";
            string body = v.Line.Body;
            float prefixWidth = ImGui.CalcTextSize(prefix).X;
            float width = prefixWidth + ImGui.CalcTextSize(body).X;
            draw.AddRectFilled(at - new Vector2(pad, pad / 2), at + new Vector2(width + pad, lineHeight + pad / 2),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.6f * v.Alpha)), 3f);
            draw.AddText(at, ImGui.GetColorU32(new Vector4(0.65f, 0.75f, 0.85f, v.Alpha)), prefix);
            draw.AddText(at + new Vector2(prefixWidth, 0f), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, v.Alpha)), body);
            at.Y += lineHeight + pad + gap;
        }
    }
}
