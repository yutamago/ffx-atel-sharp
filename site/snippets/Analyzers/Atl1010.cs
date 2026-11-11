// SPDX-License-Identifier: MIT
#pragma warning disable ATL1010 // the bad example: SnippetTests checks that it raises ATL1010
using Ffx;
using Ffx.Atel;
using static Snippets.Atl1010.Test1010;

namespace Snippets.Atl1010;

[Event("test1010")]
[Workers(typeof(Guard))]
public static partial class Test1010
{
    public static int Mood;
}

public sealed partial class Guard : Actor
{
    // region bad
    [Entry(2)]
    void Grow()
    {
        Mood = checked(Mood + 1);       // ATL1010
    }
    // endregion

    // region fix
    [Entry(3)]
    void GrowAndWrap()
    {
        Mood = Mood + 1;                // wraps past int.MaxValue, as unchecked C# does
    }
    // endregion
}
