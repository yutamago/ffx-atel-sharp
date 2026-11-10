// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: Monsters.M028 is the Condor, m028.
namespace Monsters.M028;

public sealed partial class Ai
{
    /// <summary>Runs after the vanilla OnDeath, which the battle runs when this Condor dies: one more counted.</summary>
    [Postfix(nameof(OnDeath))]
    void Count()
    {
        BesaidTally.Tally.Defeated += 1;
        Debug.Print($"Besaid monsters defeated: {BesaidTally.Tally.Defeated}");
    }
}
