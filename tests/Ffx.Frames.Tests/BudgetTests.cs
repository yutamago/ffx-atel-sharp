// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace Ffx.Frames.Tests;

public class TickBudgetTests {
    [Fact]
    public void Without_a_budget_there_is_no_limit() {
        TickBudget b = new();
        Assert.Equal(int.MaxValue, b.Ticks(double.PositiveInfinity));
        Assert.Equal(int.MaxValue, b.Ticks(0));
        Assert.Equal(int.MaxValue, b.Ticks(double.NaN));
    }

    [Fact]
    public void The_cost_converges_to_the_measured_one() {
        TickBudget b = new();
        for (int i = 0; i < 60; i++) b.Observe(0.020, 10, 0.04);
        Assert.Equal(0.002, b.SecondsPerTick, 6);
        Assert.Equal(20, b.Ticks(0.04));
    }

    [Fact]
    public void At_least_one_tick_and_bad_measurements_are_ignored() {
        TickBudget b = new();
        b.Observe(double.NaN, 3, 0.04);
        b.Observe(double.PositiveInfinity, 3, 0.04);
        b.Observe(1, 0, 0.04);
        Assert.Equal(TickBudget.InitialCost, b.SecondsPerTick);
        for (int i = 0; i < 60; i++) b.Observe(10, 1, 0.04);
        Assert.Equal(1, b.Ticks(0.04));
    }

    [Fact]
    public void A_hitch_counts_as_at_most_the_budget() {
        TickBudget b = new();
        for (int i = 0; i < 60; i++) b.Observe(0.010, 10, 0.04);
        b.Observe(5.0, 1, 0.04); // a load inside one frame's update
        Assert.True(b.SecondsPerTick <= 0.001 + 0.04 * TickBudget.Smoothing + 1e-9);
        for (int i = 0; i < 20; i++) b.Observe(0.010, 10, 0.04);
        Assert.InRange(b.Ticks(0.04), 35, 40);
    }

    [Fact]
    public void The_accumulator_reports_what_it_dropped() {
        TickAccumulator a = new();
        Assert.Equal(9, a.Advance(0.5, 1, 30, 0.3, 100));
        Assert.Equal(6, a.Dropped, 6);
        Assert.Equal(10, a.Advance(1 / 30.0, 20, 30, 0.3, 10));
        Assert.Equal(10, a.Dropped, 6);
        Assert.Equal(1, a.Advance(1 / 30.0, 1, 30, 0.3, 10));
        Assert.Equal(0, a.Dropped);
    }

    [Theory]
    [InlineData(100, 50, 50, 50)]
    [InlineData(18, 300, 0, 18)]
    [InlineData(18, 200, 100, 12)]
    [InlineData(4, 0, 0, 4)]
    public void Reached_speed(double scale, double run, double dropped, double reached) {
        Assert.Equal(reached, ReachedSpeed.Of(scale, run, dropped), 6);
    }
}

/// <summary>
///     The frame loop with a game whose update takes <c>fixed + perTick × ticks</c> seconds, at an unlimited frame rate
///     (each frame's dt is the last frame's time), as the user's setup: target Unlimited, vsync off.
/// </summary>
public class FastForwardTests {
    private const double Fixed = 0.004, PerTick = 0.0025, Present = 0.001;

    private sealed record Run(double[] FrameSeconds, int[] Ticks, TickPlan[] Plans, double[] Dropped) {
        public IEnumerable<int> Tail(int n) => Enumerable.Range(FrameSeconds.Length - n, n);
        public double Reached(int n) => Tail(n).Sum(i => Ticks[i]) / Tail(n).Sum(i => FrameSeconds[i]) / 30;
    }

    private static Run Simulate(FrameSettings s, int frames, Func<int, double>? extra = null) {
        FrameDirector d = new();
        double dt = 1 / 144.0;
        double[] seconds = new double[frames], dropped = new double[frames];
        int[] ticks = new int[frames];
        TickPlan[] plans = new TickPlan[frames];
        for (int i = 0; i < frames; i++) {
            FrameDecision f = d.Next(new FrameInput(dt, 2, true, false, false), s);
            int n = f.Plan.Ticks;
            double update = (n > 0 ? Fixed + PerTick * n : Fixed) + (extra?.Invoke(i) ?? 0);
            if (n > 0) d.Observe(update, n, s);
            seconds[i] = dt = update + Present;
            ticks[i] = n;
            plans[i] = f.Plan;
            dropped[i] = f.Dropped;
        }
        return new Run(seconds, ticks, plans, dropped);
    }

    [Fact]
    public void Without_the_budget_x18_spirals_to_the_cap() {
        // What main did: the CPU manages 1 / 2.5 ms = 400 ticks a second, ×18 asks 540, so each frame owes more than the
        // last until the 0.3 s cap; 100 ticks a frame then took a quarter of a second (the freeze), 25 of them in the
        // drawing update (the crash).
        Run r = Simulate(new FrameSettings { TimeScale = 18, FrameBudgetSeconds = double.PositiveInfinity }, 200);
        Assert.All(r.Tail(50), i => Assert.Equal(100, r.Ticks[i]));
        Assert.All(r.Tail(50), i => Assert.True(r.FrameSeconds[i] > 0.25));
    }

    [Theory]
    [InlineData(18)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    public void With_the_budget_high_scales_keep_the_frame_time_bounded(double scale) {
        FrameSettings s = new() { TimeScale = scale };
        Run r = Simulate(s, 400);
        double most = r.Tail(200).Max(i => r.FrameSeconds[i]);
        Assert.True(most <= s.FrameBudgetSeconds * 1.15 + Present, $"frame took {most * 1000:0.0} ms");
        // The game runs slower than asked, as fast as the budget allows: (budget − fixed) / perTick ticks a frame.
        double reached = r.Reached(200);
        Assert.InRange(reached, 10, 13);
        Assert.True(r.Tail(200).All(i => r.Dropped[i] > 0));
        // What the overlay shows matches what ran.
        double run = r.Tail(200).Sum(i => r.Ticks[i]), dropped = r.Tail(200).Sum(i => r.Dropped[i]);
        Assert.Equal(reached, ReachedSpeed.Of(scale, run, dropped), 0);
        Assert.True(ReachedSpeed.Short(scale, ReachedSpeed.Of(scale, run, dropped)));
    }

    [Theory]
    [InlineData(18)]
    [InlineData(100)]
    public void Every_frame_draws_with_one_effect_pass(double scale) {
        // The path table holds 16; the drawing update adds one path per pass, and the frame's own draw one more.
        Run r = Simulate(new FrameSettings { TimeScale = scale, FrameBudgetSeconds = double.PositiveInfinity }, 200);
        Assert.All(r.Plans.Where(p => p.Ticks > 0), p => Assert.Equal(1, p.ExecFor(p.Loops - 1)));
    }

    [Fact]
    public void A_scale_the_cpu_keeps_up_with_drops_nothing() {
        Run r = Simulate(new FrameSettings { TimeScale = 4 }, 400);
        Assert.Equal(4, r.Reached(200), 1);
        Assert.All(r.Tail(200), i => Assert.Equal(0, r.Dropped[i]));
    }

    [Fact]
    public void A_hitch_does_not_hold_the_speed_down() {
        FrameSettings s = new() { TimeScale = 10 };
        Run r = Simulate(s, 400, i => i == 150 ? 2.0 : 0);
        Assert.InRange(r.Ticks[151], 1, s.MaxTicksPerFrame);
        Assert.Equal(10, r.Reached(200), 0);
    }
}
