// SPDX-License-Identifier: MIT
#pragma warning disable ATL1005 // the bad example: SnippetTests checks that it raises ATL1005
using Ffx;
using Ffx.Atel;
using System.Linq;
using static Snippets.Atl1005.Test1005;

namespace Snippets.Atl1005;

[Event("test1005")]
[Workers(typeof(Guard))]
public static partial class Test1005
{
    public static int[] Counts = new int[4];
    public static int Sum;
}

public sealed partial class Guard : Actor
{
    // region bad
    [Entry(2)]
    void Total()
    {
        Sum = Counts.Sum();                 // ATL1005
    }
    // endregion

    // region fix
    [Entry(3)]
    void TotalWithALoop()
    {
        Sum = 0;
        for (int i = 0; i < 4; i++)
        {
            Sum = Sum + Counts[i];
        }
    }
    // endregion
}
