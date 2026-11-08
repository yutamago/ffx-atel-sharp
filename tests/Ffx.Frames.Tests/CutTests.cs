// SPDX-License-Identifier: MIT

using System;
using System.Numerics;

namespace Ffx.Frames.Tests;

/// <summary>A camera cut (a new shot): the camera, every character and every cloth mesh are drawn as the tick has them.</summary>
public class CameraSnapTests {
    private static Matrix4x4 View(float x) => Matrix4x4.CreateLookAt(new Vector3(x, 0, -10), new Vector3(x, 0, 0), Vector3.UnitY);

    [Fact]
    public void A_small_move_the_game_set_at_once_is_a_cut() {
        CameraHistory h = new();
        h.Push(View(0), Matrix4x4.Identity, 100, 45);
        h.Push(View(3), Matrix4x4.Identity, 100, 45, snapped: true);
        Assert.True(h.Cut);
        Assert.Equal(1, h.Cuts);
        Assert.Equal(1, h.SnapCuts);
        Assert.False(h.TryAt(0.5, out Matrix4x4 drawn, out _));
        Assert.Equal(View(3), drawn);
    }

    [Fact]
    public void The_same_small_move_otherwise_is_interpolated() {
        CameraHistory h = new();
        h.Push(View(0), Matrix4x4.Identity, 100, 45);
        h.Push(View(3), Matrix4x4.Identity, 100, 45);
        Assert.False(h.Cut);
        Assert.True(h.TryAt(0.5, out _, out _));
    }

    [Fact]
    public void A_set_to_where_the_camera_already_was_is_not_a_cut() {
        CameraHistory h = new();
        h.Push(View(3), Matrix4x4.Identity, 100, 45);
        h.Push(View(3), Matrix4x4.Identity, 100, 45, snapped: true);
        Assert.False(h.Cut);
        Assert.Equal(0, h.SnapCuts);
    }

    [Fact]
    public void A_snapped_screen_change_is_a_cut() {
        CameraHistory h = new();
        h.Push(View(3), Matrix4x4.Identity, 100, 45);
        h.Push(View(3), Matrix4x4.CreateScale(1.01f, 1, 1), 100, 45, snapped: true);
        Assert.True(h.Cut);
    }

    [Fact]
    public void A_cut_past_the_thresholds_is_not_counted_as_the_game_s() {
        CameraHistory h = new();
        h.Push(View(0), Matrix4x4.Identity, 100, 45);
        h.Push(View(900), Matrix4x4.Identity, 100, 45, snapped: true);
        Assert.True(h.Cut);
        Assert.Equal(0, h.SnapCuts);
    }

    [Fact]
    public void The_game_s_sets_are_cuts_by_default() => Assert.True(new FrameSettings().ScriptCameraCuts);
}

public class CutAllTests {
    private const double Dist = 50, Deg = 120;
    private static readonly ulong Tidus = CharacterHistory.Identity(1, 0x1000), Bones = CharacterHistory.Identity(0x7000, 1);
    private static Matrix4x4 At(float x) => Matrix4x4.CreateTranslation(x, 0, 0);

    private static CharacterHistory Tick(CharacterHistory h, float x) {
        h.BeginTick();
        h.PushRoot(0x100, Tidus, At(x), Dist, Deg);
        h.PushBones(0x300, Bones, [At(x)]);
        return h;
    }

    [Fact]
    public void After_a_cut_every_character_is_drawn_as_the_tick_has_it() {
        CharacterHistory h = Tick(Tick(new CharacterHistory(), 0), 10);
        Assert.True(h.TryRoot(0x100, Tidus, 0.5, out _));
        h.CutAll();
        Assert.True(h.AllCut);
        Assert.True(h.RootCut(0x100));
        Assert.False(h.TryRoot(0x100, Tidus, 0.5, out _));
        Assert.False(h.TryBones(0x300, Bones, 0.5, new Matrix4x4[1]));
        Matrix4x4[] latest = new Matrix4x4[1];
        Assert.True(h.TryLatestBones(0x300, Bones, latest));
        Assert.Equal(At(10), latest[0]);
        Assert.True(h.TryLatestRoot(0x100, Tidus, out Matrix4x4 root));
        Assert.Equal(At(10), root);
        Assert.Equal(1, h.AllCuts);
    }

    [Fact]
    public void The_next_tick_blends_from_the_cut_s_tick() {
        CharacterHistory h = Tick(Tick(new CharacterHistory(), 0), 10);
        h.CutAll();
        Tick(h, 12);
        Assert.False(h.AllCut);
        Assert.True(h.TryRoot(0x100, Tidus, 0.5, out Matrix4x4 m));
        Assert.Equal(11, m.M41, 4);
        Matrix4x4[] bones = new Matrix4x4[1];
        Assert.True(h.TryBones(0x300, Bones, 0.5, bones));
        Assert.Equal(11, bones[0].M41, 4);
    }

    [Fact]
    public void Cutting_twice_in_one_tick_counts_once() {
        CharacterHistory h = Tick(new CharacterHistory(), 0);
        h.CutAll();
        h.CutAll();
        Assert.Equal(1, h.AllCuts);
    }

    [Fact]
    public void Clear_forgets_the_cut() {
        CharacterHistory h = Tick(new CharacterHistory(), 0);
        h.CutAll();
        h.Clear();
        Assert.False(h.AllCut);
    }

    [Fact]
    public void No_cloth_mesh_is_blended_at_a_cut_and_the_next_tick_blends_again() {
        ClothHistory h = new();
        ulong id = CharacterHistory.Identity(0x5000, 1);
        h.Push(0x2000, id, 1, Matrix4x4.Identity, [new Vector4(0, 0, 0, 1)]);
        h.Push(0x2000, id, 2, Matrix4x4.Identity, [new Vector4(10, 0, 0, 1)]);
        h.CutAll(2);
        Vector4[] v = new Vector4[1];
        Assert.False(h.TryBlend(0x2000, id, 2, 0.5, out _, v));
        Assert.True(h.TryLatest(0x2000, id, out _, v));
        Assert.Equal(10, v[0].X);
        h.Push(0x2000, id, 3, Matrix4x4.Identity, [new Vector4(12, 0, 0, 1)]);
        Assert.True(h.TryBlend(0x2000, id, 3, 0.5, out _, v));
        Assert.Equal(11, v[0].X, 4);
    }
}
