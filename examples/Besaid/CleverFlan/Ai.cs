// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: Monsters.M016 is the Water Flan, m016.
namespace Monsters.M016;

// Ai is the Water Flan's action worker (worker 1). Its OnTurn picks a party member and casts Water on it.
public sealed partial class Ai
{
    /// <summary>
    ///     Runs inside the vanilla OnTurn, after the statement with its call to Btl.SelectTarget. Below half its HP the
    ///     flan casts Water on itself instead, which it absorbs, and the turn ends here.
    /// </summary>
    [Inject(nameof(OnTurn), After = nameof(Btl.SelectTarget))]
    void HealItself()
    {
        if (Self.Hp < Self.Maxhp / 2)
        {
            Btl.UseCommand(Target.Own, Command.Water);
            return;
        }
    }
}
