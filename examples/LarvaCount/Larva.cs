// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: Monsters.M209 is m209's vanilla source, <vanilla>/Monsters/M209/.
namespace Monsters.M209;

public sealed partial class Ai
{
    /// <summary>Runs after the vanilla Init, once per Larva in the battle: one more Larva met.</summary>
    [Postfix(nameof(Init))]
    void CountLarva()
    {
        LarvaCount.Progress.LarvasMet += 1;
        if (LarvaCount.Progress.LarvasMet >= 10) LarvaCount.Progress.Veteran = 1;
        Debug.Print($"Larvas met: {LarvaCount.Progress.LarvasMet}");
    }
}
