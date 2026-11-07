// SPDX-License-Identifier: MIT

using System;
using System.Numerics;

namespace Ffx.Frames.Tests;

public class CameraTests {
    /// <summary>A view matrix (world to view, row vectors) for a camera at <paramref name="eye"/> looking at <paramref name="target"/>.</summary>
    private static Matrix4x4 View(Vector3 eye, Vector3 target) => Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);

    private static Vector3 EyeOf(Matrix4x4 view) {
        Assert.True(CameraInterpolator.TryDecompose(view, out CameraInterpolator.Pose p));
        return p.Eye;
    }

    private static void Near(Matrix4x4 a, Matrix4x4 b, double tol = 1e-4) =>
        Assert.True(CameraInterpolator.RelativeChange(a, b) < tol, $"{a}\n!=\n{b}");

    [Fact]
    public void Decompose_and_compose_round_trip() {
        Matrix4x4 v = View(new Vector3(10, 5, -30), new Vector3(0, 1, 0));
        Assert.True(CameraInterpolator.TryDecompose(v, out CameraInterpolator.Pose p));
        Near(v, CameraInterpolator.Compose(p));
        Assert.Equal(10, p.Eye.X, 3);
        Assert.Equal(5, p.Eye.Y, 3);
        Assert.Equal(-30, p.Eye.Z, 3);
    }

    [Fact]
    public void A_scaled_and_mirrored_camera_round_trips() {
        // The PS2 flips an axis and scales its matrices; the decomposition must keep both.
        Matrix4x4 v = View(new Vector3(-4, 2, 8), Vector3.Zero) * Matrix4x4.CreateScale(1, -1, 0.5f);
        Assert.True(CameraInterpolator.TryDecompose(v, out CameraInterpolator.Pose p));
        Near(v, CameraInterpolator.Compose(p));
        Near(v, CameraInterpolator.Camera(v, v, 0.5f));
    }

    [Fact]
    public void A_camera_scaled_on_the_worlds_axes_round_trips() {
        Matrix4x4 v = Matrix4x4.CreateScale(2, 1, 1) * View(new Vector3(-4, 2, 8), Vector3.Zero);
        Assert.True(CameraInterpolator.TryDecompose(v, out CameraInterpolator.Pose p));
        Assert.False(p.ScaleColumns);
        Near(v, CameraInterpolator.Compose(p));
    }

    [Fact]
    public void A_skewed_matrix_is_not_a_camera() {
        Matrix4x4 skew = Matrix4x4.Identity with { M21 = 0.5f };
        Assert.False(CameraInterpolator.TryDecompose(skew, out _));
    }

    [Fact]
    public void The_ends_are_exact() {
        Matrix4x4 a = View(new Vector3(0, 0, -10), Vector3.Zero), b = View(new Vector3(3, 0, -10), new Vector3(3, 0, 0));
        Assert.Equal(a, CameraInterpolator.Camera(a, b, 0));
        Assert.Equal(b, CameraInterpolator.Camera(a, b, 1));
    }

    [Fact]
    public void A_moving_camera_moves_halfway() {
        Matrix4x4 a = View(new Vector3(0, 0, -10), Vector3.Zero), b = View(new Vector3(4, 0, -10), new Vector3(4, 0, 0));
        Vector3 eye = EyeOf(CameraInterpolator.Camera(a, b, 0.5f));
        Assert.Equal(2, eye.X, 3);
        Assert.Equal(-10, eye.Z, 3);
    }

    [Fact]
    public void A_turning_camera_turns_instead_of_shrinking() {
        Vector3 eye = new(0, 0, 0);
        Matrix4x4 a = View(eye, new Vector3(0, 0, -1)), b = View(eye, new Vector3(1, 0, 0));
        Matrix4x4 mid = CameraInterpolator.Camera(a, b, 0.5f);
        Assert.True(CameraInterpolator.TryDecompose(mid, out CameraInterpolator.Pose p));
        // Still a rotation (scale 1), turned 45° from each end.
        Assert.Equal(1, p.Scale.Y, 4);
        Assert.Equal(1, Math.Abs(p.Scale.X), 4);
        Assert.Equal(45, CameraInterpolator.Step(a, mid)!.Value.Degrees, 2);
        Assert.Equal(45, CameraInterpolator.Step(mid, b)!.Value.Degrees, 2);
        // A matrix lerp would have shrunk the basis to about 0.707.
        Matrix4x4 lerp = Matrix4x4.Lerp(a, b, 0.5f);
        Assert.True(new Vector3(lerp.M11, lerp.M12, lerp.M13).Length() < 0.8f);
    }

    [Fact]
    public void Steps_measure_move_and_turn() {
        Matrix4x4 a = View(new Vector3(0, 0, -10), Vector3.Zero), b = View(new Vector3(0, 0, -15), Vector3.Zero);
        (double d, double deg) = CameraInterpolator.Step(a, b)!.Value;
        Assert.Equal(5, d, 3);
        Assert.Equal(0, deg, 2);
    }

    [Fact]
    public void A_projection_is_not_a_rigid_camera() {
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(1, 4 / 3f, 1, 1000);
        Assert.False(CameraInterpolator.TryDecompose(proj, out _));
        Assert.Null(CameraInterpolator.Step(proj, proj));
        // Lerped element by element instead.
        Near(Matrix4x4.Lerp(proj, Matrix4x4.Identity, 0.5f), CameraInterpolator.Camera(proj, Matrix4x4.Identity, 0.5f));
    }

    [Fact]
    public void Cuts() {
        Matrix4x4 scr = Matrix4x4.CreateScale(2);
        Matrix4x4 a = View(new Vector3(0, 0, -10), Vector3.Zero);
        Matrix4x4 near = View(new Vector3(1, 0, -10), new Vector3(1, 0, 0));
        Matrix4x4 far = View(new Vector3(500, 0, -10), new Vector3(500, 0, 0));
        Matrix4x4 turned = View(new Vector3(0, 0, -10), new Vector3(-100, 0, -10));
        Assert.False(CameraInterpolator.IsCut(a, scr, near, scr, 100, 45));
        Assert.True(CameraInterpolator.IsCut(a, scr, far, scr, 100, 45));
        Assert.True(CameraInterpolator.IsCut(a, scr, turned, scr, 100, 45));
        Assert.True(CameraInterpolator.IsCut(a, scr, near, Matrix4x4.CreateScale(3), 100, 45));
    }

    [Fact]
    public void History_interpolates_the_last_two_ticks_and_snaps_on_a_cut() {
        CameraHistory h = new();
        Matrix4x4 scr = Matrix4x4.Identity;
        Matrix4x4 a = View(new Vector3(0, 0, -10), Vector3.Zero), b = View(new Vector3(2, 0, -10), new Vector3(2, 0, 0));
        Assert.False(h.TryAt(0.5, out _, out _));
        h.Push(a, scr, 100, 45);
        Assert.False(h.TryAt(0.5, out Matrix4x4 only, out _));
        Assert.Equal(a, only);
        h.Push(b, scr, 100, 45);
        Assert.True(h.TryAt(0.5, out Matrix4x4 mid, out _));
        Assert.Equal(1, EyeOf(mid).X, 3);
        Assert.True(h.TryLatest(out Matrix4x4 latest));
        Assert.Equal(b, latest);
        Assert.Equal(2, h.LastStep!.Value.Distance, 3);

        Matrix4x4 cut = View(new Vector3(900, 0, -10), new Vector3(900, 0, 0));
        h.Push(cut, scr, 100, 45);
        Assert.True(h.Cut);
        Assert.Equal(1, h.Cuts);
        Assert.False(h.TryAt(0.5, out Matrix4x4 snapped, out _));
        Assert.Equal(cut, snapped);

        h.Push(cut, scr, 100, 45);
        Assert.False(h.Cut);
        Assert.True(h.TryAt(0.3, out _, out _));
        h.Clear();
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void The_screen_matrix_lerps() {
        Matrix4x4 a = Matrix4x4.CreateScale(2), b = Matrix4x4.CreateScale(4);
        Assert.Equal(Matrix4x4.CreateScale(3), CameraInterpolator.Screen(a, b, 0.5f));
    }
}
