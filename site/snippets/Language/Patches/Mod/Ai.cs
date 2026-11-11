// SPDX-License-Identifier: MIT
// region usings
using Ffx;
using Ffx.Atel;
using static Monsters.M997.M997;

namespace Monsters.M997;
// endregion

// region new-variable
public static partial class M997
{
    public static int Turns;            // a new variable of the mod
}
// endregion

public sealed partial class Ai
{
    // region postfix
    [Postfix(nameof(Init))]
    void Tougher()
    {
        Self.Hp = 4000;
    }
    // endregion

    // region prefix
    [Prefix(nameof(OnHit))]
    void IgnoreWhenLow()
    {
        if (Self.Hp < 10)
        {
            return;     // ends the entry: the vanilla OnHit does not run
        }
    }
    // endregion

    // region replace
    [Replace(nameof(OnDeath))]
    void Vanish()
    {
        Hp = 0;
        Turns = 0;
    }
    // endregion

    // region inject
    // In OnTurn, after the statement with the 2nd call to Btl.UseCommand.
    [Inject(nameof(OnTurn), After = nameof(Btl.UseCommand), Occurrence = 2)]
    void Tired()
    {
        Turns = Turns + 1;
    }

    // Before the statement with the 1st use of Self.Hp: the whole if.
    [Inject(nameof(OnTurn), Before = nameof(Self.Hp))]
    void Count()
    {
        Turns = Turns + 1;
    }
    // endregion

    // region new-entry
    public static async Task ShoutAsync()
    {
        Turns = 0;
    }
    // endregion
}

// region new-worker
public sealed partial class Watcher : BattleWorker
{
    protected override void Init()
    {
        return;
    }

    protected override void Main()
    {
        Wait.Forever();
    }
}
// endregion
