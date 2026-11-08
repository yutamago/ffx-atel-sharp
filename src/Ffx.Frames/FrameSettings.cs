// SPDX-License-Identifier: MIT

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ffx.Frames;

/// <summary>How fast the mod lets the game draw.</summary>
public enum FpsTarget {
    /// <summary>The game's own limiter: 30 fps in the world, 60 in the main menu.</summary>
    Game,
    /// <summary>The refresh rate of the monitor the game's window is on.</summary>
    Monitor,
    /// <summary><see cref="FrameSettings.FixedFps"/>.</summary>
    Fixed,
    /// <summary>No limiter: as fast as the machine draws (vsync, when on, still waits for the monitor).</summary>
    Unlimited,
}

/// <summary>Whether a present waits for the monitor's refresh.</summary>
public enum VsyncMode {
    /// <summary>As the game's renderer has it (its own setting).</summary>
    Game,
    On,
    Off,
}

/// <summary>What a drawn frame without a tick does.</summary>
public enum DrawOnlyMode {
    /// <summary>
    ///     Runs the game's update with no tick (exec frames 0) and holds what would still advance, so the frame draws
    ///     again and the camera can move between ticks (interpolation).
    /// </summary>
    Redraw,
    /// <summary>
    ///     Skips the game's update, as the PC's Esc menu does, and lets the renderer show the last frame again. Nothing
    ///     of the game runs; only the camera can still move (when interpolating).
    /// </summary>
    Repeat,
}

/// <summary>
///     The mod's settings. Immutable: the window makes a changed copy and swaps it in, so the game thread always reads
///     one consistent set.
/// </summary>
/// <remarks>
///     Saved as JSON in the module's global state file, which Fahrenheit keeps at
///     <c>state/global/yuta.frames/Ffx.Frames.FramesModule</c>. Missing keys keep their defaults; values out of range
///     are clamped (<see cref="Clamped"/>).
/// </remarks>
public sealed record FrameSettings {
    /// <summary>The whole mod: off gives the game back its own timing, limiter and vsync.</summary>
    public bool Enabled { get; init; } = true;

    public FpsTarget Target { get; init; } = FpsTarget.Monitor;
    /// <summary>The frame rate of <see cref="FpsTarget.Fixed"/>, 10 to 1000.</summary>
    public double FixedFps { get; init; } = 120;
    public VsyncMode Vsync { get; init; } = VsyncMode.Game;
    /// <summary>A small always-on counter in the corner: drawn fps, world ticks a second, vsync, time scale.</summary>
    public bool ShowOverlay { get; init; } = true;

    /// <summary>Game time per real second, 0.05 to 100; 1 is the game's speed.</summary>
    public double TimeScale { get; init; } = 1;
    /// <summary>The scale Ctrl+F9 switches to and back.</summary>
    public double FastScale { get; init; } = 10;
    /// <summary>The scale Shift+F9 switches to and back.</summary>
    public double SlowScale { get; init; } = 0.25;
    /// <summary>Sound off while the game runs faster than ×1, as the booster does.</summary>
    public bool MuteWhenFast { get; init; } = true;
    /// <summary>Also keep ×1 where the PC booster refuses to speed up (its maps, spells, the sphere grid, a load).</summary>
    public bool RespectBoosterLimits { get; init; } = true;

    /// <summary>The most real time one frame counts, in seconds (the game's own is 0.3).</summary>
    public double CapSeconds { get; init; } = 0.3;
    /// <summary>The most ticks one frame runs.</summary>
    public int MaxTicksPerFrame { get; init; } = 100;
    /// <summary>The most whole game updates one frame runs; more ticks than this go through exec frames.</summary>
    public int MaxUpdatesPerFrame { get; init; } = 4;

    public DrawOnlyMode DrawOnly { get; init; } = DrawOnlyMode.Redraw;
    /// <summary>
    ///     Redraw frames: what the effects' programs do (<see cref="EffectsMode"/>). Replay holds them and hands the
    ///     renderer their last tick's draws again, so they keep the game's speed and are drawn every frame.
    /// </summary>
    public EffectsMode Effects { get; init; } = EffectsMode.Replay;
    /// <summary>Redraw frames: skip the cloth step (<c>Ch_DrawClothClear</c> and the cloth jobs <c>Ch_DrawMain</c> queues); the cloth keeps its last tick's shape.</summary>
    public bool HoldCloth { get; init; } = true;
    /// <summary>Redraw frames: skip <c>Ch_CalcMiddle</c> (the characters' bones and motion events).</summary>
    public bool HoldCharacters { get; init; } = true;
    /// <summary>Redraw frames: skip <c>yiAnimMain</c> (the field's animation).</summary>
    public bool HoldFieldAnimation { get; init; } = true;
    /// <summary>Redraw frames: put the fade counters back after <c>Sg_DrawFilter</c> drew them.</summary>
    public bool HoldFades { get; init; } = true;

    /// <summary>Draw the camera between its last two ticks.</summary>
    public bool Interpolate { get; init; } = true;
    /// <summary>Also move the camera on <see cref="DrawOnlyMode.Repeat"/> frames.</summary>
    public bool InterpolateRepeats { get; init; } = true;
    /// <summary>A camera that moved further than this in one tick cut: it is not interpolated (world units).</summary>
    public double CutDistance { get; init; } = 100;
    /// <summary>A camera that turned more than this in one tick cut (degrees).</summary>
    public double CutDegrees { get; init; } = 45;
    /// <summary>
    ///     A camera the game sets at once (the scripts' camera setters through <c>MsSetCheckChange</c>, <c>MsCameraResetMove</c>:
    ///     the cut natives, the magic files' camera) is a cut when its step jumps, however far under the thresholds. A cut draws the camera, every character and their cloth as the tick has them.
    /// </summary>
    public bool ScriptCameraCuts { get; init; } = true;

    /// <summary>
    ///     Draw each character's place and facing between its last two ticks (with <see cref="InterpolateBones"/> its
    ///     animation, with <see cref="InterpolateCloth"/> its cloth and hair). On since its in-game check (2026-11-08).
    /// </summary>
    public bool InterpolateCharacters { get; init; } = true;
    /// <summary>Also draw the characters' bones between their last two ticks (their animation), not only their place.</summary>
    public bool InterpolateBones { get; init; } = true;
    /// <summary>
    ///     Also draw the characters' cloth (hair, Tidus's shorts, Auron's coat) between their last two ticks: its vertices
    ///     blended and carried with the character. Needs <see cref="HoldCloth"/>; without it the cloth keeps its last tick's
    ///     shape and place while the body moves on.
    /// </summary>
    public bool InterpolateCloth { get; init; } = true;
    /// <summary>A character that moved further than this in one tick warped: it is drawn as it is (world units).</summary>
    public double CharacterCutDistance { get; init; } = 50;
    /// <summary>A character that turned more than this in one tick warped (degrees).</summary>
    public double CharacterCutDegrees { get; init; } = 120;

    /// <summary>The settings with every number in its range.</summary>
    public FrameSettings Clamped() => this with {
        Target = Enum.IsDefined(Target) ? Target : FpsTarget.Monitor,
        Vsync = Enum.IsDefined(Vsync) ? Vsync : VsyncMode.Game,
        DrawOnly = Enum.IsDefined(DrawOnly) ? DrawOnly : DrawOnlyMode.Redraw,
        Effects = Enum.IsDefined(Effects) ? Effects : EffectsMode.Replay,
        FixedFps = Clamp(FixedFps, MinFps, MaxFps, 120),
        TimeScale = Clamp(TimeScale, MinScale, MaxScale, 1),
        FastScale = Clamp(FastScale, MinScale, MaxScale, 10),
        SlowScale = Clamp(SlowScale, MinScale, MaxScale, 0.25),
        CapSeconds = Clamp(CapSeconds, 0.05, 2, 0.3),
        MaxTicksPerFrame = Math.Clamp(MaxTicksPerFrame, 1, MaxTicks),
        MaxUpdatesPerFrame = Math.Clamp(MaxUpdatesPerFrame, 1, 10),
        CutDistance = Clamp(CutDistance, 1, 100000, 100),
        CutDegrees = Clamp(CutDegrees, 1, 180, 45),
        CharacterCutDistance = Clamp(CharacterCutDistance, 1, 100000, 50),
        CharacterCutDegrees = Clamp(CharacterCutDegrees, 1, 180, 120),
    };

    public const double MinFps = 10, MaxFps = 1000;
    public const double MinScale = 0.05, MaxScale = 100;
    /// <summary>10 whole updates × 99 exec frames.</summary>
    public const int MaxTicks = 990;

    private static double Clamp(double v, double min, double max, double fallback) =>
        double.IsNaN(v) ? fallback : Math.Clamp(v, min, max);

    private static readonly JsonSerializerOptions Json = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The JSON the state file holds.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a state file; an empty one is the defaults.</summary>
    /// <exception cref="JsonException">The file is not the JSON <see cref="ToJson"/> writes.</exception>
    public static FrameSettings FromJson(string json) {
        if (string.IsNullOrWhiteSpace(json)) return new FrameSettings();
        return (JsonSerializer.Deserialize<FrameSettings>(json, Json) ?? throw new JsonException("expected an object")).Clamped();
    }
}
