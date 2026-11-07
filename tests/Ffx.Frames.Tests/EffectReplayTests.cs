// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Frames.Tests;

public class EffectGateTests {
    [Theory]
    [InlineData(EffectsMode.Replay, false, EffectAction.Run)]
    [InlineData(EffectsMode.Run, false, EffectAction.Run)]
    [InlineData(EffectsMode.Hold, false, EffectAction.Run)]
    [InlineData(EffectsMode.Replay, true, EffectAction.Replay)]
    [InlineData(EffectsMode.Run, true, EffectAction.Run)]
    [InlineData(EffectsMode.Hold, true, EffectAction.Hold)]
    public void Effects_run_on_ticks_and_follow_the_setting_between_them(EffectsMode mode, bool redraw, EffectAction expected) =>
        Assert.Equal(expected, EffectGate.Decide(mode, redraw));

    [Fact]
    public void The_loops_counters_step_only_when_the_effects_run() {
        Assert.True(EffectGate.RunLoopAfter(EffectsMode.Replay, redraw: false));
        Assert.False(EffectGate.RunLoopAfter(EffectsMode.Replay, redraw: true));
        Assert.False(EffectGate.RunLoopAfter(EffectsMode.Hold, redraw: true));
        Assert.True(EffectGate.RunLoopAfter(EffectsMode.Run, redraw: true));
    }

    [Fact]
    public void Replay_is_the_default() => Assert.Equal(EffectsMode.Replay, new FrameSettings().Effects);
}

public class EffectRecorderTests {
    private static EffectDraw Geo(int g) => new(EffectDrawKind.Register, g, 0x4000_0000, 4);

    [Fact]
    public void Nothing_is_kept_outside_a_slot() {
        EffectRecorder r = new();
        Assert.False(r.Add(Geo(1)));
        Assert.Equal(0, r.Count);
        r.Begin(EffectSlot.FieldParticles);
        Assert.True(r.Add(Geo(1)));
        r.End();
        Assert.False(r.Add(Geo(2)));
        Assert.Null(r.Recording);
        Assert.Equal([Geo(1)], r.Entries(EffectSlot.FieldParticles));
    }

    [Fact]
    public void Draws_keep_their_order_and_kinds() {
        EffectRecorder r = new();
        r.Begin(EffectSlot.OtDraw);
        r.Add(Geo(1));
        r.Add(new EffectDraw(EffectDrawKind.SyncPath));
        r.Add(new EffectDraw(EffectDrawKind.DrawEvent, 0x1000, E0: 2, E2: 0x7f7f7f7f));
        r.Add(Geo(2));
        r.End();
        Assert.Equal([EffectDrawKind.Register, EffectDrawKind.SyncPath, EffectDrawKind.DrawEvent, EffectDrawKind.Register],
            [.. System.Linq.Enumerable.Select(r.Entries(EffectSlot.OtDraw), d => d.Kind)]);
    }

    [Fact]
    public void A_slots_last_call_replaces_the_one_before() {
        // Exec frames above 1 run the draw loop several times in one update: the last pass is the one replayed.
        EffectRecorder r = new();
        r.Begin(EffectSlot.EffectsAfter);
        r.Add(Geo(1));
        r.Add(Geo(2));
        r.Begin(EffectSlot.EffectsAfter);
        r.Add(Geo(3));
        r.End();
        Assert.Equal([Geo(3)], r.Entries(EffectSlot.EffectsAfter));
    }

    [Fact]
    public void A_new_draw_pass_empties_the_draw_slots_but_not_the_update() {
        EffectRecorder r = new();
        r.Begin(EffectSlot.Update);
        r.Add(Geo(1));
        r.Begin(EffectSlot.FieldParticles);
        r.Add(Geo(2));
        r.End();
        r.BeginDrawPass();
        Assert.Equal([Geo(1)], r.Entries(EffectSlot.Update));
        Assert.Empty(r.Entries(EffectSlot.FieldParticles));
        r.Clear();
        Assert.Equal(0, r.Count);
    }
}

public class DynGeoSegmentTests {
    [Fact]
    public void The_buffers_slots_are_28_bytes_apart_and_fit_the_segment() {
        Assert.Equal(0x50, DynGeoSegment.At(0x34, 1));
        Assert.Equal(0x30, DynGeoSegment.At(DynGeoSegment.IndexCount, 0));
        // Both buffers' last word (the index array of buffer 1) lie inside the 0x6c-byte segment.
        Assert.True(DynGeoSegment.At(0x44, 1) + 4 <= DynGeoSegment.Size);
    }

    [Fact]
    public void The_five_arrays_are_positions_colours_normals_uvs_and_indices() {
        ReadOnlySpan<(int Offset, int Bytes, bool PerIndex)> a = DynGeoSegment.Arrays;
        Assert.Equal(5, a.Length);
        Assert.Equal((0x34, 12, false), a[0]);
        Assert.Equal((0x3c, 16, false), a[1]);
        Assert.Equal((0x38, 12, false), a[2]);
        Assert.Equal((0x40, 8, false), a[3]);
        Assert.Equal((0x44, 2, true), a[4]);
        Assert.Equal(12 * 7, DynGeoSegment.Bytes(a[0], 7, 9));
        Assert.Equal(2 * 9, DynGeoSegment.Bytes(a[4], 7, 9));
        Assert.Equal(0, DynGeoSegment.Bytes(a[4], 7, 0));
        Assert.Equal(0, DynGeoSegment.Bytes(a[0], -1, 9));
        Assert.Equal(0, DynGeoSegment.Bytes(a[0], DynGeoSegment.MaxCount + 1, 9));
    }

    [Theory]
    [InlineData(0, 2, 1)]
    [InlineData(1, 2, 0)]
    [InlineData(0, 3, 2)]
    [InlineData(2, 3, 1)]
    public void The_previous_buffer_is_the_one_before_in_the_flip_order(int current, int count, int previous) =>
        Assert.Equal(previous, DynGeoSegment.Previous(current, count));
}
