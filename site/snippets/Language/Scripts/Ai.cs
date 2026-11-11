// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Monsters.M999.M999;

namespace Monsters.M999;

// region worker
public sealed partial class Ai : BattleWorker
{
    [Register(0)] public static int I0;

    protected override void Init()
    {
        Turns = 0;
        return;
    }

    protected override void Main()
    {
        return;
    }

    [Entry(2)]
    void OnTurn()
    {
        Turns = Turns + 1;
        Btl.UseCommand(Target.Own, Command.Attack);
        return;
    }
}
// endregion

public sealed partial class Cameras : BattleWorker
{
    protected override void Init()
    {
        return;
    }
}
