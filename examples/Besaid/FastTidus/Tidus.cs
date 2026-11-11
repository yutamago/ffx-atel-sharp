// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: bsil0700 is the Village Slope, the path between Besaid village and the island.
namespace Events.Bs_Besaid.Bsil0700;

// Tidus is the player's worker on this map.
public sealed partial class Tidus
{
    /// <summary>
    ///     Runs after the vanilla Init, which makes Tidus the player when the map loads: his run speed becomes 76,
    ///     twice the 38 every map starts with. The beach (bsil0100) changes it the same way, to 40.8.
    /// </summary>
    [Postfix(nameof(Init))]
    void Fast()
    {
        Screen.PlayerSetSpeed(kind: 2, speed: 76f);   // kind 2 is running (1 walking, 18 swimming)
    }
}
