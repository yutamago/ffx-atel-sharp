// SPDX-License-Identifier: MIT

global using Xunit;

using System.Linq;

namespace Ffx.Frames.Tests;

public class TickPlanTests {
    [Theory]
    [InlineData(1, 4, 1)]
    [InlineData(3, 4, 3)]
    [InlineData(4, 4, 4)]
    [InlineData(10, 4, 4)]
    [InlineData(100, 4, 4)]
    [InlineData(5, 1, 1)]
    public void Ticks_become_whole_updates_up_to_the_limit(int ticks, int maxLoops, int loops) {
        TickPlan plan = TickPlan.For(ticks, maxLoops);
        Assert.Equal(loops, plan.Loops);
        Assert.Equal(ticks, Enumerable.Range(0, plan.Loops).Sum(plan.ExecFor));
    }

    [Fact]
    public void Exec_frames_are_spread_evenly() {
        TickPlan plan = TickPlan.For(10, 4);
        Assert.Equal([3, 3, 2, 2], Enumerable.Range(0, 4).Select(plan.ExecFor));
        Assert.Equal(0, plan.ExecFor(4));
    }

    [Fact]
    public void At_most_99_ticks_per_update() {
        TickPlan plan = TickPlan.For(1000, 4);
        Assert.Equal(396, plan.Ticks);
        Assert.All(Enumerable.Range(0, plan.Loops), i => Assert.Equal(99, plan.ExecFor(i)));
    }

    [Fact]
    public void No_ticks_no_updates() {
        Assert.Equal(TickPlan.None, TickPlan.For(0, 4));
        Assert.Equal(0, TickPlan.None.ExecFor(0));
    }
}

public class DirectorTests {
    private static readonly FrameSettings Defaults = new();

    private static FrameInput World(double dt, bool world = true, bool movie = false, bool excluded = false, int vsyncs = 2, double booster = 1) =>
        new(dt, vsyncs, world, movie, excluded, booster);

    [Fact]
    public void Off_is_vanilla() {
        FrameDirector d = new();
        FrameDecision f = d.Next(World(1 / 144.0), Defaults with { Enabled = false });
        Assert.Equal(FrameKind.Vanilla, f.Kind);
    }

    [Fact]
    public void At_120_fps_one_frame_in_four_ticks_and_the_others_redraw() {
        FrameDirector d = new();
        FrameKind[] kinds = Enumerable.Range(0, 8).Select(_ => d.Next(World(1 / 120.0), Defaults).Kind).ToArray();
        Assert.Equal(2, kinds.Count(k => k == FrameKind.Tick));
        Assert.Equal(6, kinds.Count(k => k == FrameKind.Redraw));
    }

    [Fact]
    public void Draw_only_frames_repeat_outside_the_world_and_in_movies() {
        FrameDirector d = new();
        Assert.Equal(FrameKind.Repeat, d.Next(World(1 / 1000.0, world: false), Defaults).Kind);
        Assert.Equal(FrameKind.Repeat, d.Next(World(1 / 1000.0, movie: true), Defaults).Kind);
        Assert.Equal(FrameKind.Repeat, d.Next(World(1 / 1000.0, vsyncs: 1), Defaults).Kind);
        Assert.Equal(FrameKind.Repeat, d.Next(World(1 / 1000.0), Defaults with { DrawOnly = DrawOnlyMode.Repeat }).Kind);
        Assert.Equal(FrameKind.Redraw, d.Next(World(1 / 1000.0), Defaults).Kind);
    }

    [Fact]
    public void The_main_menu_ticks_at_60() {
        FrameDirector d = new();
        int ticks = Enumerable.Range(0, 240).Sum(_ => d.Next(World(1 / 240.0, world: false, vsyncs: 1), Defaults).Plan.Ticks);
        Assert.Equal(60, ticks);
    }

    [Fact]
    public void An_excluded_scene_stays_at_1() {
        FrameDirector d = new();
        FrameSettings fast = Defaults with { TimeScale = 10 };
        int ticks = Enumerable.Range(0, 30).Sum(_ => d.Next(World(1 / 30.0, excluded: true), fast).Plan.Ticks);
        Assert.Equal(30, ticks);
        Assert.False(d.Next(World(1 / 30.0, excluded: true), fast).Mute);
    }

    [Fact]
    public void Fast_forward_mutes_and_uses_exec_frames_past_four_updates() {
        FrameDirector d = new();
        FrameDecision f = d.Next(World(1 / 30.0), Defaults with { TimeScale = 10 });
        Assert.Equal(FrameKind.Tick, f.Kind);
        Assert.Equal(10, f.Plan.Ticks);
        Assert.Equal(4, f.Plan.Loops);
        Assert.True(f.Mute);
        Assert.False(d.Next(World(1 / 30.0), Defaults with { TimeScale = 10, MuteWhenFast = false }).Mute);
    }

    [Fact]
    public void Times_100_at_144_fps_is_3000_ticks_a_second() {
        FrameDirector d = new();
        FrameSettings s = Defaults with { TimeScale = 100 };
        int ticks = Enumerable.Range(0, 144).Sum(_ => d.Next(World(1 / 144.0), s).Plan.Ticks);
        Assert.Equal(3000, ticks);
    }

    [Fact]
    public void The_booster_multiplies() {
        FrameDirector d = new();
        int ticks = Enumerable.Range(0, 30).Sum(_ => d.Next(World(1 / 30.0, booster: 2), Defaults).Plan.Ticks);
        Assert.Equal(60, ticks);
    }

    [Fact]
    public void Alpha_is_the_fraction_between_ticks() {
        FrameDirector d = new();
        Assert.Equal(0.25, d.Next(World(1 / 120.0), Defaults).Alpha, 6);
        Assert.Equal(0.5, d.Next(World(1 / 120.0), Defaults).Alpha, 6);
    }

    [Fact]
    public void Exclusion_reasons_in_order() {
        Assert.Null(TimeScaleRules.Excluded(false, false, false, true, false));
        Assert.Equal("voice-synced scene", TimeScaleRules.Excluded(true, true, true, false, true));
        Assert.Equal("movie", TimeScaleRules.Excluded(false, true, false, true, false));
        Assert.NotNull(TimeScaleRules.Excluded(false, false, false, false, false));
        Assert.Equal("the booster's exclusions", TimeScaleRules.Excluded(false, false, false, true, true));
    }
}

public class PacingTests {
    [Fact]
    public void Intervals() {
        Assert.Null(FramePacing.Interval(FpsTarget.Game, 120, 144));
        Assert.Equal(1 / 144.0, FramePacing.Interval(FpsTarget.Monitor, 120, 144)!.Value, 9);
        Assert.Equal(1 / 60.0, FramePacing.Interval(FpsTarget.Monitor, 120, 0)!.Value, 9);
        Assert.Equal(1 / 120.0, FramePacing.Interval(FpsTarget.Fixed, 120, 144)!.Value, 9);
        Assert.Equal(1 / 1000.0, FramePacing.Interval(FpsTarget.Fixed, 5000, 144)!.Value, 9);
        Assert.Equal(0, FramePacing.Interval(FpsTarget.Unlimited, 120, 144));
    }

    [Fact]
    public void The_limiter_keeps_the_games_operands_for_its_own_pacing() {
        Assert.Equal((2, 1001.0 / 60000), FramePacing.Limiter(null, 2));
        Assert.Equal((1, 1 / 144.0), FramePacing.Limiter(1 / 144.0, 2));
        Assert.Equal((1, 0.0), FramePacing.Limiter(0, 1));
    }

    [Fact]
    public void Time_scale_steps() {
        Assert.Equal(2, TimeScaleSteps.Up(1));
        Assert.Equal(0.5, TimeScaleSteps.Down(1));
        Assert.Equal(100, TimeScaleSteps.Up(100));
        Assert.Equal(0.05, TimeScaleSteps.Down(0.05));
        Assert.Equal(3, TimeScaleSteps.Up(2.5));
        Assert.Equal(10, TimeScaleSteps.Toggle(1, 10));
        Assert.Equal(1, TimeScaleSteps.Toggle(10, 10));
    }
}

public class SettingsTests {
    [Fact]
    public void Round_trip() {
        FrameSettings s = new() { Target = FpsTarget.Fixed, FixedFps = 165, Vsync = VsyncMode.Off, TimeScale = 2.5, DrawOnly = DrawOnlyMode.Repeat, Effects = EffectsMode.Hold, InterpolateBones = false };
        Assert.Equal(s, FrameSettings.FromJson(s.ToJson()));
        Assert.Contains("\"target\": \"fixed\"", s.ToJson());
    }

    [Fact]
    public void Empty_is_the_defaults_and_missing_keys_keep_theirs() {
        Assert.Equal(new FrameSettings(), FrameSettings.FromJson(""));
        Assert.Equal(new FrameSettings() with { TimeScale = 4 }, FrameSettings.FromJson("{ \"timeScale\": 4 }"));
    }

    [Fact]
    public void Out_of_range_is_clamped() {
        FrameSettings s = FrameSettings.FromJson("{ \"timeScale\": 1000, \"fixedFps\": 1, \"maxTicksPerFrame\": 0, \"capSeconds\": 9 }");
        Assert.Equal(100, s.TimeScale);
        Assert.Equal(10, s.FixedFps);
        Assert.Equal(1, s.MaxTicksPerFrame);
        Assert.Equal(2, s.CapSeconds);
    }

    [Fact]
    public void Not_json_throws() {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => FrameSettings.FromJson("[1, 2]"));
    }
}

public class StatsTests {
    [Fact]
    public void Rates_per_second() {
        FrameStats s = new();
        Assert.False(s.Sample(0));
        s.Add(Counter.Presents, 144);
        s.Add(Counter.WorldTicks, 30);
        Assert.False(s.Sample(0.5));
        Assert.True(s.Sample(1));
        Assert.Equal(144, s.Rate(Counter.Presents));
        Assert.Equal(30, s.Rate(Counter.WorldTicks));
        Assert.Equal(0, s.Rate(Counter.Updates));
    }
}
