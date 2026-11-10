// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: bsil0700 is the Village Slope, the path between Besaid village and the island.
namespace Events.Bs_Besaid.Bsil0700;

// Tidus is the player's worker on this map.
public sealed partial class Tidus
{
    /// <summary>
    ///     Runs after the vanilla Init, which makes Tidus the player when the map loads: the player's run speed
    ///     (speed 2) becomes 68, twice the 34 most maps use.
    /// </summary>
    [Postfix(nameof(Init))]
    void Fast()
    {
        Screen.PlayerSetSpeed(2, 68f);
    }
}
