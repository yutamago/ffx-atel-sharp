// SPDX-License-Identifier: MIT
#pragma warning disable ATL1001 // the bad example: SnippetTests checks that it raises ATL1001
using Ffx;
using Ffx.Atel;
using static Snippets.Atl1001.Test1001;

namespace Snippets.Atl1001;

[Event("test1001")]
[Workers(typeof(Guard))]
public static partial class Test1001
{
    // region fix
    public static int[] Counts = new int[4];    // a variable of the script, with its size
    // endregion
}

public sealed partial class Guard : Actor
{
    // region bad
    [Entry(2)]
    void Count()
    {
        int[] counts = new int[4];      // ATL1001
        counts[0] = 1;
    }
    // endregion

    // region fix-use
    [Entry(3)]
    void CountInAField()
    {
        Counts[0] = 1;
    }
    // endregion
}
