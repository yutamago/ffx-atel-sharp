// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace Ffx.Frames.Tests;

/// <summary>
///     The title's water and the dream noise step their counters in the draw, once per <c>Sg_MainLoop</c>. Modelled as
///     the game does it: <c>PhyreScene_drawMainMenuWaveEffect</c> draws <c>Water_[frame]</c>, then steps the frame (0..69) and
///     a byte count; <c>graphicDrawDream</c> draws its speckles when the cycle is 0, then steps it (0, 1, 2).
/// </summary>
public class DrawCounterTests {
    /// <summary>The bytes as the mod reads them: water frame, water count, the dream cycle (16 bits).</summary>
    private sealed class Model {
        public readonly byte[] Memory = new byte[4];

        /// <summary>One <c>Sg_MainLoop</c>'s draw: what it showed (water frame, speckles).</summary>
        public (int Water, bool Speckles) Draw() {
            int water = Memory[0];
            Memory[0] = (byte)(water + 1 < 0x46 ? water + 1 : 0);
            Memory[1]++;
            int cycle = BitConverter.ToInt16(Memory, 2);
            bool speckles = cycle == 0;
            BitConverter.GetBytes((short)(cycle == 2 ? 0 : cycle + 1)).CopyTo(Memory, 2);
            return (water, speckles);
        }
    }

    /// <summary>The title at <paramref name="fps"/> (30 Hz world, Redraw between ticks), for <paramref name="seconds"/>.</summary>
    private static List<(FrameKind Kind, int Water, bool Speckles)> Title(double fps, double seconds, bool hold) {
        Model game = new();
        DrawCounters counters = new(game.Memory.Length);
        FrameDirector d = new();
        List<(FrameKind, int, bool)> drawn = [];
        for (int i = 0; i < (int)(fps * seconds); i++) {
            FrameDecision f = d.Next(new FrameInput(1 / fps, 2, true, false, false), new FrameSettings());
            int updates = f.Kind == FrameKind.Tick ? f.Plan.Loops : 1;
            for (int u = 0; u < updates; u++) {
                if (hold && f.Kind == FrameKind.Redraw) counters.BeginRedraw(game.Memory);
                else if (hold) counters.BeginTick(game.Memory);
                (int water, bool speckles) = game.Draw();
                if (hold && f.Kind == FrameKind.Redraw) counters.EndRedraw(game.Memory);
                if (u == updates - 1) drawn.Add((f.Kind, water, speckles));
            }
        }
        return drawn;
    }

    [Fact]
    public void Without_the_hold_the_water_runs_at_the_frame_rate() {
        // What main did: at 200 fps the 70 frames went round in 0.35 s instead of 2.3 s.
        var drawn = Title(200, 1, hold: false);
        Assert.Equal(200 % 70, drawn[^1].Water + 1);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(200)]
    [InlineData(1000)]
    public void The_water_steps_once_a_tick_whatever_the_frame_rate(double fps) {
        var drawn = Title(fps, 2, hold: true);
        int ticks = drawn.Count(x => x.Kind == FrameKind.Tick);
        Assert.InRange(ticks, 59, 61);
        // Every frame shows the frame of the tick before it: the water steps 30 times a second.
        int last = -1;
        foreach ((FrameKind kind, int water, _) in drawn) {
            if (kind == FrameKind.Tick) {
                Assert.Equal(last < 0 ? 0 : (last + 1) % 0x46, water);
                last = water;
            }
            else if (last >= 0) Assert.Equal(last, water);
        }
        Assert.Equal((ticks - 1) % 0x46, last);
    }

    [Fact]
    public void The_dream_noise_keeps_its_cadence() {
        var drawn = Title(200, 2, hold: true);
        bool tickSpeckles = false;
        int tick = 0;
        foreach ((FrameKind kind, _, bool speckles) in drawn) {
            if (kind == FrameKind.Tick) {
                Assert.Equal(tick++ % 3 == 0, speckles);
                tickSpeckles = speckles;
            }
            else if (tick > 0) Assert.Equal(tickSpeckles, speckles);
        }
    }

    [Fact]
    public void A_redraw_before_any_tick_holds_the_counters() {
        DrawCounters c = new(4);
        byte[] memory = [5, 6, 1, 0];
        c.BeginRedraw(memory);
        Assert.Equal([5, 6, 1, 0], memory);
        memory[0] = 9;
        c.EndRedraw(memory);
        Assert.Equal([5, 6, 1, 0], memory);
        Assert.False(c.HaveTick);
        c.BeginTick([1, 2, 3, 4]);
        c.Clear();
        Assert.False(c.HaveTick);
    }
}
