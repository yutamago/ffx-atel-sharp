// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Monsters.M995.M995;

namespace Monsters.M995;

[Monster("m995")]
[Workers(typeof(Ai))]
public static partial class M995
{
}

public sealed partial class Ai : BattleWorker
{
    protected override void Init()
    {
        // region actors
        Self.Hp = 500;                                  // this monster
        Actors[Target.AllPly].Defense = 0;              // every party member
        if (Actors[Target.Own].Hp < 100)
        {
            Self.Hp = Self.Hp + 50;                     // read, then set: no +=
        }
        // endregion
        return;
    }
}
