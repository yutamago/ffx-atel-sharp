// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;

using Fahrenheit;

using Hexa.NET.ImGui;

namespace Ffx.Frames;

/// <summary>
///     Frames (<c>yuta.frames</c>): draws FFX at the monitor's refresh rate, a fixed rate or uncapped while the world
///     keeps its 30 ticks a second, draws the camera between ticks, and scales game time (slow motion, fast-forward)
///     without drawing more frames. F9 opens the settings (docs/research/frame-timing.md, "What the Frames mod does").
/// </summary>
[FhLoad(FhGameId.FFX)]
public sealed class FramesModule : FhModule {
    private FrameSettings _settings = new();
    private FrameHooks? _hooks;
    private FileStream? _stateFile;
    private readonly Lock _saveLock = new();
    private bool _open;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string _hookProblem = "";
    private double _saveAt = double.NaN;

    private FrameSettings Settings => Volatile.Read(ref _settings);

    public override bool init(FhModContext mod_context, FileStream global_state_file) {
        _stateFile = global_state_file;
        try {
            using StreamReader reader = new(global_state_file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            _settings = FrameSettings.FromJson(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) {
            _logger.Error($"Frames: cannot read the settings ({ex.Message}); using the defaults");
        }
        nint baseAddr = Win32.ModuleBase("FFX.exe");
        if (baseAddr == 0) {
            _logger.Error("Frames: FFX.exe is not loaded; the mod does nothing.");
            return true;
        }
        Game game = new(baseAddr);
        FrameHooks hooks = new(game, _logger, () => Settings);
        if (!hooks.CheckGame()) {
            _logger.Error("Frames: a hook cannot go in safely (see above); the mod installs nothing and the game runs as without it.");
            return true;
        }
        _hooks = hooks;
        if (!_hooks.Install(this)) {
            _hookProblem = "Some hooks failed (see the log); the mod may not work.";
            _logger.Error("Frames: not every hook went in; the frame timing may be wrong. Switch the mod off in the F9 window or remove it from the load order.");
        }
        _hooks.PatchLimiter();
        FrameSettings s = Settings;
        _logger.Info($"Frames: {(s.Enabled ? "on" : "off")}, target {s.Target}{(s.Target == FpsTarget.Fixed ? $" {s.FixedFps}" : "")}, vsync {s.Vsync}, "
            + $"time scale ×{s.TimeScale}, draw-only frames {s.DrawOnly}, interpolation {(s.Interpolate ? "on" : "off")}. F9 opens the window.");
        return true;
    }

    /// <summary>Swaps in new settings at once; the file is written half a second after the last change (a slider being dragged changes them every frame).</summary>
    private void Set(FrameSettings next) {
        Volatile.Write(ref _settings, next.Clamped());
        _saveAt = _clock.Elapsed.TotalSeconds + 0.5;
    }

    private void SaveWhenDue() {
        if (double.IsNaN(_saveAt) || _clock.Elapsed.TotalSeconds < _saveAt) return;
        _saveAt = double.NaN;
        Save(Settings);
    }

    private void Save(FrameSettings s) {
        if (_stateFile is null) return;
        try {
            lock (_saveLock) {
                byte[] json = Encoding.UTF8.GetBytes(s.ToJson());
                _stateFile.SetLength(0);
                _stateFile.Position = 0;
                _stateFile.Write(json);
                _stateFile.Flush(flushToDisk: true);
            }
        }
        catch (IOException ex) {
            _logger.Error($"Frames: cannot save the settings: {ex.Message}");
        }
    }

    private static readonly Vector4 Grey = new(0.7f, 0.7f, 0.7f, 1f);
    private static readonly Vector4 Red = new(1f, 0.45f, 0.4f, 1f);
    private static readonly Vector4 Yellow = new(1f, 0.85f, 0.3f, 1f);
    private static readonly string[] Targets = ["Game (30 fps)", "Monitor refresh rate", "Fixed", "Unlimited"];
    private static readonly string[] Vsyncs = ["As the game has it", "On", "Off"];
    private static readonly string[] DrawOnlys = ["Redraw (camera moves between ticks)", "Repeat the last frame"];
    private static readonly string[] EffectModes = ["Replay the last tick's (right speed)", "Run every frame (too fast)", "Hold (missing between ticks)"];

    /// <summary>Hotkeys and the window. Render thread: it reads the stats and swaps in new settings.</summary>
    public override void render_imgui() {
        Hotkeys();
        SaveWhenDue();
        _hooks?.Stats.Sample(_clock.Elapsed.TotalSeconds);
        Overlay(Settings);
        if (!_open) return;
        FrameSettings s = Settings;
        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Frames###yuta.frames", ref _open)) {
            Window(s);
        }
        ImGui.End();
    }

    private void Hotkeys() {
        ImGuiIOPtr io = ImGui.GetIO();
        if (ImGui.IsKeyPressed(ImGuiKey.F9, false)) {
            FrameSettings s = Settings;
            if (io.KeyCtrl && io.KeyShift) Set(s with { TimeScale = 1 });
            else if (io.KeyCtrl) Set(s with { TimeScale = TimeScaleSteps.Toggle(s.TimeScale, s.FastScale) });
            else if (io.KeyShift) Set(s with { TimeScale = TimeScaleSteps.Toggle(s.TimeScale, s.SlowScale) });
            else if (io.KeyAlt) Set(s with { Vsync = s.Vsync == VsyncMode.On ? VsyncMode.Off : VsyncMode.On });
            else _open = !_open;
        }
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.PageUp, true)) Set(Settings with { TimeScale = TimeScaleSteps.Up(Settings.TimeScale) });
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.PageDown, true)) Set(Settings with { TimeScale = TimeScaleSteps.Down(Settings.TimeScale) });
    }

    /// <summary>The corner counter: what to look at while trying settings, without opening the window.</summary>
    private void Overlay(FrameSettings s) {
        FrameHooks? h = _hooks;
        if (!s.ShowOverlay || h is null) return;
        FrameStats st = h.Stats;
        ImGui.SetNextWindowPos(new Vector2(ImGui.GetIO().DisplaySize.X - 10, 10), ImGuiCond.Always, new Vector2(1, 0));
        ImGui.SetNextWindowBgAlpha(0.55f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("###yuta.frames.overlay", flags)) {
            string vsync = s.Vsync switch { VsyncMode.On => "on", VsyncMode.Off => "off", _ => "game" };
            ImGui.Text($"{st.Rate(Counter.Presents):0} fps   {st.Rate(Counter.WorldTicks):0.0} ticks/s");
            ImGui.TextColored(Grey, $"vsync {vsync} (Alt+F9)   ×{h.LastScale:0.##}{(s.Enabled ? "" : "   mod off")}");
            ImGui.TextColored(Grey, "F9 settings");
        }
        ImGui.End();
    }

    private void Window(FrameSettings s) {
        FrameHooks? h = _hooks;
        if (h is null) {
            ImGui.TextColored(Red, "Not running: FFX.exe was not found or is another version (see the log).");
            return;
        }
        if (h.Failed) ImGui.TextColored(Red, h.Status);
        if (_hookProblem.Length > 0) ImGui.TextColored(Red, _hookProblem);
        if (!h.LimiterPatched) ImGui.TextColored(Yellow, "The frame limiter is not patched: the game stays at its own 30 fps.");

        bool enabled = s.Enabled;
        if (ImGui.Checkbox("On", ref enabled)) Set(s with { Enabled = enabled });
        ImGui.SameLine();
        ImGui.TextColored(Grey, "(off: the game's own timing)");

        if (ImGui.CollapsingHeader("Frame rate", ImGuiTreeNodeFlags.DefaultOpen)) {
            int target = (int)s.Target;
            if (ImGui.Combo("Target", ref target, Targets, Targets.Length)) Set(s with { Target = (FpsTarget)target });
            if (s.Target == FpsTarget.Fixed) {
                float fps = (float)s.FixedFps;
                if (ImGui.SliderFloat("fps", ref fps, (float)FrameSettings.MinFps, 360f, "%.0f")) Set(s with { FixedFps = fps });
            }
            int vsync = (int)s.Vsync;
            if (ImGui.Combo("Vsync", ref vsync, Vsyncs, Vsyncs.Length)) Set(s with { Vsync = (VsyncMode)vsync });
            bool overlay = s.ShowOverlay;
            if (ImGui.Checkbox("Show the fps counter", ref overlay)) Set(s with { ShowOverlay = overlay });
            int drawOnly = (int)s.DrawOnly;
            if (ImGui.Combo("Frames between ticks", ref drawOnly, DrawOnlys, DrawOnlys.Length)) Set(s with { DrawOnly = (DrawOnlyMode)drawOnly });
            bool interp = s.Interpolate;
            if (ImGui.Checkbox("Interpolate the camera", ref interp)) Set(s with { Interpolate = interp });
            ImGui.SameLine();
            bool chars = s.InterpolateCharacters;
            if (ImGui.Checkbox("the characters (experimental)", ref chars)) Set(s with { InterpolateCharacters = chars });
            int effects = (int)s.Effects;
            if (ImGui.Combo("Effects between ticks", ref effects, EffectModes, EffectModes.Length)) Set(s with { Effects = (EffectsMode)effects });
        }

        if (ImGui.CollapsingHeader("Time scale", ImGuiTreeNodeFlags.DefaultOpen)) {
            float scale = (float)s.TimeScale;
            if (ImGui.SliderFloat("×", ref scale, (float)FrameSettings.MinScale, (float)FrameSettings.MaxScale, "%.2f", ImGuiSliderFlags.Logarithmic)) Set(s with { TimeScale = scale });
            if (ImGui.Button("×0.25")) Set(s with { TimeScale = 0.25 });
            ImGui.SameLine();
            if (ImGui.Button("×0.5")) Set(s with { TimeScale = 0.5 });
            ImGui.SameLine();
            if (ImGui.Button("×1")) Set(s with { TimeScale = 1 });
            ImGui.SameLine();
            if (ImGui.Button("×2")) Set(s with { TimeScale = 2 });
            ImGui.SameLine();
            if (ImGui.Button("×10")) Set(s with { TimeScale = 10 });
            ImGui.SameLine();
            if (ImGui.Button("×100")) Set(s with { TimeScale = 100 });
            bool mute = s.MuteWhenFast;
            if (ImGui.Checkbox("Mute above ×1", ref mute)) Set(s with { MuteWhenFast = mute });
            ImGui.SameLine();
            bool booster = s.RespectBoosterLimits;
            if (ImGui.Checkbox("Keep the booster's limits", ref booster)) Set(s with { RespectBoosterLimits = booster });
            ImGui.TextColored(Grey, $"Ctrl+F9 ×{s.FastScale:0.##} / ×1, Shift+F9 ×{s.SlowScale:0.##} / ×1, Ctrl+Shift+F9 ×1, Ctrl+PgUp/PgDn step, Alt+F9 vsync on/off.");
        }

        if (ImGui.CollapsingHeader("Live")) {
            FrameStats st = h.Stats;
            ImGui.Text($"Drawn: {st.Rate(Counter.Presents):0.0} fps (monitor {h.MonitorHz} Hz)");
            ImGui.Text($"World ticks: {st.Rate(Counter.WorldTicks):0.0}/s (planned {st.Rate(Counter.PlannedTicks):0.0}/s; ×1 reads about 30)");
            ImGui.Text($"Game updates: {st.Rate(Counter.Updates):0.0}/s; frames with ticks {st.Rate(Counter.TickFrames):0.0}/s, redrawn {st.Rate(Counter.RedrawFrames):0.0}/s, repeated {st.Rate(Counter.RepeatFrames):0.0}/s");
            ImGui.Text($"Scale now: ×{h.LastScale:0.##}, ticks last frame {h.LastTicks}, between ticks {h.LastAlpha:0.00}");
            string? why = h.ExcludedReason;
            if (why is not null) ImGui.TextColored(Yellow, $"Time scale held at ×1: {why}");
            CameraHistory cam = h.Camera;
            string step = cam.LastStep is (double d, double deg) ? $"{d:0.0} units, {deg:0.0}°" : "-";
            ImGui.Text($"Camera: last tick moved {step}; cuts {cam.Cuts}{(cam.Cut ? " (cut now)" : "")}");
            ImGui.Text($"Effects: {h.EffectDraws} draws recorded; replayed {st.Rate(Counter.EffectReplays):0}/s, gone {st.Rate(Counter.EffectsGone):0}/s");
            ImGui.Text($"Characters: {h.Characters} held, between ticks {st.Rate(Counter.CharactersInterpolated):0}/s; warps {h.CharacterCuts}");
            ImGui.TextColored(Grey, h.Status);
        }

        if (ImGui.CollapsingHeader("Advanced")) {
            float cap = (float)s.CapSeconds;
            if (ImGui.SliderFloat("Most real time per frame (s)", ref cap, 0.05f, 2f, "%.2f")) Set(s with { CapSeconds = cap });
            int maxTicks = s.MaxTicksPerFrame;
            if (ImGui.SliderInt("Most ticks per frame", ref maxTicks, 1, FrameSettings.MaxTicks)) Set(s with { MaxTicksPerFrame = maxTicks });
            int maxUpdates = s.MaxUpdatesPerFrame;
            if (ImGui.SliderInt("Whole updates per frame", ref maxUpdates, 1, 10)) Set(s with { MaxUpdatesPerFrame = maxUpdates });
            float fast = (float)s.FastScale, slow = (float)s.SlowScale;
            if (ImGui.SliderFloat("Ctrl+F9 scale", ref fast, 1f, (float)FrameSettings.MaxScale, "%.1f", ImGuiSliderFlags.Logarithmic)) Set(s with { FastScale = fast });
            if (ImGui.SliderFloat("Shift+F9 scale", ref slow, (float)FrameSettings.MinScale, 1f, "%.2f", ImGuiSliderFlags.Logarithmic)) Set(s with { SlowScale = slow });
            ImGui.Separator();
            ImGui.TextColored(Grey, "Held on redrawn frames (things that would otherwise run once per drawn frame):");
            bool chars = s.HoldCharacters, field = s.HoldFieldAnimation, fades = s.HoldFades, cloth = s.HoldCloth;
            if (ImGui.Checkbox("Characters' bones and motion events (Ch_CalcMiddle)", ref chars)) Set(s with { HoldCharacters = chars });
            if (ImGui.Checkbox("Field animation (yiAnimMain)", ref field)) Set(s with { HoldFieldAnimation = field });
            if (ImGui.Checkbox("Fade counters (Sg_DrawFilter)", ref fades)) Set(s with { HoldFades = fades });
            if (ImGui.Checkbox("Cloth (Sg_ClothStep)", ref cloth)) Set(s with { HoldCloth = cloth });
            ImGui.Separator();
            bool bones = s.InterpolateBones;
            if (ImGui.Checkbox("Interpolate the characters' bones (their animation), not only their place", ref bones)) Set(s with { InterpolateBones = bones });
            float cdist = (float)s.CharacterCutDistance, cdeg = (float)s.CharacterCutDegrees;
            if (ImGui.SliderFloat("Character warp: moved more than", ref cdist, 1f, 2000f, "%.0f units", ImGuiSliderFlags.Logarithmic)) Set(s with { CharacterCutDistance = cdist });
            if (ImGui.SliderFloat("Character warp: turned more than", ref cdeg, 1f, 180f, "%.0f°")) Set(s with { CharacterCutDegrees = cdeg });
            bool repeats = s.InterpolateRepeats;
            if (ImGui.Checkbox("Move the camera on repeated frames", ref repeats)) Set(s with { InterpolateRepeats = repeats });
            float dist = (float)s.CutDistance, deg = (float)s.CutDegrees;
            if (ImGui.SliderFloat("Cut: moved more than", ref dist, 1f, 2000f, "%.0f units", ImGuiSliderFlags.Logarithmic)) Set(s with { CutDistance = dist });
            if (ImGui.SliderFloat("Cut: turned more than", ref deg, 1f, 180f, "%.0f°")) Set(s with { CutDegrees = deg });
            if (ImGui.Button("Defaults")) Set(new FrameSettings { Enabled = s.Enabled });
        }
        ImGui.Separator();
        ImGui.TextColored(Grey, "F9 hides this window.");
    }
}
