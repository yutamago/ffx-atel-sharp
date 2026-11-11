// SPDX-License-Identifier: MIT
#pragma warning disable ATL1013 // the bad example: SnippetTests checks that it raises ATL1013
using Ffx;
using Ffx.Atel;
using static Snippets.Atl1013.Test1013;

namespace Snippets.Atl1013;

[Monster("m1013")]
[Workers(typeof(Ai))]
public static partial class Test1013
{

}

public sealed partial class Ai : BattleWorker
{
    // region bad
    [Entry(2)]
    void Weaken()
    {
        var party = Actors[Target.AllPly];      // ATL1013
    }
    // endregion

    // region fix
    [Entry(3)]
    void WeakenEveryone()
    {
        Actors[Target.AllPly].Defense = 0;      // the member right after it
        Actors[Target.AllPly].Mag = 0;
    }
    // endregion
}
