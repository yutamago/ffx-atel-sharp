// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Atel;

/// <summary>
///     The <c>int</c> constants of <paramref name="constants"/> name the bits of this save field:
///     <c>[Bits(typeof(Save.LightningReward))] public const int LightningDodgingRewards</c>. The decompiler
///     writes a bit test or set on the field with a constant that has a name as the name
///     (<c>LightningDodgingRewards[0] &amp; Save.LightningReward.Streak5</c>, <c>|=</c>); other masks stay numbers.
/// </summary>
/// <param name="constants">A static class of <c>public const int</c> fields, each a mask.</param>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class BitsAttribute(Type constants) : Attribute {
    public Type Constants { get; } = constants;
}
