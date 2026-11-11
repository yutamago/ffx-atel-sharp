// SPDX-License-Identifier: MIT
#pragma warning disable ATL1007 // the bad example: SnippetTests checks that it raises ATL1007
using Ffx;
using Ffx.Atel;
using static Snippets.Atl1007.Test1007;

namespace Snippets.Atl1007;

[Event("test1007")]
[Workers(typeof(Guard))]
public static partial class Test1007
{
    public static int Mood;
}

public sealed partial class Guard : Actor
{
    // region bad
    [Entry(2)]
    void Cheer()
    {
        void Bump() { Mood = Mood + 1; }    // ATL1007
        Bump();
        Bump();
    }
    // endregion

    // region fix
    [Entry(3)]
    void CheerWithAHelper()
    {
        Bump();
        Bump();
    }

    static void Bump()
    {
        Mood = Mood + 1;
    }
    // endregion
}
