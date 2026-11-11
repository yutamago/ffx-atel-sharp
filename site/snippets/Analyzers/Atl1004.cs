// SPDX-License-Identifier: MIT
#pragma warning disable ATL1004 // the bad example: SnippetTests checks that it raises ATL1004
using Ffx;
using Ffx.Atel;
using System.Collections.Generic;
using static Snippets.Atl1004.Test1004;

namespace Snippets.Atl1004;

[Event("test1004")]
[Workers(typeof(Guard))]
public static partial class Test1004
{
    // region fix
    public static byte[] Seen = new byte[8];    // a fixed-size array of the script
    // endregion
}

public sealed partial class Guard : Actor
{
    // region bad
    [Entry(2)]
    void Remember()
    {
        var seen = new List<int>();     // ATL1004
    }
    // endregion

    // region fix-use
    [Entry(3)]
    void RememberInAnArray()
    {
        Seen[2] = 1;
    }
    // endregion
}
