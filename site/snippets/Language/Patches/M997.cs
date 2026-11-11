// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Monsters.M997.M997;

namespace Monsters.M997;

[Monster("m997")]
[Workers(typeof(Ai))]
[SharedData(0x10, "")]
public static partial class M997
{
    [Var(Storage.Shared, 0x0000)] public static int Hp;
}

// region vanilla
public sealed partial class Ai : BattleWorker
{
    protected override void Init()
    {
        Self.Hp = 1498;
        return;
    }

    protected override void Main()
    {
        return;
    }

    [Entry(2)]
    void OnTurn()
    {
        if (Self.Hp < 500)
        {
            Btl.UseCommand(Target.Own, Command.Cure);
            return;
        }
        Btl.UseCommand(Target.AllPly, Command.Attack);
        Btl.UseCommand(Target.AllPly, Command.Attack);
        return;
    }

    [Entry(3)]
    void OnHit()
    {
        Hp = Self.Hp;
        return;
    }

    [Entry(4)]
    void OnDeath()
    {
        Hp = 0;
        return;
    }
}
// endregion
