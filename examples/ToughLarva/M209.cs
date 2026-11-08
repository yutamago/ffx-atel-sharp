// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: Monsters.M209 is m209's vanilla source, <vanilla>/Monsters/M209/.
namespace Monsters.M209;

// The script class's static constructor sets initial values: each line is one field of the monster's data,
// which the linker writes into the player's m209.bin in place (docs/language.md, "Initial values and data edits").
public static partial class M209
{
    static M209()
    {
        MonsterStats.Hp = 4321;        // vanilla 1498
        MonsterStats.Strength = 30;    // vanilla 10
        MonsterRewards.Gil = 999;      // vanilla 330
    }
}
