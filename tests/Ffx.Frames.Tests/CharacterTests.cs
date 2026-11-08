// SPDX-License-Identifier: MIT

using System;
using System.Numerics;

namespace Ffx.Frames.Tests;

public class TransformInterpolatorTests {
    private static Matrix4x4 At(float x, float y, float z, float yawDegrees = 0, float scale = 1) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(yawDegrees * MathF.PI / 180) * Matrix4x4.CreateTranslation(x, y, z);

    private static void Near(Matrix4x4 a, Matrix4x4 b, double tol = 1e-4) =>
        Assert.True(CameraInterpolator.RelativeChange(a, b) < tol, $"{a}\n!=\n{b}");

    [Fact]
    public void A_character_matrix_round_trips() {
        Matrix4x4 m = At(120, -3, 45, 30, 1000);
        TransformInterpolator.Pose p = TransformInterpolator.Decompose(m);
        Assert.True(p.Ok);
        Near(m, TransformInterpolator.Compose(p));
    }

    [Fact]
    public void A_turn_is_slerped_and_a_move_lerped() {
        Matrix4x4 a = At(0, 0, 0, 0), b = At(10, 0, 0, 90);
        Near(At(5, 0, 0, 45), TransformInterpolator.Interpolate(a, b, 0.5f));
        Near(At(2.5f, 0, 0, 22.5f), TransformInterpolator.Interpolate(a, b, 0.25f));
    }

    [Fact]
    public void A_slerp_keeps_the_size_where_a_lerp_shrinks() {
        Matrix4x4 a = At(0, 0, 0, 0), b = At(0, 0, 0, 90);
        Matrix4x4 mid = TransformInterpolator.Interpolate(a, b, 0.5f);
        Assert.Equal(1, new Vector3(mid.M11, mid.M12, mid.M13).Length(), 4);
        Matrix4x4 lerp = Matrix4x4.Lerp(a, b, 0.5f);
        Assert.True(new Vector3(lerp.M11, lerp.M12, lerp.M13).Length() < 0.75f);
    }

    [Fact]
    public void The_ends_are_exact_and_t_is_clamped() {
        Matrix4x4 a = At(1, 2, 3, 10), b = At(4, 5, 6, 20);
        Assert.Equal(a, TransformInterpolator.Interpolate(a, b, 0));
        Assert.Equal(b, TransformInterpolator.Interpolate(a, b, 1));
        Assert.Equal(a, TransformInterpolator.Interpolate(a, b, -1));
        Assert.Equal(b, TransformInterpolator.Interpolate(a, b, 2));
    }

    [Fact]
    public void A_matrix_that_is_not_affine_is_lerped_element_by_element() {
        Matrix4x4 a = Matrix4x4.CreatePerspectiveFieldOfView(1, 1, 1, 100), b = Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 1, 1, 100);
        Assert.False(TransformInterpolator.Decompose(a).Ok);
        Assert.Equal(Matrix4x4.Lerp(a, b, 0.5f), TransformInterpolator.Interpolate(a, b, 0.5f));
    }

    [Fact]
    public void A_skewed_matrix_cannot_be_taken_apart() =>
        Assert.False(TransformInterpolator.Decompose(Matrix4x4.Identity with { M21 = 0.5f }).Ok);

    [Fact]
    public void Warps_are_long_moves_big_turns_and_flips() {
        Matrix4x4 a = At(0, 0, 0);
        Assert.False(TransformInterpolator.IsCut(a, At(3, 0, 1, 10), 50, 120));
        Assert.True(TransformInterpolator.IsCut(a, At(80, 0, 0), 50, 120));
        Assert.True(TransformInterpolator.IsCut(a, At(0, 0, 0, 150), 50, 120));
        Assert.True(TransformInterpolator.IsCut(a, Matrix4x4.CreateScale(-1, 1, 1), 50, 120));
        Assert.Equal(80, TransformInterpolator.Step(a, At(80, 0, 0))!.Value.Distance, 3);
    }
}

public class CharacterDefaultsTests {
    [Fact]
    public void Character_interpolation_is_on_since_its_check_in_the_game() {
        FrameSettings s = new();
        Assert.True(s.InterpolateCharacters);
        Assert.True(s.InterpolateBones);
        Assert.True(FrameSettings.FromJson("{ \"interpolate\": true }").InterpolateCharacters);
        Assert.False(FrameSettings.FromJson("{ \"interpolateCharacters\": false }").InterpolateCharacters);
    }
}

public class CharacterHistoryTests {
    private const double Dist = 50, Deg = 120;
    private static readonly ulong Tidus = CharacterHistory.Identity(1, 0x1000);
    private static Matrix4x4 At(float x, float yaw = 0) => Matrix4x4.CreateRotationY(yaw * MathF.PI / 180) * Matrix4x4.CreateTranslation(x, 0, 0);

    private static CharacterHistory Two(Matrix4x4 a, Matrix4x4 b, nint key = 0x100, ulong? id = null) {
        CharacterHistory h = new();
        h.BeginTick();
        h.PushRoot(key, id ?? Tidus, a, Dist, Deg);
        h.BeginTick();
        h.PushRoot(key, id ?? Tidus, b, Dist, Deg);
        return h;
    }

    [Fact]
    public void One_tick_is_drawn_as_it_is() {
        CharacterHistory h = new();
        h.BeginTick();
        h.PushRoot(0x100, Tidus, At(0), Dist, Deg);
        Assert.False(h.TryRoot(0x100, Tidus, 0.5, out _));
        Assert.True(h.TryLatestRoot(0x100, Tidus, out Matrix4x4 m));
        Assert.Equal(At(0), m);
    }

    [Fact]
    public void Two_consecutive_ticks_are_interpolated() {
        CharacterHistory h = Two(At(0), At(10));
        Assert.True(h.TryRoot(0x100, Tidus, 0.3, out Matrix4x4 m));
        Assert.Equal(3, m.M41, 4);
        Assert.False(h.RootCut(0x100));
    }

    [Fact]
    public void A_character_missing_from_a_tick_starts_again() {
        CharacterHistory h = new();
        h.BeginTick();
        h.PushRoot(0x100, Tidus, At(0), Dist, Deg);
        h.BeginTick();
        h.BeginTick();
        h.PushRoot(0x100, Tidus, At(10), Dist, Deg);
        Assert.False(h.TryRoot(0x100, Tidus, 0.5, out _));
    }

    [Fact]
    public void A_character_not_updated_at_the_last_tick_is_not_interpolated() {
        CharacterHistory h = Two(At(0), At(10));
        h.BeginTick();
        Assert.False(h.TryRoot(0x100, Tidus, 0.5, out _));
        Assert.False(h.TryLatestRoot(0x100, Tidus, out _));
    }

    [Fact]
    public void Another_character_in_the_slot_or_a_model_swap_starts_again() {
        CharacterHistory h = new();
        h.BeginTick();
        h.PushRoot(0x100, Tidus, At(0), Dist, Deg);
        h.BeginTick();
        ulong yuna = CharacterHistory.Identity(2, 0x1000);
        h.PushRoot(0x100, yuna, At(1), Dist, Deg);
        Assert.False(h.TryRoot(0x100, yuna, 0.5, out _));
        Assert.False(h.TryRoot(0x100, Tidus, 0.5, out _));
    }

    [Fact]
    public void A_warp_is_drawn_as_it_is_and_counted() {
        CharacterHistory h = Two(At(0), At(500));
        Assert.False(h.TryRoot(0x100, Tidus, 0.5, out _));
        Assert.True(h.RootCut(0x100));
        Assert.Equal(1, h.Cuts);
    }

    [Fact]
    public void Prune_drops_what_was_not_seen_and_clear_drops_all() {
        CharacterHistory h = Two(At(0), At(1));
        h.BeginTick();
        h.PushRoot(0x200, Tidus, At(0), Dist, Deg);
        h.Prune();
        Assert.Equal(2, h.Characters);
        h.BeginTick();
        h.PushRoot(0x200, Tidus, At(0), Dist, Deg);
        h.Prune();
        Assert.Equal(1, h.Characters);
        h.Clear();
        Assert.Equal(0, h.Characters);
    }

    [Fact]
    public void Bones_are_interpolated_between_consecutive_ticks() {
        CharacterHistory h = new();
        ulong id = CharacterHistory.Identity(0x5000, 2);
        Matrix4x4[] a = [At(0), At(0, 0)], b = [At(4), At(0, 60)];
        h.BeginTick();
        h.PushBones(0x300, id, a);
        Span<Matrix4x4> dest = new Matrix4x4[2];
        Assert.False(h.TryBones(0x300, id, 0.5, dest));
        h.BeginTick();
        h.PushBones(0x300, id, b);
        Assert.True(h.TryBones(0x300, id, 0.5, dest));
        Assert.Equal(2, dest[0].M41, 4);
        Assert.True(CameraInterpolator.RelativeChange(At(0, 30), dest[1]) < 1e-4);
        Assert.True(h.TryLatestBones(0x300, id, dest));
        Assert.Equal(b[1], dest[1]);
    }

    [Fact]
    public void Another_bone_count_or_array_starts_again() {
        CharacterHistory h = new();
        ulong id = CharacterHistory.Identity(0x5000, 1), other = CharacterHistory.Identity(0x6000, 1);
        h.BeginTick();
        h.PushBones(0x300, id, [At(0)]);
        h.BeginTick();
        h.PushBones(0x300, other, [At(1)]);
        Assert.False(h.TryBones(0x300, other, 0.5, new Matrix4x4[1]));
        Assert.False(h.TryBones(0x300, other, 0.5, new Matrix4x4[2]));
    }

    [Fact]
    public void A_second_push_in_a_tick_replaces_the_first() {
        // The main and the shadow instance list can hold the same instance.
        CharacterHistory h = new();
        ulong id = CharacterHistory.Identity(0x5000, 1);
        h.BeginTick();
        h.PushBones(0x300, id, [At(0)]);
        h.BeginTick();
        h.PushBones(0x300, id, [At(8)]);
        h.PushBones(0x300, id, [At(10)]);
        Span<Matrix4x4> dest = new Matrix4x4[1];
        Assert.True(h.TryBones(0x300, id, 0.5, dest));
        Assert.Equal(5, dest[0].M41, 4);
    }
}
