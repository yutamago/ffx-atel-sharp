// SPDX-License-Identifier: MIT
using Ffx.Atel;

// Not a script's namespace: a plain static class of [Saved] fields is the mod's own.
namespace GenerousVillager;

/// <summary>What the mod keeps with each save game (docs/language.md, "Saved fields").</summary>
public static class Gift
{
    /// <summary>1 once the old woman has given the gil. 0 on a new game and on a save made without the mod.</summary>
    [Saved] public static byte Given;
}
