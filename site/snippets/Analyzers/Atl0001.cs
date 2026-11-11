// SPDX-License-Identifier: MIT
#pragma warning disable ATL0001 // the bad example: SnippetTests checks that it raises ATL0001
using Ffx;
using Ffx.Atel;
using static Snippets.Atl0001.Test0001;

namespace Snippets.Atl0001;

[Event("test0001")]
[Workers(typeof(Guard))]
public static partial class Test0001
{

}

public sealed partial class Guard : Actor
{
    [Register(0)] public static int I0;
}

// region bad
public sealed partial class Guard
{
    [Entry(2)]
    void Pace()
    {
        for (I0 = 0; I0 < 10; I0++)
        {
            Wait.Frames(1);         // ATL0001: I0 holds the count across this wait
        }
    }

    [Entry(3)]
    void Reset()
    {
        I0 = 5;                     // and another entry writes I0
    }
}
// endregion

// region fix
public sealed partial class Guard
{
    [Entry(4)]
    void PaceWithALocal()
    {
        for (int i = 0; i < 10; i++)
        {
            Wait.Frames(1);         // a local keeps its value across the wait
        }
    }
}
// endregion
