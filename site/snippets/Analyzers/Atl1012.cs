// SPDX-License-Identifier: MIT
#pragma warning disable ATL1012 // the bad example: SnippetTests checks that it raises ATL1012
using Ffx;
using Ffx.Atel;
using static Snippets.Atl1012.Test1012;

namespace Snippets.Atl1012;

[Monster("m1012")]
[Workers(typeof(Ai))]
public static partial class Test1012
{

}

public sealed partial class Ai : BattleWorker
{
    // region bad
    [Entry(2)]
    void Heal()
    {
        Self.Hp += 100;                 // ATL1012
    }
    // endregion

    // region fix
    [Entry(3)]
    void HealInTwoSteps()
    {
        Self.Hp = Self.Hp + 100;        // read it, then set it
    }
    // endregion
}
