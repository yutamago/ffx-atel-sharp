// SPDX-License-Identifier: MIT
using Ffx.Atel;

// Not a script's namespace: a plain static class of [Saved] fields is the mod's own, shared by all its scripts.
namespace BesaidTally;

/// <summary>What the mod keeps with each save game (docs/language.md, "Saved fields").</summary>
public static class Tally
{
    /// <summary>The Dingos, Water Flans and Condors defeated. Kept under the key BesaidTally.Tally.Defeated.</summary>
    [Saved] public static ushort Defeated;
}
