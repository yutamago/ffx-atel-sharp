// SPDX-License-Identifier: MIT
#pragma warning disable ATL3003 // the bad example: SnippetTests checks that it raises ATL3003
using Ffx;
using Ffx.Atel;
using static Snippets.Atl3003.Test3003;

namespace Snippets.Atl3003;

[Event("test3003")]
[Workers(typeof(Guard))]
public static partial class Test3003
{

}

public sealed partial class Guard : Actor
{
    // region bad
    [Inline]
    static void Ping(int n) { Pong(n); }    // ATL3003

    [Inline]
    static void Pong(int n) { Ping(n); }    // ATL3003
    // endregion

    // region fix
    [Inline(Waits = true)]
    static void PingPong(int times)
    {
        for (int i = 0; i < times; i++)
        {
            Wait.Frames(1);
        }
    }
    // endregion

    [Entry(2)]
    void Start()
    {
        Ping(1);
        PingPong(2);
    }
}
