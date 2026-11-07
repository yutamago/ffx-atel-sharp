// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ffx.Frames;

/// <summary>
///     The cloth of the last two ticks (hair, Tidus's shorts, Auron's coat, Wakka's crest, Yuna's ornaments): each cloth
///     mesh's drawn vertices and its matrix, and what to draw between them.
/// </summary>
/// <remarks>
///     <para>What the game draws (ffx-rust <c>docs/formats/scene-chars.md</c>, "Cloth"; <c>docs/formats/main.md</c>, "Cloth
///     anchors"): a character's render cloth (<c>chr + 0x834</c>, the same record the renderer lists at <c>0x00ccb254</c>)
///     holds <c>+0x14</c> lists from <c>+0x30</c>, each a count and mesh pointers. A mesh's first 16 floats are its matrix
///     (the shader's <c>ClothWorld</c>), <c>+0x44</c> its vertex count and <c>+0x4c</c> its vertices, 16 bytes each.</para>
///     <list type="bullet">
///         <item>The vertices: the cloth job (<c>Ch_Cloth_PerCharacter</c> <c>0x0082e390</c>: <c>Apply_Envelope</c>,
///         <c>Sg_ClothStep</c>, <c>Sg_ClothPlaceAnchors</c> <c>0x0082dbc0</c>) puts each one on its simulated triangle, in the
///         frame of the model's base element (the envelope skins with base⁻¹ × element). Once per game update; the mod holds it
///         between ticks.</item>
///         <item>The matrix: <c>Ch_UpdateClothMatrix</c> (<c>0x008314c0</c>, in <c>Ch_DrawMain</c>, every draw) writes the
///         base element's global matrix (translation × 1000) × the character's matrix (<c>+0x1d0</c>) × the flip
///         diag(−1, −1, 1, 1), row vectors.</item>
///     </list>
///     <para>So between ticks the cloth is drawn as the tick left it while the body under it moves on: the hair trails and
///     jumps on each tick. Each mesh is kept here as its vertices and its local matrix L (the matrix without the
///     character's: <see cref="Local"/>); between ticks both are blended and the character's drawn matrix put back
///     (<see cref="World"/>), so the cloth follows the body as the bones do.</para>
///     <para>A sample is blended only when it has two samples from consecutive tick updates with the same identity (the
///     vertex buffer and its count); the caller decides whether the character itself is drawn between ticks.</para>
/// </remarks>
public sealed class ClothHistory {
    /// <summary><c>Ch_UpdateClothMatrix</c>'s flip (<c>g_ChrClothFlip</c> <c>0x00b92340</c>, −1 on x and y). Its own inverse.</summary>
    public static readonly Matrix4x4 Flip = Matrix4x4.CreateScale(-1, -1, 1);

    private sealed class Sample {
        public ulong Identity;
        public int Count;
        public Matrix4x4 PrevLocal, CurLocal;
        public TransformInterpolator.Pose PrevPose, CurPose;
        public Vector4[] Prev = [], Cur = [];
        public int PrevSerial = -1, CurSerial = -1;
    }

    private readonly Dictionary<nint, Sample> _meshes = [];
    private readonly List<nint> _stale = [];

    /// <summary>Meshes held.</summary>
    public int Meshes => _meshes.Count;

    /// <summary>Forgets everything (a scene change, the setting switched off).</summary>
    public void Clear() => _meshes.Clear();

    /// <summary>
    ///     The mesh's matrix without the character's: <c>ClothWorld × Flip × root⁻¹</c> (the base element's matrix);
    ///     false when the character's matrix has no inverse.
    /// </summary>
    public static bool Local(in Matrix4x4 clothWorld, in Matrix4x4 root, out Matrix4x4 local) {
        local = default;
        if (!Matrix4x4.Invert(root, out Matrix4x4 inverse)) return false;
        local = clothWorld * Flip * inverse;
        return true;
    }

    /// <summary>The matrix the game would write for a character drawn at <paramref name="root"/>: <c>local × root × Flip</c>.</summary>
    public static Matrix4x4 World(in Matrix4x4 local, in Matrix4x4 root) => local * root * Flip;

    /// <summary>Adds a mesh's local matrix and vertices at tick update <paramref name="serial"/>; a second push in the same update replaces the first.</summary>
    public void Push(nint key, ulong identity, int serial, in Matrix4x4 local, ReadOnlySpan<Vector4> vertices) {
        if (!_meshes.TryGetValue(key, out Sample? s)) _meshes[key] = s = new Sample();
        if (s.CurSerial == serial) {
            if (s.Identity != identity || s.Count != vertices.Length) s.PrevSerial = -1;
        }
        else {
            bool follows = s.Identity == identity && s.CurSerial == serial - 1 && s.Count == vertices.Length;
            (s.Prev, s.Cur) = (s.Cur, s.Prev);
            s.PrevLocal = s.CurLocal;
            s.PrevPose = s.CurPose;
            s.PrevSerial = follows ? s.CurSerial : -1;
        }
        if (s.Cur.Length < vertices.Length) s.Cur = new Vector4[vertices.Length];
        vertices.CopyTo(s.Cur);
        s.CurLocal = local;
        s.CurPose = TransformInterpolator.Decompose(local);
        s.Identity = identity;
        s.Count = vertices.Length;
        s.CurSerial = serial;
    }

    /// <summary>
    ///     The mesh at <paramref name="alpha"/> between its last two ticks: its local matrix, and its vertices into
    ///     <paramref name="vertices"/>; false (nothing written) without two consecutive samples of this identity ending at
    ///     <paramref name="serial"/>.
    /// </summary>
    public bool TryBlend(nint key, ulong identity, int serial, double alpha, out Matrix4x4 local, Span<Vector4> vertices) {
        local = default;
        if (!_meshes.TryGetValue(key, out Sample? s) || s.Identity != identity || s.Count != vertices.Length
            || s.CurSerial != serial || s.PrevSerial != serial - 1) return false;
        float t = (float)Math.Clamp(alpha, 0, 1);
        local = TransformInterpolator.Interpolate(s.PrevLocal, s.PrevPose, s.CurLocal, s.CurPose, t);
        Blend(s.Prev.AsSpan(0, s.Count), s.Cur.AsSpan(0, s.Count), t, vertices);
        return true;
    }

    /// <summary>The mesh's latest sample (the tick's own): its local matrix and vertices; false when it has none of this identity.</summary>
    public bool TryLatest(nint key, ulong identity, out Matrix4x4 local, Span<Vector4> vertices) {
        local = default;
        if (!_meshes.TryGetValue(key, out Sample? s) || s.Identity != identity || s.Count != vertices.Length || s.CurSerial < 0) return false;
        local = s.CurLocal;
        s.Cur.AsSpan(0, s.Count).CopyTo(vertices);
        return true;
    }

    /// <summary>Drops what was not seen in the tick updates <paramref name="serial"/> − 1 and <paramref name="serial"/>.</summary>
    public void Prune(int serial) {
        _stale.Clear();
        foreach ((nint k, Sample s) in _meshes) {
            if (s.CurSerial < serial - 1) _stale.Add(k);
        }
        foreach (nint k in _stale) _meshes.Remove(k);
    }

    /// <summary><paramref name="a"/> to <paramref name="b"/> at <paramref name="t"/>, vertex by vertex (a vertex that did not move stays exactly where it is).</summary>
    public static void Blend(ReadOnlySpan<Vector4> a, ReadOnlySpan<Vector4> b, float t, Span<Vector4> dest) {
        for (int i = 0; i < a.Length; i++) dest[i] = a[i] == b[i] ? b[i] : Vector4.Lerp(a[i], b[i], t);
    }
}
