// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ffx.Frames;

/// <summary>
///     An object's matrix between two ticks: a character's matrix (local to world, <c>+0x1d0</c>) or one of its bone
///     matrices. Row-vector 4×4 floats as on the PS2 (a point is drawn at <c>p × M</c>, the translation in the last row).
/// </summary>
/// <remarks>
///     The matrix is taken apart into scale, rotation and translation (<see cref="Matrix4x4.Decompose"/>); the rotation is
///     slerped, the rest lerped, so a turning arm turns instead of shrinking through the straight line between two
///     matrices. A matrix that is not affine, or that does not come back from its parts, is lerped element by element.
/// </remarks>
public static class TransformInterpolator {
    /// <summary>A matrix taken apart; <paramref name="Ok"/> false when it cannot be.</summary>
    public readonly record struct Pose(bool Ok, Vector3 Scale, Quaternion Rotation, Vector3 Translation);

    /// <summary>Takes <paramref name="m"/> apart; <see cref="Pose.Ok"/> is false when it is not affine, not finite, skewed or degenerate.</summary>
    public static Pose Decompose(in Matrix4x4 m) {
        const float Eps = 1e-4f;
        if (MathF.Abs(m.M14) > Eps || MathF.Abs(m.M24) > Eps || MathF.Abs(m.M34) > Eps || MathF.Abs(m.M44 - 1) > Eps) return default;
        if (!Finite(m) || !Matrix4x4.Decompose(m, out Vector3 s, out Quaternion q, out Vector3 t)) return default;
        if (!float.IsFinite(s.X + s.Y + s.Z) || !float.IsFinite(q.X + q.Y + q.Z + q.W)) return default;
        Pose p = new(true, s, Quaternion.Normalize(q), t);
        // Decompose does not refuse a skewed matrix: it must come back.
        return CameraInterpolator.RelativeChange(Compose(p), m) < 1e-3 ? p : default;
    }

    /// <summary>The matrix of a pose.</summary>
    public static Matrix4x4 Compose(in Pose p) =>
        Matrix4x4.CreateScale(p.Scale) * Matrix4x4.CreateFromQuaternion(p.Rotation) * Matrix4x4.CreateTranslation(p.Translation);

    /// <summary>The matrix at <paramref name="t"/> between two poses (0 = <paramref name="a"/>, 1 = <paramref name="b"/>) and their matrices.</summary>
    public static Matrix4x4 Interpolate(in Matrix4x4 a, in Pose pa, in Matrix4x4 b, in Pose pb, float t) {
        if (t <= 0) return a;
        if (t >= 1 || a == b) return b;
        if (!pa.Ok || !pb.Ok || !SameSigns(pa.Scale, pb.Scale)) return Matrix4x4.Lerp(a, b, t);
        return Compose(new Pose(true, Vector3.Lerp(pa.Scale, pb.Scale, t), Quaternion.Slerp(pa.Rotation, pb.Rotation, t),
            Vector3.Lerp(pa.Translation, pb.Translation, t)));
    }

    /// <summary>The matrix at <paramref name="t"/> between <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static Matrix4x4 Interpolate(in Matrix4x4 a, in Matrix4x4 b, float t) {
        t = Math.Clamp(t, 0f, 1f);
        if (t <= 0) return a;
        if (t >= 1 || a == b) return b;
        return Interpolate(a, Decompose(a), b, Decompose(b), t);
    }

    /// <summary>How far the object moved and turned from <paramref name="a"/> to <paramref name="b"/>; null when either cannot be taken apart.</summary>
    public static (double Distance, double Degrees)? Step(in Matrix4x4 a, in Matrix4x4 b) {
        Pose pa = Decompose(a), pb = Decompose(b);
        if (!pa.Ok || !pb.Ok) return null;
        double dot = Math.Min(1, Math.Abs(Quaternion.Dot(pa.Rotation, pb.Rotation)));
        return (Vector3.Distance(pa.Translation, pb.Translation), 2 * Math.Acos(dot) * 180 / Math.PI);
    }

    /// <summary>
    ///     Whether going from <paramref name="a"/> to <paramref name="b"/> in one tick is a jump to draw as it is: a move
    ///     further than <paramref name="cutDistance"/>, a turn more than <paramref name="cutDegrees"/>, a mirror that flipped,
    ///     or, for matrices that cannot be taken apart, an element that changed by more than a quarter.
    /// </summary>
    public static bool IsCut(in Matrix4x4 a, in Matrix4x4 b, double cutDistance, double cutDegrees) {
        Pose pa = Decompose(a), pb = Decompose(b);
        if (pa.Ok && pb.Ok) {
            if (!SameSigns(pa.Scale, pb.Scale)) return true;
            double dot = Math.Min(1, Math.Abs(Quaternion.Dot(pa.Rotation, pb.Rotation)));
            return Vector3.Distance(pa.Translation, pb.Translation) > cutDistance || 2 * Math.Acos(dot) * 180 / Math.PI > cutDegrees;
        }
        return !Finite(a) || !Finite(b) || CameraInterpolator.RelativeChange(a, b) > 0.25;
    }

    private static bool SameSigns(Vector3 a, Vector3 b) =>
        MathF.Sign(a.X) == MathF.Sign(b.X) && MathF.Sign(a.Y) == MathF.Sign(b.Y) && MathF.Sign(a.Z) == MathF.Sign(b.Z);

    private static bool Finite(in Matrix4x4 m) =>
        float.IsFinite(m.M11 + m.M12 + m.M13 + m.M14 + m.M21 + m.M22 + m.M23 + m.M24
            + m.M31 + m.M32 + m.M33 + m.M34 + m.M41 + m.M42 + m.M43 + m.M44);
}

/// <summary>
///     The characters of the last two ticks: each character's matrix (keyed by its slot in the character array) and each
///     renderer instance's bone matrices (keyed by the instance), and what to draw between them. Filled on the game
///     thread after each game update that ran a tick (<c>Ch_CalcMiddle</c>'s return).
/// </summary>
/// <remarks>
///     <para>A sample is drawn between ticks only when it has two samples from consecutive tick updates, both with the same
///     identity, and the step between them is not a jump:</para>
///     <list type="bullet">
///         <item>a character that was not there at the tick before (new, shown again, or not updated) is drawn as it is;</item>
///         <item>the identity is what the slot holds (the character's id and its instance list) or what the instance is (its
///         bone array and count): a slot reused for another character, or a model swap, starts again;</item>
///         <item>a move further than the character cut distance or a turn more than the character cut angle in one tick
///         is a warp (<see cref="TransformInterpolator.IsCut"/>);</item>
///         <item><see cref="Clear"/> on a scene change.</item>
///     </list>
/// </remarks>
public sealed class CharacterHistory {
    private sealed class RootSample {
        public ulong Identity;
        public Matrix4x4 Prev, Cur;
        public int PrevSerial = -1, CurSerial = -1;
        public bool Cut;
    }

    private sealed class BoneSample {
        public ulong Identity;
        public Matrix4x4[] Prev = [], Cur = [];
        public TransformInterpolator.Pose[] PrevPose = [], CurPose = [];
        public int Count;
        public int PrevSerial = -1, CurSerial = -1;
    }

    private readonly Dictionary<nint, RootSample> _roots = [];
    private readonly Dictionary<nint, BoneSample> _bones = [];
    private readonly List<nint> _stale = [];

    /// <summary>Tick updates recorded (the current one's number).</summary>
    public int Serial { get; private set; }
    /// <summary>Characters and instances held.</summary>
    public int Characters => _roots.Count;
    public int Instances => _bones.Count;
    /// <summary>Warps seen.</summary>
    public int Cuts { get; private set; }
    /// <summary>Tick updates in which every character was cut (<see cref="CutAll"/>).</summary>
    public int AllCuts { get; private set; }
    private int _allCut = -1;

    /// <summary>Whether every character is drawn as it is at this tick update (<see cref="CutAll"/>).</summary>
    public bool AllCut => _allCut == Serial;

    /// <summary>Forgets everything (a scene change, the setting switched off).</summary>
    public void Clear() {
        _roots.Clear();
        _bones.Clear();
        _allCut = -1;
    }

    /// <summary>
    ///     The camera cut at this tick update (a new shot): every character is drawn as the tick left it, like a warp,
    ///     until the next tick, which blends from this one.
    /// </summary>
    public void CutAll() {
        if (_allCut == Serial) return;
        _allCut = Serial;
        AllCuts++;
    }

    /// <summary>A new tick update: the samples pushed until the next call are its own.</summary>
    public void BeginTick() => Serial++;

    /// <summary>Drops what was not seen in the last two tick updates.</summary>
    public void Prune() {
        _stale.Clear();
        foreach ((nint k, RootSample s) in _roots) {
            if (s.CurSerial < Serial - 1) _stale.Add(k);
        }
        foreach (nint k in _stale) _roots.Remove(k);
        _stale.Clear();
        foreach ((nint k, BoneSample s) in _bones) {
            if (s.CurSerial < Serial - 1) _stale.Add(k);
        }
        foreach (nint k in _stale) _bones.Remove(k);
    }

    /// <summary>Adds a character's matrix at this tick update.</summary>
    public void PushRoot(nint key, ulong identity, in Matrix4x4 m, double cutDistance, double cutDegrees) {
        if (!_roots.TryGetValue(key, out RootSample? s)) _roots[key] = s = new RootSample();
        if (s.CurSerial == Serial) {
            s.Cur = m;
            return;
        }
        bool follows = s.Identity == identity && s.CurSerial == Serial - 1;
        s.Identity = identity;
        s.Prev = s.Cur;
        s.PrevSerial = follows ? s.CurSerial : -1;
        s.Cur = m;
        s.CurSerial = Serial;
        s.Cut = follows && TransformInterpolator.IsCut(s.Prev, s.Cur, cutDistance, cutDegrees);
        if (s.Cut) Cuts++;
    }

    /// <summary>
    ///     The character's matrix to draw at <paramref name="alpha"/>; false when it is drawn as it is (one sample, not
    ///     consecutive, another identity, a warp).
    /// </summary>
    public bool TryRoot(nint key, ulong identity, double alpha, out Matrix4x4 m) {
        m = default;
        if (!Interpolates(key, identity, out RootSample? s)) return false;
        m = TransformInterpolator.Interpolate(s.Prev, s.Cur, (float)Math.Clamp(alpha, 0, 1));
        return true;
    }

    /// <summary>The character's latest matrix (the tick's own), if it has one with this identity.</summary>
    public bool TryLatestRoot(nint key, ulong identity, out Matrix4x4 m) {
        m = default;
        if (!_roots.TryGetValue(key, out RootSample? s) || s.Identity != identity || s.CurSerial != Serial) return false;
        m = s.Cur;
        return true;
    }

    /// <summary>Whether the character jumped at the last tick (its bones are then drawn as they are too).</summary>
    public bool RootCut(nint key) => AllCut || _roots.TryGetValue(key, out RootSample? s) && s.Cut && s.CurSerial == Serial;

    private bool Interpolates(nint key, ulong identity, out RootSample s) {
        if (!_roots.TryGetValue(key, out s!) || AllCut) return false;
        return s.Identity == identity && s.CurSerial == Serial && s.PrevSerial == Serial - 1 && !s.Cut;
    }

    /// <summary>Adds an instance's bone matrices at this tick update.</summary>
    public void PushBones(nint key, ulong identity, ReadOnlySpan<Matrix4x4> bones) {
        if (!_bones.TryGetValue(key, out BoneSample? s)) _bones[key] = s = new BoneSample();
        if (s.CurSerial == Serial) {
            // A second push in the same tick update replaces the first.
            if (s.Identity != identity || s.Count != bones.Length) s.PrevSerial = -1;
        }
        else {
            bool follows = s.Identity == identity && s.CurSerial == Serial - 1 && s.Count == bones.Length;
            (s.Prev, s.Cur) = (s.Cur, s.Prev);
            (s.PrevPose, s.CurPose) = (s.CurPose, s.PrevPose);
            s.PrevSerial = follows ? s.CurSerial : -1;
        }
        if (s.Cur.Length < bones.Length) {
            s.Cur = new Matrix4x4[bones.Length];
            s.CurPose = new TransformInterpolator.Pose[bones.Length];
        }
        bones.CopyTo(s.Cur);
        for (int i = 0; i < bones.Length; i++) s.CurPose[i] = TransformInterpolator.Decompose(bones[i]);
        s.Identity = identity;
        s.Count = bones.Length;
        s.CurSerial = Serial;
    }

    /// <summary>Writes the instance's bones at <paramref name="alpha"/> into <paramref name="dest"/>; false (nothing written) when it has no two consecutive samples of this identity.</summary>
    public bool TryBones(nint key, ulong identity, double alpha, Span<Matrix4x4> dest) {
        if (AllCut || !_bones.TryGetValue(key, out BoneSample? s) || s.Identity != identity || s.Count != dest.Length
            || s.CurSerial != Serial || s.PrevSerial != Serial - 1) return false;
        float t = (float)Math.Clamp(alpha, 0, 1);
        for (int i = 0; i < s.Count; i++) dest[i] = TransformInterpolator.Interpolate(s.Prev[i], s.PrevPose[i], s.Cur[i], s.CurPose[i], t);
        return true;
    }

    /// <summary>Writes the instance's latest bones (the tick's own) into <paramref name="dest"/>; false when it has none of this identity.</summary>
    public bool TryLatestBones(nint key, ulong identity, Span<Matrix4x4> dest) {
        if (!_bones.TryGetValue(key, out BoneSample? s) || s.Identity != identity || s.Count != dest.Length || s.CurSerial < 0) return false;
        s.Cur.AsSpan(0, s.Count).CopyTo(dest);
        return true;
    }

    /// <summary>A key for a slot's identity from two words.</summary>
    public static ulong Identity(uint a, uint b) => (ulong)a << 32 | b;
}
