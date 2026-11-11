// SPDX-License-Identifier: MIT
#pragma warning disable ATL1006 // the bad example: SnippetTests checks that it raises ATL1006
using Ffx;
using Ffx.Atel;
using static Snippets.Atl1006.Test1006;

namespace Snippets.Atl1006;

[Event("test1006")]
[Workers(typeof(Guard))]
public static partial class Test1006
{
    public static int Mood;
    public static int Visits;
}

public sealed partial class Guard : Actor
{
    // region bad
    [Entry(2)]
    void Share()
    {
        try                         // ATL1006
        {
            Mood = 100 / Visits;
        }
        finally
        {
            Visits = Visits + 1;
        }
    }
    // endregion

    // region fix
    [Entry(3)]
    void ShareCarefully()
    {
        if (Visits != 0)
        {
            Mood = 100 / Visits;
        }
        Visits = Visits + 1;
    }
    // endregion
}
