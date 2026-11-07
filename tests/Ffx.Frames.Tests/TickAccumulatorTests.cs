// SPDX-License-Identifier: MIT

using System;
using System.Linq;

namespace Ffx.Frames.Tests;

public class TickAccumulatorTests {
    private const double Cap = 0.3;

    /// <summary>Runs <paramref name="frames"/> frames of <paramref name="dt"/> and returns each frame's ticks.</summary>
    private static int[] Run(double dt, int frames, double scale = 1, double rate = 30, double cap = Cap, int max = 100) {
        TickAccumulator acc = new();
        return Enumerable.Range(0, frames).Select(_ => acc.Advance(dt, scale, rate, cap, max)).ToArray();
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(75)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(240)]
    [InlineData(360)]
    [InlineData(1000)]
    public void A_second_at_any_frame_rate_is_30_ticks(int fps) {
        int[] ticks = Run(1.0 / fps, fps * 10);
        Assert.Equal(300, ticks.Sum());
        Assert.All(ticks, t => Assert.InRange(t, 0, 1));
    }

    [Fact]
    public void At_60_fps_every_other_frame_ticks() {
        int[] ticks = Run(1.0 / 60, 12);
        // Half a tick is due after the first frame, a whole one after the second.
        Assert.Equal([0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1], ticks);
        // Exactly one of every two frames, never two in a row and never two idle in a row.
        for (int i = 0; i + 1 < ticks.Length; i++) Assert.Equal(1, ticks[i] + ticks[i + 1]);
    }

    [Fact]
    public void At_30_fps_every_frame_ticks_once() {
        Assert.All(Run(1.0 / 30, 300), t => Assert.Equal(1, t));
    }

    [Fact]
    public void The_games_own_29_97_fps_gets_a_second_tick_now_and_then_like_the_game() {
        // Game_Main's 2-vsync interval: 1001/30000 s. 30 ticks a real second, a little more than 29.97 frames.
        int[] ticks = Run(1001.0 / 30000, 3000);
        Assert.Equal((int)Math.Floor(3000 * 1001.0 / 1000), ticks.Sum());
        Assert.All(ticks, t => Assert.InRange(t, 1, 2));
    }

    [Fact]
    public void At_20_fps_frames_alternate_one_and_two_ticks() {
        int[] ticks = Run(1.0 / 20, 20);
        Assert.Equal(30, ticks.Sum());
        Assert.All(ticks, t => Assert.InRange(t, 1, 2));
    }

    [Fact]
    public void The_carry_keeps_the_fraction() {
        TickAccumulator acc = new();
        Assert.Equal(0, acc.Advance(0.02, 1, 30, Cap, 100));
        Assert.Equal(0.6, acc.Fraction, 6);
        Assert.Equal(1, acc.Advance(0.02, 1, 30, Cap, 100));
        Assert.Equal(0.2, acc.Fraction, 6);
        Assert.Equal(0.2 / 30, acc.Carry, 9);
    }

    [Fact]
    public void A_frame_counts_at_most_the_cap() {
        TickAccumulator acc = new();
        Assert.Equal(9, acc.Advance(5.0, 1, 30, 0.3, 100));
        Assert.Equal(15, acc.Advance(5.0, 1, 30, 0.5, 100));
    }

    [Fact]
    public void The_cap_is_on_real_time_so_a_scale_still_multiplies() {
        TickAccumulator acc = new();
        Assert.Equal(90, acc.Advance(1.0, 10, 30, 0.3, 1000));
    }

    [Fact]
    public void Max_ticks_drops_the_excess_and_keeps_less_than_a_tick() {
        TickAccumulator acc = new();
        Assert.Equal(50, acc.Advance(0.3, 100, 30, 0.3, 50));
        Assert.InRange(acc.Fraction, 0, 0.999999);
        Assert.Equal(0, acc.Advance(0, 100, 30, 0.3, 50));
    }

    [Theory]
    [InlineData(0.25, 30, 15)]
    [InlineData(0.25, 60, 15)]
    [InlineData(0.5, 144, 30)]
    [InlineData(2, 60, 120)]
    [InlineData(4, 30, 240)]
    [InlineData(100, 144, 6000)]
    public void A_time_scale_scales_ticks_per_real_second(double scale, int fps, int expectedPerTwoSeconds) {
        int[] ticks = Run(1.0 / fps, fps * 2, scale, max: 990);
        Assert.Equal(expectedPerTwoSeconds, ticks.Sum());
    }

    [Fact]
    public void Slow_motion_at_a_quarter_ticks_every_fourth_frame_at_30_fps() {
        int[] ticks = Run(1.0 / 30, 16, 0.25);
        Assert.Equal(4, ticks.Sum());
        for (int i = 0; i + 3 < ticks.Length; i++) Assert.Equal(1, ticks[i] + ticks[i + 1] + ticks[i + 2] + ticks[i + 3]);
    }

    [Fact]
    public void Zero_tick_frames_happen_and_advance_the_fraction() {
        TickAccumulator acc = new();
        double last = -1;
        for (int i = 0; i < 4; i++) {
            Assert.Equal(0, acc.Advance(1.0 / 240, 1, 30, Cap, 100));
            Assert.True(acc.Fraction > last);
            last = acc.Fraction;
        }
    }

    [Fact]
    public void The_main_menu_rate_is_60() {
        Assert.Equal(60, Run(1.0 / 120, 120, rate: 60).Sum());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    [InlineData(0)]
    public void A_bad_frame_time_runs_nothing(double dt) {
        TickAccumulator acc = new();
        Assert.Equal(0, acc.Advance(dt, 1, 30, Cap, 100));
        Assert.Equal(0, acc.Carry);
    }

    [Fact]
    public void Variable_frame_times_keep_the_total() {
        // A VRR display: frame times between 4 and 20 ms.
        Random r = new(7);
        TickAccumulator acc = new();
        double time = 0;
        int ticks = 0;
        while (time < 10) {
            double dt = 0.004 + r.NextDouble() * 0.016;
            time += dt;
            ticks += acc.Advance(dt, 1, 30, Cap, 100);
        }
        Assert.Equal(Math.Floor(time * 30 + 1e-6), ticks + 0.0, 0);
    }

    [Fact]
    public void Reset_drops_the_carry() {
        TickAccumulator acc = new();
        acc.Advance(0.02, 1, 30, Cap, 100);
        acc.Reset();
        Assert.Equal(0, acc.Carry);
        Assert.Equal(0, acc.Advance(0.02, 1, 30, Cap, 100));
    }
}
