// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ffx.Frames.Tests;

/// <summary>
///     The crash of 2026-11-08 (the opening's flashback, target Game): at 30 fps the mod still drew the characters
///     "between ticks" at the accumulator's left-over, and a camera cut then wrote through an instance list an earlier
///     update had logged and the game had since freed.
/// </summary>
public class PassThroughTests {
    private static FrameInput At30(double dt) => new(dt, 2, true, false, false, 1);

    [Fact]
    public void At_the_game_s_30_fps_every_frame_is_the_tick_itself() {
        FrameDirector d = new();
        FrameSettings s = new() { Target = FpsTarget.Game };
        // The limiter's jitter: frames a little short and long of a tick.
        foreach (double dt in new[] { 1 / 30.0, 0.0331, 0.0336, 0.0329, 0.034, 1 / 30.0 }) {
            FrameDecision f = d.Next(At30(dt), s);
            Assert.Equal(1, f.Alpha);
        }
    }

    [Fact]
    public void Slow_motion_at_30_fps_still_draws_between_ticks() {
        FrameDirector d = new();
        FrameSettings s = new() { Target = FpsTarget.Game, TimeScale = 0.25 };
        FrameDecision f = d.Next(At30(1 / 30.0), s);
        Assert.True(f.Alpha < 1);
    }

    [Fact]
    public void Other_targets_draw_between_ticks() {
        Assert.False(FrameDirector.DrawsBetweenTicks(new FrameSettings { Target = FpsTarget.Game }, 1));
        Assert.False(FrameDirector.DrawsBetweenTicks(new FrameSettings { Target = FpsTarget.Game }, 10));
        Assert.True(FrameDirector.DrawsBetweenTicks(new FrameSettings { Target = FpsTarget.Monitor }, 1));
        Assert.True(FrameDirector.DrawsBetweenTicks(new FrameSettings { Target = FpsTarget.Unlimited }, 1));
    }
}

public class WorldMatrixLogTests {
    private static readonly Matrix4x4 Truth = Matrix4x4.CreateTranslation(1, 2, 3);
    private static WorldMatrixLog.Entry Tidus(int model = -1) => new(0x29b060b0, Truth, model, 1, 0x146aec00);

    [Fact]
    public void An_entry_is_replayed_in_the_update_that_logged_it() {
        WorldMatrixLog log = new();
        log.BeginUpdate(7);
        log.Add(7, Tidus());
        List<WorldMatrixLog.Entry> got = [];
        Assert.Equal(1, log.Take(7, _ => true, got));
        Assert.Equal(Truth, got[0].Truth);
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void An_update_without_Ch_DrawMain_replays_nothing_from_the_one_before() {
        // The crash: Ch_DrawMain logged in update 7; update 8 (the scene change) did not run it, and its camera cut.
        WorldMatrixLog log = new();
        log.BeginUpdate(7);
        log.Add(7, Tidus());
        log.BeginUpdate(8);
        List<WorldMatrixLog.Entry> got = [];
        Assert.Equal(0, log.Take(8, _ => true, got));
        Assert.Empty(got);
    }

    [Fact]
    public void An_entry_for_another_update_is_not_logged() {
        WorldMatrixLog log = new();
        log.BeginUpdate(8);
        log.Add(7, Tidus());
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void A_list_that_is_no_longer_intact_is_left_alone() {
        WorldMatrixLog log = new();
        log.BeginUpdate(1);
        log.Add(1, Tidus());
        log.Add(1, Tidus(model: 2) with { List = 0x1000 });
        List<WorldMatrixLog.Entry> got = [];
        Assert.Equal(1, log.Take(1, e => e.List != 0x1000, got));
        Assert.Equal(-1, got[0].Model);
    }

    [Fact]
    public void A_list_with_a_null_instance_is_not_intact() {
        // As in the dump: [list + 4][0] == 0, which setMeshInstanceWorldMatrix reads at +4.
        WorldMatrixLog.Entry e = Tidus();
        Assert.False(WorldMatrixLog.Intact(e, 1, 0x146aec00, _ => 0));
        Assert.True(WorldMatrixLog.Intact(e, 1, 0x146aec00, _ => 0x22000000));
    }

    [Fact]
    public void A_list_whose_count_or_array_changed_is_not_intact() {
        WorldMatrixLog.Entry e = Tidus();
        Assert.False(WorldMatrixLog.Intact(e, 2, 0x146aec00, _ => 0x22000000));
        Assert.False(WorldMatrixLog.Intact(e, 1, 0x146aed00, _ => 0x22000000));
        Assert.False(WorldMatrixLog.Intact(e, 0, 0x146aec00, _ => 0x22000000));
        Assert.False(WorldMatrixLog.Intact(e with { List = 0 }, 1, 0x146aec00, _ => 0x22000000));
        // The count word's top bit is a flag, not part of the count.
        Assert.True(WorldMatrixLog.Intact(e, 0x80000001, 0x146aec00, _ => 0x22000000));
    }

    [Fact]
    public void A_list_too_long_to_be_a_character_s_is_not_intact() {
        WorldMatrixLog.Entry e = Tidus() with { Count = 500 };
        Assert.False(WorldMatrixLog.Intact(e, 500, 0x146aec00, _ => 0x22000000));
    }
}
