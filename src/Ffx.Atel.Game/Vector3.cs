// SPDX-License-Identifier: MIT

namespace Ffx;

/// <summary>
///     A point or direction in the game's world: three floats. It exists only in the source: setting a
///     <see cref="Vector3"/> property (<c>Pos = new Vector3(x, y, z);</c>) passes the three values to the native as
///     they are written, so it compiles to the same bytes as <c>Common.SetPos(x, y, z)</c>; reading <c>Pos.X</c>
///     calls the native that reads that component.
/// </summary>
/// <remarks>
///     Its own type, not <c>System.Numerics.Vector3</c> (or Unity's): ATEL has no structs, so a
///     <see cref="Vector3"/> is never stored, compared or computed with. It is written only where a property takes
///     one, as <c>new Vector3(x, y, z)</c>, and read only by component. A .NET vector would offer arithmetic, length
///     and normalization that no script can run, and need a <c>using</c> the decompiled files do not have.
/// </remarks>
/// <param name="x">The x coordinate.</param>
/// <param name="y">The y coordinate.</param>
/// <param name="z">The z coordinate.</param>
public readonly struct Vector3(float x, float y, float z) {
    /// <summary>The x coordinate.</summary>
    public float X { get; } = x;

    /// <summary>The y coordinate.</summary>
    public float Y { get; } = y;

    /// <summary>The z coordinate.</summary>
    public float Z { get; } = z;
}
