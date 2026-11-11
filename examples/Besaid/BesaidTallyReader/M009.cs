// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: Monsters.M009 is the Dingo, m009.
namespace Monsters.M009;

public sealed partial class Ai
{
    /// <summary>Runs after the vanilla Init, once per Dingo in the battle: Besaid Tally's count so far.</summary>
    [Postfix(nameof(Init))]
    void ShowTally()
    {
        Debug.Print($"Besaid Tally Reader: {BesaidTallyReader.Tally.Defeated} defeated so far");
    }
}
