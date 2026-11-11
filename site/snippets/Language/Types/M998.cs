// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Monsters.M998.M998;

namespace Monsters.M998;

[Monster("m998")]
[Workers(typeof(Ai))]
[SharedData(0x10, "")]
public static partial class M998
{
    [Var(Storage.Shared, 0x0000)] public static int Hits;
    [Var(Storage.Shared, 0x0004)] public static byte Small;
    [Var(Storage.Shared, 0x0008)] public static uint Kills;
    [Var(Storage.Shared, 0x000c)] public static float Speed;
}

public sealed partial class Ai : BattleWorker
{
    [Register(0)] public static int I0;

    protected override void Init()
    {
        // region numbers
        Hits = Hits * 3 + 1;        // 32-bit, wraps on overflow
        Speed = 35f;                // a float literal needs its f
        Speed = Speed / 2f;
        // endregion

        // region enums
        Btl.UseCommand(Target.Own, Command.Cure);
        I0 = Btl.GetActorProperty(Target.Own, ActorProperty.Hp);
        if (Btl.GetBattleId() == Battle.Bsil03_00)
        {
            Btl.UseCommand((Target)I0, Command.Attack);
        }
        // endregion

        // region casts
        Small = (byte)(Hits + I0);  // C# wants the cast; it compiles to nothing
        Hits = (int)Kills;          // a uint read as a value
        // endregion
        return;
    }
}
