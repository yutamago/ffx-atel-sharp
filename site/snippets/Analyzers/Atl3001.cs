// SPDX-License-Identifier: MIT
#pragma warning disable ATL3001 // the bad example: SnippetTests checks that it raises ATL3001
using Ffx;
using Ffx.Atel;
using static Snippets.Atl3001.Test3001;

namespace Snippets.Atl3001;

[Event("test3001")]
[Workers(typeof(Guard))]
public static partial class Test3001
{

}

public sealed partial class Guard : Actor
{
    // region bad
    static void CountDown(int n)
    {
        if (n > 0)
        {
            Wait.Frames(30);
            CountDown(n - 1);           // ATL3001
        }
    }
    // endregion

    // region fix
    static void CountDownWithALoop(int n)
    {
        while (n > 0)
        {
            Wait.Frames(30);
            n = n - 1;
        }
    }
    // endregion

    [Entry(2)]
    void Start()
    {
        CountDown(3);
        CountDownWithALoop(3);
    }
}
