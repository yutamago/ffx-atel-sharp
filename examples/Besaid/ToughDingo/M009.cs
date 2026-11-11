// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: Monsters.M009 is the Dingo, m009.
namespace Monsters.M009;

// The static constructor sets initial values: each line is one field of the monster's data, which the linker
// writes into the player's m009.bin in place (docs/language.md, "Initial values and data edits").
public static partial class M009
{
    static M009()
    {
        MonsterStats.Hp = 1250;        // vanilla 125
        MonsterStats.Strength = 30;    // vanilla 13
        MonsterRewards.Gil = 999;      // vanilla 16
    }
}
