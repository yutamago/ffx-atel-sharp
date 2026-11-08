// SPDX-License-Identifier: MIT
using Ffx.Atel;

// Not a script's namespace: a plain static class of [Saved] fields is the mod's own, shared by all its scripts.
namespace LarvaCount;

/// <summary>What the mod keeps with each save game (docs/language.md, "Saved fields").</summary>
public static class Progress
{
    /// <summary>The Larvas met, in every battle with one. Kept under the key LarvaCount.Progress.LarvasMet.</summary>
    [Saved] public static ushort LarvasMet;

    /// <summary>1 once ten Larvas are met.</summary>
    [Saved] public static byte Veteran;
}
