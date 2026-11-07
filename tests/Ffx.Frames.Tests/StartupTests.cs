// SPDX-License-Identifier: MIT

using System;
using System.Linq;

namespace Ffx.Frames.Tests;

/// <summary>
///     The settings saved at the start-up crash (2026-11-08): enabled, Unlimited, vsync off, ×18.42, cap 0.3 s, 100 ticks
///     and 4 updates a frame, Redraw, Replay, the characters interpolated.
/// </summary>
public class StartupTests {
    private static readonly FrameSettings Saved = new FrameSettings {
        Target = FpsTarget.Unlimited, Vsync = VsyncMode.Off, TimeScale = 18.42, CapSeconds = 0.3, MaxTicksPerFrame = 100,
        MaxUpdatesPerFrame = 4, DrawOnly = DrawOnlyMode.Redraw, Effects = EffectsMode.Replay, HoldCloth = true,
        HoldCharacters = false, HoldFieldAnimation = true, HoldFades = false, Interpolate = true, InterpolateCharacters = true,
        InterpolateBones = true,
    }.Clamped();

    private static FrameInput World(double dt, double? scale = null) => new(dt, 2, true, false, false, 1, scale);

    /// <summary>What main's plan gave the drawing update: the ticks spread evenly over the updates, the last one ⌊ticks / updates⌋.</summary>
    private static int OldDrawingPasses(int ticks, int loops) => ticks / Math.Min(ticks, loops);

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.5)]
    [InlineData(5.0)]
    [InlineData(double.PositiveInfinity)]
    public void A_frame_after_a_load_owes_the_cap_and_draws_with_one_pass(double dt) {
        // A load's frame hits the 0.3 s cap: ⌊0.3 × 30 × 18.42⌋ = 165 ticks owed, more than the 100 allowed. On main all
        // 100 ran, 25 of them (and 25 effect passes, 25 VFX paths of 16) in the drawing update: the start-up crash.
        Assert.Equal(25, OldDrawingPasses(100, 4));
        FrameDirector d = new();
        FrameDecision f = d.Next(World(dt), Saved with { FrameBudgetSeconds = double.PositiveInfinity });
        Assert.Equal(FrameKind.Tick, f.Kind);
        Assert.Equal(100, f.Plan.Ticks);
        Assert.Equal(1, f.Plan.ExecFor(f.Plan.Loops - 1));
        Assert.True(f.Dropped >= 65 - 1e-9, $"dropped {f.Dropped}");
        Assert.InRange(d.Accumulator.Carry * 30, 0, 1);
    }

    [Theory]
    [InlineData(100 / 9.0 - 0.01, 99)]
    [InlineData(100 / 9.0, 100)]
    [InlineData(100 / 9.0 + 0.01, 100)]
    [InlineData(18.42, 100)]
    public void At_the_most_ticks_per_frame_boundary(double scale, int ticks) {
        // 0.3 s × 30 × scale reaches the 100 allowed at ×11.1; past it the plan is the same 100 ticks, so nothing changes at ×18.
        FrameDirector d = new();
        FrameDecision f = d.Next(World(0.3), Saved with { TimeScale = scale, FrameBudgetSeconds = double.PositiveInfinity });
        Assert.Equal(ticks, f.Plan.Ticks);
        Assert.Equal(1, f.Plan.ExecFor(f.Plan.Loops - 1));
        Assert.Equal(f.Plan.Ticks, Enumerable.Range(0, f.Plan.Loops).Sum(f.Plan.ExecFor));
        Assert.All(Enumerable.Range(0, f.Plan.Loops), i => Assert.InRange(f.Plan.ExecFor(i), 1, TickPlan.MaxExec));
    }

    [Fact]
    public void The_first_frame_without_a_measurement_runs_what_the_budget_guesses() {
        FrameDirector d = new();
        FrameDecision f = d.Next(World(0.3), Saved);
        Assert.Equal(new TickBudget().Ticks(Saved.FrameBudgetSeconds), f.Plan.Ticks);
        Assert.InRange(f.Plan.Ticks, 39, 40);
        Assert.Equal(1, f.Plan.ExecFor(f.Plan.Loops - 1));
    }

    [Fact]
    public void Start_up_ramps_from_x1_to_the_saved_scale() {
        // Start-up: the load's long frame, then the world at about 200 fps.
        ScaleGuard g = new(Saved.TimeScale);
        FrameDirector d = new();
        double[] dts = [3.0, 0.3, .. Enumerable.Repeat(0.005, 2000)];
        double t = 0, reachedAt = double.NaN;
        int first = -1;
        foreach (double dt in dts) {
            double scale = g.Effective(Saved.TimeScale);
            FrameDecision f = d.Next(World(dt, scale), Saved);
            if (first < 0) first = f.Plan.Ticks;
            if (f.Plan.Ticks > 0) d.Observe(0.001 * f.Plan.Ticks, f.Plan.Ticks, Saved);
            g.WorldFrame(dt, scale);
            t += Math.Min(dt, ScaleGuard.MaxStep);
            if (double.IsNaN(reachedAt) && scale >= Saved.TimeScale) reachedAt = t;
        }
        // The load's frame runs at ×1: 9 ticks for the 0.3 s cap, as the game would.
        Assert.Equal(9, first);
        Assert.InRange(reachedAt, 4.0, 4.5); // log2(18.42) ≈ 4.2 s
        Assert.Equal(Saved.TimeScale, g.Effective(Saved.TimeScale));
    }

    [Fact]
    public void Slow_motion_is_never_ramped_nor_held_back() {
        ScaleGuard g = new(0.25);
        Assert.Equal(0.25, g.Effective(0.25));
        Assert.Equal(0.25, g.Persisted(0.25));
        Assert.Equal(1, g.Effective(1));
    }

    [Fact]
    public void An_unchecked_scale_is_not_saved() {
        ScaleGuard g = new(18.42);
        Assert.Equal(1, g.Persisted(18.42));
        Assert.Equal(1, g.Effective(18.42));
        // Ramp up and run at ×18.42: saved only after CheckSeconds at it.
        double t = 0;
        while (g.Effective(18.42) < 18.42) {
            g.WorldFrame(0.01, g.Effective(18.42));
            t += 0.01;
        }
        Assert.Equal(1, g.Persisted(18.42));
        for (int i = 0; i < (int)(ScaleGuard.CheckSeconds / 0.01) - 5; i++) g.WorldFrame(0.01, 18.42);
        Assert.Equal(1, g.Persisted(18.42));
        for (int i = 0; i < 10; i++) g.WorldFrame(0.01, 18.42);
        Assert.Equal(18.42, g.Persisted(18.42), 6);
        // A lower scale is saved at once; a higher one waits for its own check.
        Assert.Equal(4, g.Persisted(4));
        Assert.Equal(18.42, g.Persisted(50), 6);
    }

    [Fact]
    public void Loads_and_held_frames_do_not_count_as_checked() {
        ScaleGuard g = new(10);
        g.WorldFrame(60, 10); // one long frame counts as at most MaxStep
        Assert.True(g.Limit <= Math.Pow(2, ScaleGuard.MaxStep / ScaleGuard.DoublingSeconds) + 1e-9);
        for (int i = 0; i < 1000; i++) g.WorldFrame(0.01, 1); // held at ×1 (a synced scene)
        Assert.Equal(1, g.Persisted(10));
        Assert.True(g.Done);
    }
}
