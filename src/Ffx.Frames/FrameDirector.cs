// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Frames;

/// <summary>What a drawn frame does.</summary>
public enum FrameKind {
    /// <summary>The mod is off: the game's own timing.</summary>
    Vanilla,
    /// <summary>One tick or more (<see cref="FrameDecision.Plan"/>).</summary>
    Tick,
    /// <summary>No tick: the game's update runs with exec frames 0 and the holds (<see cref="DrawOnlyMode.Redraw"/>).</summary>
    Redraw,
    /// <summary>No tick: the game's update is skipped (<see cref="DrawOnlyMode.Repeat"/>).</summary>
    Repeat,
}

/// <summary>What the game looks like at the start of a drawn frame (read on the game thread).</summary>
/// <param name="Dt">The frame's real time, seconds (<c>updateFFX</c>'s argument).</param>
/// <param name="GameVsyncs"><c>g_FrameVsyncs</c>: 2 in the world (30 fps), 1 in the main menu (60 fps).</param>
/// <param name="WorldActive">The last game update reached the world update (not the menu, a pause, a load).</param>
/// <param name="Movie">A movie plays (<c>g_WaitFramesByTime</c> or the movie byte).</param>
/// <param name="ScaleExcluded">The time scale must stay ×1 here (<see cref="TimeScaleRules"/>).</param>
/// <param name="Booster">The PC booster's factor this frame (1, 2 or 4).</param>
/// <param name="Scale">The time scale to run, when it is not the setting's (the start-up ramp, <see cref="ScaleGuard"/>).</param>
public readonly record struct FrameInput(double Dt, int GameVsyncs, bool WorldActive, bool Movie, bool ScaleExcluded, double Booster = 1, double? Scale = null);

/// <param name="Kind">What the frame does.</param>
/// <param name="Plan">Its ticks (Tick frames).</param>
/// <param name="Alpha">Where the frame is between the last tick and the next, 0 to 1: the interpolation factor.</param>
/// <param name="Scale">The time scale applied (1 where excluded), booster included.</param>
/// <param name="Mute">The sound should be off.</param>
/// <param name="Dropped">Ticks the frame owed but did not run (the CPU could not keep up with the scale): the reached speed is <c>Scale × Plan.Ticks / (Plan.Ticks + Dropped)</c>.</param>
public readonly record struct FrameDecision(FrameKind Kind, TickPlan Plan, double Alpha, double Scale, bool Mute, double Dropped = 0);

/// <summary>
///     Decides, at the start of each drawn frame, how many ticks it runs and what a frame without one does. Pure: the
///     module reads the game into a <see cref="FrameInput"/> and carries the decision out.
/// </summary>
public sealed class FrameDirector {
    private readonly TickAccumulator _acc = new();
    private readonly TickBudget _budget = new();

    public TickAccumulator Accumulator => _acc;
    public TickBudget Budget => _budget;

    /// <summary>
    ///     The most ticks this frame may run: the setting, what the plan can hold in its updates, and what fits into the
    ///     frame's time budget at the measured cost of a tick.
    /// </summary>
    public int MaxTicks(FrameSettings s) =>
        Math.Min(Math.Min(s.MaxTicksPerFrame, TickPlan.MaxTicks(s.MaxUpdatesPerFrame)), _budget.Ticks(s.FrameBudgetSeconds));

    /// <summary>A frame's game update took <paramref name="seconds"/> for <paramref name="ticks"/> ticks (the budget's measurement).</summary>
    public void Observe(double seconds, int ticks, FrameSettings s) => _budget.Observe(seconds, ticks, s.FrameBudgetSeconds);

    /// <summary>The tick rate for the game's vsync count: 30 a second in the world, 60 in the main menu (one game update per frame at its 60 fps, as the game does there).</summary>
    public static double TickRate(int gameVsyncs) => 60.0 / Math.Clamp(gameVsyncs, 1, 2);

    public FrameDecision Next(in FrameInput input, FrameSettings s) {
        if (!s.Enabled) {
            _acc.Reset();
            _budget.Reset();
            return new FrameDecision(FrameKind.Vanilla, TickPlan.None, 1, 1, false);
        }
        double user = input.ScaleExcluded ? 1 : input.Scale ?? s.TimeScale;
        double scale = user * (input.Booster > 0 ? input.Booster : 1);
        int ticks = _acc.Advance(input.Dt, scale, TickRate(input.GameVsyncs), s.CapSeconds, MaxTicks(s));
        bool mute = s.MuteWhenFast && user > 1;
        if (ticks > 0) return new FrameDecision(FrameKind.Tick, TickPlan.For(ticks, s.MaxUpdatesPerFrame), _acc.Fraction, scale, mute, _acc.Dropped);
        // Redraw only where there is a world to draw again: not the menus, a pause, a load (their logic runs once per
        // update, so a second update would speed them up) and not a movie (its picture steps per update and per draw).
        bool redraw = s.DrawOnly == DrawOnlyMode.Redraw && input.WorldActive && !input.Movie && input.GameVsyncs >= 2;
        return new FrameDecision(redraw ? FrameKind.Redraw : FrameKind.Repeat, TickPlan.None, _acc.Fraction, scale, mute, _acc.Dropped);
    }
}

/// <summary>The speed the game reached, for the overlay.</summary>
public static class ReachedSpeed {
    /// <summary>
    ///     The scale the run ticks gave: <paramref name="scale"/> × run / (run + dropped), from the ticks a second that
    ///     ran and those dropped; <paramref name="scale"/> when none were dropped.
    /// </summary>
    public static double Of(double scale, double ticksRun, double ticksDropped) =>
        ticksDropped > 0 && ticksRun + ticksDropped > 0 ? scale * ticksRun / (ticksRun + ticksDropped) : scale;

    /// <summary>Whether the reached scale is far enough below the asked one to show (more than 2 % short).</summary>
    public static bool Short(double asked, double reached) => reached < asked * 0.98;
}

/// <summary>Where the time scale stays ×1, and why (for the window).</summary>
public static class TimeScaleRules {
    /// <summary>The first reason the scale is held at ×1, or null when it applies.</summary>
    /// <param name="synced">A voice-synced cut scene (<c>g_SyncOn</c>): its ticks follow the voices' clock.</param>
    /// <param name="movieWait">A movie's waits count its frames (<c>g_WaitFramesByTime</c>).</param>
    /// <param name="movieFading"><c>g_MovieFading</c>.</param>
    /// <param name="canSpeedUp"><c>Sg_CanSpeedUp()</c>: 0 for a video without a movie camera and the game's story exceptions.</param>
    /// <param name="boosterRefuses">The booster's own exclusions (only with <see cref="FrameSettings.RespectBoosterLimits"/>).</param>
    public static string? Excluded(bool synced, bool movieWait, bool movieFading, bool canSpeedUp, bool boosterRefuses) =>
        synced ? "voice-synced scene"
        : movieWait ? "movie"
        : movieFading ? "movie fade"
        : !canSpeedUp ? "the game allows no speed-up here (Sg_CanSpeedUp)"
        : boosterRefuses ? "the booster's exclusions"
        : null;
}
