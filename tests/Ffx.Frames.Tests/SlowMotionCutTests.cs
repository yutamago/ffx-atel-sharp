// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ffx.Frames.Tests;

/// <summary>
///     Hard cuts in slow motion (2026-11-08, ×0.1, target Unlimited): the camera slid from the last shot to the new one
///     over the ten frames between two ticks. Each test drives the director's frames and the camera history as the hooks
///     do: a tick frame pushes the tick's camera, every frame draws <see cref="CameraHistory.TryAt"/> or the tick's own.
/// </summary>
public class SlowMotionCutTests {
    private static Matrix4x4 View(float x, float yaw = 0) {
        Vector3 eye = new(x, 0, -10);
        Vector3 dir = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw * MathF.PI / 180));
        return Matrix4x4.CreateLookAt(eye, eye + dir, Vector3.UnitY);
    }

    private static Vector3 Eye(Matrix4x4 view) {
        Matrix4x4.Invert(view, out Matrix4x4 world);
        return world.Translation;
    }

    /// <summary>One drawn frame: whether it ticked, its alpha and the camera it drew.</summary>
    private readonly record struct Drawn(bool Tick, double Alpha, Matrix4x4 Camera);

    /// <summary>
    ///     Frames at 300 fps and ×0.1 (a tick every 1/3 s real, about 100 frames), the camera of each tick from
    ///     <paramref name="tickCamera"/> (tick number → matrix, snapped), until <paramref name="ticks"/> ticks ran.
    /// </summary>
    private static List<Drawn> Run(Func<int, (Matrix4x4 Cam, bool Snapped)> tickCamera, int ticks, double scale = 0.1) {
        FrameDirector d = new();
        FrameSettings s = new() { Target = FpsTarget.Unlimited, TimeScale = scale };
        CameraHistory h = new();
        List<Drawn> frames = [];
        int tick = 0;
        while (tick < ticks) {
            FrameDecision f = d.Next(new FrameInput(1 / 300.0, 2, true, false, false, 1), s);
            bool ticked = f.Kind == FrameKind.Tick;
            if (ticked) {
                // Slow motion: one tick per frame at most.
                Assert.Equal(1, f.Plan.Ticks);
                (Matrix4x4 cam, bool snapped) = tickCamera(tick++);
                h.Push(cam, Matrix4x4.Identity, s.CutDistance, s.CutDegrees, snapped);
            }
            else Assert.Equal(FrameKind.Redraw, f.Kind);
            // Frames before the first tick have no camera yet.
            if (!h.TryLatest(out Matrix4x4 latest)) continue;
            frames.Add(new Drawn(ticked, f.Alpha, h.TryAt(f.Alpha, out Matrix4x4 cam2, out _) ? cam2 : latest));
        }
        // Up to the next tick.
        for (int i = 0; i < 200; i++) {
            FrameDecision f = d.Next(new FrameInput(1 / 300.0, 2, true, false, false, 1), s);
            if (f.Kind == FrameKind.Tick) break;
            Assert.True(h.TryLatest(out Matrix4x4 latest));
            frames.Add(new Drawn(false, f.Alpha, h.TryAt(f.Alpha, out Matrix4x4 cam2, out _) ? cam2 : latest));
        }
        return frames;
    }

    /// <summary>The frames from the <paramref name="n"/>-th tick (0-based) up to the next tick.</summary>
    private static List<Drawn> After(List<Drawn> frames, int n) {
        int seen = -1, start = -1;
        for (int i = 0; i < frames.Count; i++) {
            if (frames[i].Tick && ++seen == n) {
                start = i;
                break;
            }
        }
        Assert.True(start >= 0);
        int end = start + 1;
        while (end < frames.Count && !frames[end].Tick) end++;
        return frames.GetRange(start, end - start);
    }

    [Fact]
    public void A_small_hard_cut_the_script_set_jumps_on_every_frame_until_the_next_tick() {
        // Shot A held still, then the script sets shot B 30 units and 20° away: under the thresholds (100, 45°).
        List<Drawn> frames = Run(t => t < 3 ? (View(0), false) : (View(30, 20), t == 3), 5);
        List<Drawn> cut = After(frames, 3);
        Assert.True(cut.Count >= 8, $"{cut.Count} frames between the ticks");
        foreach (Drawn f in cut) Assert.Equal(View(30, 20), f.Camera);
        // The tick after the cut blends from the new shot, not from the old one.
        foreach (Drawn f in After(frames, 4)) Assert.Equal(30, Eye(f.Camera).X, 3);
    }

    [Fact]
    public void A_cut_past_the_thresholds_jumps_on_every_frame_without_a_signal() {
        List<Drawn> frames = Run(t => t < 3 ? (View(0), false) : (View(500), false), 4);
        foreach (Drawn f in After(frames, 3)) Assert.Equal(View(500), f.Camera);
    }

    [Fact]
    public void An_unsignalled_small_jump_is_interpolated_as_before() {
        // Without the game's signal a move under the thresholds is a move: the frames go from A to B.
        List<Drawn> frames = Run(t => t < 3 ? (View(0), false) : (View(30, 20), false), 4);
        List<Drawn> between = After(frames, 3);
        Assert.Contains(between, f => Eye(f.Camera).X is > 1 and < 29);
    }

    [Fact]
    public void A_script_moving_its_camera_every_tick_stays_smooth() {
        // CamSetPos on every tick, 2 units a tick: each tick signals a set, but the steps are even.
        List<Drawn> frames = Run(t => (View(2 * t), true), 6);
        List<Drawn> between = After(frames, 4);
        Assert.Contains(between, f => Eye(f.Camera).X is > 6.2f and < 7.8f);
    }

    [Fact]
    public void A_script_set_jumps_out_of_a_slow_pan() {
        // A pan of 0.3 units a tick, then a set 10 units on: a jump against the pan's step.
        List<Drawn> frames = Run(t => t < 4 ? (View(0.3f * t), true) : (View(0.3f * 3 + 10), true), 6);
        foreach (Drawn f in After(frames, 4)) Assert.Equal(View(0.3f * 3 + 10), f.Camera);
    }

    [Fact]
    public void Snap_jumps_compare_against_the_step_before() {
        Assert.True(CameraHistory.SnapJumps((30, 0), (0, 0), 1, 0, 0));
        Assert.True(CameraHistory.SnapJumps((0, 20), null, 1, 0, 0));
        Assert.False(CameraHistory.SnapJumps((2, 0), (2, 0), 1, 0, 0));
        Assert.False(CameraHistory.SnapJumps((0.2, 0.1), (0, 0), 1e-3, 0, 0));
        Assert.True(CameraHistory.SnapJumps((0, 0), (0, 0), 1e-5, 0.05, 0));
        Assert.False(CameraHistory.SnapJumps((0, 0), (0, 0), 1e-5, 0.05, 0.05));
    }
}
