// SPDX-License-Identifier: MIT
#pragma warning disable ATL1008 // the bad example: SnippetTests checks that it raises ATL1008
using Ffx;
using Ffx.Atel;
using static Snippets.Atl1008.Test1008;

namespace Snippets.Atl1008;

[Event("test1008")]
[Workers(typeof(Guard))]
public static partial class Test1008
{
    public static int Mood;
}

public sealed partial class Guard : Actor
{
    // region bad
    static T Either<T>(bool first, T a, T b) => first ? a : b;     // ATL1008
    // endregion

    // region fix
    static int Either(bool first, int a, int b) => first ? a : b;
    static float Either(bool first, float a, float b) => first ? a : b;
    // endregion

    [Entry(2)]
    void Pick()
    {
        Mood = Either(true, 1, 2);
    }
}
