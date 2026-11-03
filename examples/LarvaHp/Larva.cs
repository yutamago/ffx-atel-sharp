// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: Monsters.M209 is m209's vanilla source, <vanilla>/Monsters/M209/.
namespace Monsters.M209;

// W01 is the Larva's worker in the decompiled source; this part of it adds to it.
public sealed partial class W01
{
    /// <summary>Runs after the vanilla Init, which sets the Larva up: its HP becomes 1234.</summary>
    [Postfix(nameof(Init))]
    void MoreHp()
    {
        Btl.SetActorProperty(ActorProperty.Hp, 1234);
    }
}
