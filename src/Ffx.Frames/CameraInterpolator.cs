// SPDX-License-Identifier: MIT

using System;
using System.Numerics;

namespace Ffx.Frames;

/// <summary>
///     The camera between two ticks. The game keeps the camera matrix (world to view, <c>g_CameraMatrix</c>
///     <c>0x02311440</c>) and the screen matrix (view to screen, <c>g_ScreenMatrix</c> <c>0x02311480</c>; a point is
///     drawn at <c>p × camera × screen</c>), both row-vector 4×4 floats as on the PS2.
/// </summary>
/// <remarks>
///     <para>
///         The camera matrix is a rigid move, possibly with a scale or a mirrored axis (on the view's axes, or on the
///         world's). It is taken apart into scale,
///         rotation and the eye (the camera's place in the world): the rotation is slerped, the eye and scale lerped,
///         and the matrix put together again, so a turning camera turns instead of shrinking through the straight
///         line between two matrices. A matrix that is not affine (last column not 0, 0, 0, 1) or has no inverse is
///         lerped element by element. The screen matrix (projection, screen offsets, shake) is lerped element by
///         element.
///     </para>
///     <para>
///         A cut (the camera jumped to a new shot) is a move further than <see cref="FrameSettings.CutDistance"/> or a
///         turn more than <see cref="FrameSettings.CutDegrees"/> in one tick, or a screen matrix that changed by more
///         than a quarter: then the frame shows the new camera as it is, instead of a sweep through the scene.
///     </para>
/// </remarks>
public static class CameraInterpolator {
    /// <summary>
    ///     A camera matrix taken apart: its upper 3×3 is a rotation times a scale per axis, the scale on the view's axes
    ///     (columns, <paramref name="ScaleColumns"/>: a flipped or stretched view, the PS2's usual) or on the world's
    ///     (rows); a mirror is kept as a negative first scale. <paramref name="Eye"/> is where the camera is.
    /// </summary>
    public readonly record struct Pose(Vector3 Scale, Quaternion Rotation, Vector3 Eye, bool ScaleColumns = true);

    /// <summary>Takes a camera matrix apart; false when it is not affine, has no inverse, or is skewed.</summary>
    public static bool TryDecompose(in Matrix4x4 m, out Pose pose) {
        pose = default;
        const float Eps = 1e-5f, Square = 1e-3f;
        if (MathF.Abs(m.M14) > Eps || MathF.Abs(m.M24) > Eps || MathF.Abs(m.M34) > Eps || MathF.Abs(m.M44 - 1) > Eps) return false;
        Vector3 r0 = new(m.M11, m.M12, m.M13), r1 = new(m.M21, m.M22, m.M23), r2 = new(m.M31, m.M32, m.M33);
        Vector3 c0 = new(m.M11, m.M21, m.M31), c1 = new(m.M12, m.M22, m.M32), c2 = new(m.M13, m.M23, m.M33);
        bool columns = Orthogonal(c0, c1, c2, Square);
        if (!columns && !Orthogonal(r0, r1, r2, Square)) return false;
        (Vector3 a0, Vector3 a1, Vector3 a2) = columns ? (c0, c1, c2) : (r0, r1, r2);
        float s0 = a0.Length(), s1 = a1.Length(), s2 = a2.Length();
        if (s0 < Eps || s1 < Eps || s2 < Eps || !float.IsFinite(s0 + s1 + s2)) return false;
        a0 /= s0; a1 /= s1; a2 /= s2;
        // A mirrored basis (determinant below 0) keeps its mirror in the first scale, so the rest is a rotation.
        if (Vector3.Dot(Vector3.Cross(a0, a1), a2) < 0) { s0 = -s0; a0 = -a0; }
        Matrix4x4 rot = columns
            ? new(a0.X, a1.X, a2.X, 0, a0.Y, a1.Y, a2.Y, 0, a0.Z, a1.Z, a2.Z, 0, 0, 0, 0, 1)
            : new(a0.X, a0.Y, a0.Z, 0, a1.X, a1.Y, a1.Z, 0, a2.X, a2.Y, a2.Z, 0, 0, 0, 0, 1);
        Quaternion q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rot));
        // p × A + t = 0 at the eye: eye = -t × A⁻¹.
        Matrix4x4 a = m with { M41 = 0, M42 = 0, M43 = 0 };
        if (!Matrix4x4.Invert(a, out Matrix4x4 inv)) return false;
        Vector3 eye = -Vector3.Transform(new Vector3(m.M41, m.M42, m.M43), inv);
        if (!float.IsFinite(eye.X + eye.Y + eye.Z) || !float.IsFinite(q.X + q.Y + q.Z + q.W)) return false;
        pose = new Pose(new Vector3(s0, s1, s2), q, eye, columns);
        return true;
    }

    private static bool Orthogonal(Vector3 a, Vector3 b, Vector3 c, float tol) {
        float la = a.Length(), lb = b.Length(), lc = c.Length();
        if (la == 0 || lb == 0 || lc == 0) return false;
        return MathF.Abs(Vector3.Dot(a, b)) / (la * lb) < tol
            && MathF.Abs(Vector3.Dot(a, c)) / (la * lc) < tol
            && MathF.Abs(Vector3.Dot(b, c)) / (lb * lc) < tol;
    }

    /// <summary>The camera matrix of a pose.</summary>
    public static Matrix4x4 Compose(in Pose p) {
        Matrix4x4 r = Matrix4x4.CreateFromQuaternion(p.Rotation);
        Vector3 s = p.Scale;
        Matrix4x4 a = p.ScaleColumns
            ? new(r.M11 * s.X, r.M12 * s.Y, r.M13 * s.Z, 0,
                  r.M21 * s.X, r.M22 * s.Y, r.M23 * s.Z, 0,
                  r.M31 * s.X, r.M32 * s.Y, r.M33 * s.Z, 0,
                  0, 0, 0, 1)
            : new(r.M11 * s.X, r.M12 * s.X, r.M13 * s.X, 0,
                  r.M21 * s.Y, r.M22 * s.Y, r.M23 * s.Y, 0,
                  r.M31 * s.Z, r.M32 * s.Z, r.M33 * s.Z, 0,
                  0, 0, 0, 1);
        Vector3 t = -Vector3.Transform(p.Eye, a);
        return a with { M41 = t.X, M42 = t.Y, M43 = t.Z };
    }

    /// <summary>How far the camera moved and turned from <paramref name="a"/> to <paramref name="b"/>; null when either is not a rigid camera.</summary>
    public static (double Distance, double Degrees)? Step(in Matrix4x4 a, in Matrix4x4 b) {
        if (!TryDecompose(a, out Pose pa) || !TryDecompose(b, out Pose pb)) return null;
        double dot = Math.Min(1, Math.Abs(Quaternion.Dot(pa.Rotation, pb.Rotation)));
        return (Vector3.Distance(pa.Eye, pb.Eye), 2 * Math.Acos(dot) * 180 / Math.PI);
    }

    /// <summary>Whether going from (<paramref name="camA"/>, <paramref name="scrA"/>) to (<paramref name="camB"/>, <paramref name="scrB"/>) in one tick is a cut.</summary>
    public static bool IsCut(in Matrix4x4 camA, in Matrix4x4 scrA, in Matrix4x4 camB, in Matrix4x4 scrB, double cutDistance, double cutDegrees) {
        if (Step(camA, camB) is (double d, double deg)) {
            if (d > cutDistance || deg > cutDegrees) return true;
        }
        else if (RelativeChange(camA, camB) > 0.25) return true;
        return RelativeChange(scrA, scrB) > 0.25;
    }

    /// <summary>The largest element change relative to the larger matrix's largest element.</summary>
    public static double RelativeChange(in Matrix4x4 a, in Matrix4x4 b) {
        double scale = 0, diff = 0;
        for (int i = 0; i < 16; i++) {
            float x = Element(a, i), y = Element(b, i);
            scale = Math.Max(scale, Math.Max(Math.Abs(x), Math.Abs(y)));
            diff = Math.Max(diff, Math.Abs(x - y));
        }
        return scale > 0 ? diff / scale : 0;
    }

    /// <summary>The camera matrix at <paramref name="t"/> (0 = <paramref name="a"/>, 1 = <paramref name="b"/>).</summary>
    public static Matrix4x4 Camera(in Matrix4x4 a, in Matrix4x4 b, float t) {
        t = Math.Clamp(t, 0f, 1f);
        if (t <= 0) return a;
        if (t >= 1) return b;
        if (!TryDecompose(a, out Pose pa) || !TryDecompose(b, out Pose pb)
            || MathF.Sign(pa.Scale.X) != MathF.Sign(pb.Scale.X) || pa.ScaleColumns != pb.ScaleColumns) return Matrix4x4.Lerp(a, b, t);
        return Compose(new Pose(Vector3.Lerp(pa.Scale, pb.Scale, t), Quaternion.Slerp(pa.Rotation, pb.Rotation, t), Vector3.Lerp(pa.Eye, pb.Eye, t), pa.ScaleColumns));
    }

    /// <summary>The screen matrix at <paramref name="t"/>.</summary>
    public static Matrix4x4 Screen(in Matrix4x4 a, in Matrix4x4 b, float t) => Matrix4x4.Lerp(a, b, Math.Clamp(t, 0f, 1f));

    private static float Element(in Matrix4x4 m, int i) => m[i / 4, i % 4];
}

/// <summary>
///     The camera of the last two ticks, and what to draw between them. Filled on the game thread once per game update
///     that ran a tick.
/// </summary>
public sealed class CameraHistory {
    private Matrix4x4 _prevCam, _prevScr, _cam, _scr;
    private int _count;

    /// <summary>Samples held (0, 1 or 2).</summary>
    public int Count => _count;
    /// <summary>The last two samples were a cut.</summary>
    public bool Cut { get; private set; }
    /// <summary>The last step's move and turn, for the window; null when not a rigid camera.</summary>
    public (double Distance, double Degrees)? LastStep { get; private set; }
    /// <summary>Cuts seen.</summary>
    public int Cuts { get; private set; }

    public void Clear() { _count = 0; Cut = false; LastStep = null; }

    /// <summary>Adds the camera after a tick.</summary>
    public void Push(in Matrix4x4 cam, in Matrix4x4 scr, double cutDistance, double cutDegrees) {
        if (_count > 0) {
            _prevCam = _cam;
            _prevScr = _scr;
        }
        _cam = cam;
        _scr = scr;
        _count = Math.Min(2, _count + 1);
        if (_count == 2) {
            LastStep = CameraInterpolator.Step(_prevCam, _cam);
            Cut = CameraInterpolator.IsCut(_prevCam, _prevScr, _cam, _scr, cutDistance, cutDegrees);
            if (Cut) Cuts++;
        }
    }

    /// <summary>The latest camera matrix; false when there is none.</summary>
    public bool TryLatest(out Matrix4x4 cam) {
        cam = _cam;
        return _count > 0;
    }

    /// <summary>The camera and screen to draw at <paramref name="alpha"/>; false when there is nothing to interpolate (fewer than 2 samples, or a cut).</summary>
    public bool TryAt(double alpha, out Matrix4x4 cam, out Matrix4x4 scr) {
        cam = _cam;
        scr = _scr;
        if (_count < 2 || Cut) return false;
        float t = (float)Math.Clamp(alpha, 0, 1);
        cam = CameraInterpolator.Camera(_prevCam, _cam, t);
        scr = CameraInterpolator.Screen(_prevScr, _scr, t);
        return true;
    }
}
