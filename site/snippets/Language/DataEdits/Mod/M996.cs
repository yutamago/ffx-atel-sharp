// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Monsters.M996.M996;

namespace Monsters.M996;

// region data-edits
public static partial class M996
{
    static M996()
    {
        MonsterStats.Hp = 4321;
        MonsterStats.Strength = 30;
        MonsterRewards.Gil = 999;
        Rage = 2;                                   // a shared variable's initial value
        Odds[3] = 7;                                // one element of a table
        Data.At<ushort>(DataArea.Other, 0x20) = 7;  // bytes no variable covers
    }
}
// endregion
