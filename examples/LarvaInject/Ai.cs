// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: Monsters.M209 is m209's vanilla source, <vanilla>/Monsters/M209/.
namespace Monsters.M209;

public sealed partial class Ai
{
    /// <summary>
    ///     Runs inside the vanilla OnTurn, after the statement with its 3rd call to Btl.UseCommand: the Thundara the
    ///     Larva casts on a party member while it has more than half its HP. Then its HP drops to 1.
    /// </summary>
    [Inject(nameof(OnTurn), After = nameof(Btl.UseCommand), Occurrence = 3)]
    void Exhausted()
    {
        Btl.SetActorProperty(ActorProperty.Hp, 1);
    }
}
