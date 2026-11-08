// SPDX-License-Identifier: MIT

using System;
using System.Numerics;

namespace Ffx.Frames.Tests;

/// <summary>The cloth's matrix taken apart and put back, and the history that blends the cloth between ticks.</summary>
public class ClothMatrixTests {
    private static Matrix4x4 Root(float x, float z, float yawDegrees) =>
        Matrix4x4.CreateRotationY(yawDegrees * MathF.PI / 180) * Matrix4x4.CreateTranslation(x, 0, z);

    // A base element's global matrix as Ch_UpdateClothMatrix uses it: a turn, the translation × 1000.
    private static readonly Matrix4x4 Element = Matrix4x4.CreateRotationX(0.3f) * Matrix4x4.CreateTranslation(0, 900, 20);

    private static void Near(Matrix4x4 a, Matrix4x4 b, double tol = 1e-4) =>
        Assert.True(CameraInterpolator.RelativeChange(a, b) < tol, $"{a}\n!=\n{b}");

    private static void Near(Vector3 a, Vector3 b, float tol = 1e-2f) =>
        Assert.True(Vector3.Distance(a, b) < tol, $"{a} != {b}");

    /// <summary>
    ///     <c>Ch_UpdateClothMatrix</c>'s product, written as the game's <c>mul4x4(a, b)</c> (which is <c>b × a</c>):
    ///     <c>m = mul4x4(root, element)</c>, then <c>mul4x4(flip, m)</c>.
    /// </summary>
    private static Matrix4x4 GameClothWorld(Matrix4x4 element, Matrix4x4 root) {
        static Matrix4x4 Mul4x4(Matrix4x4 a, Matrix4x4 b) => b * a;
        return Mul4x4(ClothHistory.Flip, Mul4x4(root, element));
    }

    [Fact]
    public void The_local_matrix_is_the_base_element() {
        Matrix4x4 root = Root(120, -40, 35);
        Matrix4x4 w = GameClothWorld(Element, root);
        Assert.True(ClothHistory.Local(w, root, out Matrix4x4 local));
        Near(Element, local);
        Near(w, ClothHistory.World(local, root));
    }

    [Fact]
    public void The_flip_is_its_own_inverse() => Assert.Equal(Matrix4x4.Identity, ClothHistory.Flip * ClothHistory.Flip);

    [Fact]
    public void Cloth_carried_with_the_drawn_character_lands_where_the_game_would_draw_it_there() {
        Matrix4x4 truth = Root(0, 0, 0), drawn = Root(5, 2, 20);
        Matrix4x4 w = GameClothWorld(Element, truth);
        Assert.True(ClothHistory.Local(w, truth, out Matrix4x4 local));
        Vector3 v = new(12, -30, 4);
        Near(Vector3.Transform(v, GameClothWorld(Element, drawn)), Vector3.Transform(v, ClothHistory.World(local, drawn)));
    }

    [Fact]
    public void A_character_matrix_without_an_inverse_gives_no_local_matrix() =>
        Assert.False(ClothHistory.Local(Matrix4x4.Identity, new Matrix4x4(), out _));
}

public class ClothHistoryTests {
    private const nint Hair = 0x2000;
    private static readonly ulong Buffer = CharacterHistory.Identity(0x5000, 3);
    private static readonly Matrix4x4 A = Matrix4x4.CreateTranslation(0, 900, 0), B = Matrix4x4.CreateTranslation(10, 900, 0);
    private static readonly Vector4[] Va = [new(0, 0, 0, 1), new(10, 0, 0, 1), new(5, 5, 5, 1)];
    private static readonly Vector4[] Vb = [new(2, 0, 0, 1), new(10, 0, 0, 1), new(5, 9, 5, 1)];

    private static ClothHistory Two(int first = 1, int second = 2, ulong? id = null) {
        ClothHistory h = new();
        h.Push(Hair, Buffer, first, A, Va);
        h.Push(Hair, id ?? Buffer, second, B, Vb);
        return h;
    }

    [Fact]
    public void Two_consecutive_ticks_blend() {
        ClothHistory h = Two();
        Vector4[] got = new Vector4[3];
        Assert.True(h.TryBlend(Hair, Buffer, 2, 0.25, out Matrix4x4 local, got));
        Assert.Equal(new Vector4(0.5f, 0, 0, 1), got[0]);
        Assert.Equal(new Vector4(5, 6, 5, 1), got[2]);
        Assert.Equal(2.5f, local.M41, 4);
        Assert.Equal(900f, local.M42, 3);
    }

    [Fact]
    public void A_vertex_that_did_not_move_stays_exactly() {
        ClothHistory h = Two();
        Vector4[] got = new Vector4[3];
        Assert.True(h.TryBlend(Hair, Buffer, 2, 0.37, out _, got));
        Assert.Equal(Va[1], got[1]);
    }

    [Fact]
    public void The_ends_are_the_ticks() {
        ClothHistory h = Two();
        Vector4[] got = new Vector4[3];
        Assert.True(h.TryBlend(Hair, Buffer, 2, 0, out Matrix4x4 l0, got));
        Assert.Equal(Va, got);
        Assert.Equal(A, l0);
        Assert.True(h.TryBlend(Hair, Buffer, 2, 1, out Matrix4x4 l1, got));
        Assert.Equal(Vb, got);
        Assert.Equal(B, l1);
    }

    [Fact]
    public void A_gap_between_ticks_is_drawn_as_it_is() {
        Assert.False(Two(1, 3).TryBlend(Hair, Buffer, 3, 0.5, out _, new Vector4[3]));
    }

    [Fact]
    public void Another_buffer_starts_again() {
        ulong other = CharacterHistory.Identity(0x6000, 3);
        ClothHistory h = Two(id: other);
        Assert.False(h.TryBlend(Hair, other, 2, 0.5, out _, new Vector4[3]));
        Assert.False(h.TryBlend(Hair, Buffer, 2, 0.5, out _, new Vector4[3]));
    }

    [Fact]
    public void Another_count_starts_again() {
        ClothHistory h = new();
        h.Push(Hair, Buffer, 1, A, Va);
        h.Push(Hair, Buffer, 2, B, Vb.AsSpan(0, 2));
        Assert.False(h.TryBlend(Hair, Buffer, 2, 0.5, out _, new Vector4[2]));
    }

    [Fact]
    public void An_old_sample_is_not_blended_at_a_later_tick() {
        Assert.False(Two().TryBlend(Hair, Buffer, 3, 0.5, out _, new Vector4[3]));
    }

    [Fact]
    public void A_second_push_in_the_same_tick_replaces_the_first() {
        ClothHistory h = Two();
        h.Push(Hair, Buffer, 2, A, Va);
        Vector4[] got = new Vector4[3];
        Assert.True(h.TryBlend(Hair, Buffer, 2, 0.5, out Matrix4x4 local, got));
        Assert.Equal(Va, got);
        Assert.Equal(A, local);
    }

    [Fact]
    public void The_latest_is_the_tick_s_own() {
        ClothHistory h = Two();
        Vector4[] got = new Vector4[3];
        Assert.True(h.TryLatest(Hair, Buffer, out Matrix4x4 local, got));
        Assert.Equal(Vb, got);
        Assert.Equal(B, local);
        Assert.False(h.TryLatest(Hair, CharacterHistory.Identity(1, 3), out _, got));
    }

    [Fact]
    public void Meshes_not_seen_for_two_ticks_are_dropped() {
        ClothHistory h = Two();
        h.Prune(3);
        Assert.Equal(1, h.Meshes);
        h.Prune(4);
        Assert.Equal(0, h.Meshes);
    }

    [Fact]
    public void Clear_forgets_everything() {
        ClothHistory h = Two();
        h.Clear();
        Assert.Equal(0, h.Meshes);
        Assert.False(h.TryLatest(Hair, Buffer, out _, new Vector4[3]));
    }
}

public class ClothDefaultsTests {
    [Fact]
    public void The_cloth_follows_the_characters_by_default() {
        FrameSettings s = new();
        Assert.True(s.InterpolateCloth);
        Assert.True(s.HoldCloth);
        Assert.False(FrameSettings.FromJson(new FrameSettings { InterpolateCloth = false }.ToJson()).InterpolateCloth);
        Assert.True(FrameSettings.FromJson("{ \"interpolateCharacters\": true }").InterpolateCloth);
    }
}
