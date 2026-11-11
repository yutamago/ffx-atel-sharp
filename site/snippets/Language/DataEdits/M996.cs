// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Monsters.M996.M996;

namespace Monsters.M996;

[Monster("m996")]
[Workers(typeof(Ai))]
[SharedData(0x10, "")]
[OtherData(0x40, "")]
public static partial class M996
{
    [Var(Storage.Shared, 0x0000)] public static int Rage;
    [Var(Storage.Shared, 0x0004)] public static byte[] Odds = new byte[4];
}

public sealed partial class Ai : BattleWorker
{
    protected override void Init()
    {
        Rage = Rage + Odds[0];
        return;
    }
}
