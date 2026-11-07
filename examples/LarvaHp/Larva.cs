// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: Monsters.M209 is m209's vanilla source, <vanilla>/Monsters/M209/.
namespace Monsters.M209;

// Ai is the Larva's action worker in the decompiled source (worker 1); this part of it adds to it.
public sealed partial class Ai
{
    /// <summary>Runs after the vanilla Init, which sets the Larva up: its HP becomes 1234.</summary>
    [Postfix(nameof(Init))]
    void MoreHp()
    {
        Btl.SetActorProperty(ActorProperty.Hp, 1234);
    }
}
