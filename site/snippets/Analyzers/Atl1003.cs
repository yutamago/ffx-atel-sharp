// SPDX-License-Identifier: MIT
#pragma warning disable ATL1003 // the bad example: SnippetTests checks that it raises ATL1003
using Ffx;
using Ffx.Atel;
using static Snippets.Atl1003.Test1003;

namespace Snippets.Atl1003;

[Event("test1003")]
[Workers(typeof(Guard))]
public static partial class Test1003
{
    public static int Mood;
}

public sealed partial class Guard : Actor
{
    // region bad
    [Entry(2)]
    void Calm()
    {
        Mood = System.Math.Abs(Mood);   // ATL1003
    }
    // endregion

    // region fix
    [Entry(3)]
    void CalmByHand()
    {
        if (Mood < 0)
        {
            Mood = -Mood;
        }
    }
    // endregion
}
