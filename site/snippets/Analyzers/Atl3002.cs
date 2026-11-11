// SPDX-License-Identifier: MIT
#pragma warning disable ATL3002 // the bad example: SnippetTests checks that it raises ATL3002
using Ffx;
using Ffx.Atel;
using static Snippets.Atl3002.Test3002;

namespace Snippets.Atl3002;

[Event("test3002")]
[Workers(typeof(Guard))]
public static partial class Test3002
{

}

public sealed partial class Guard : Actor
{
    // region bad
    [Entry(2)]
    void Start()
    {
        Prepare();                      // ATL3002: Start → Prepare → Dress → Comb → Part
    }

    static void Prepare() { Dress(); }
    static void Dress() { Comb(); }
    static void Comb() { Part(); }
    static void Part() { Wait.Frames(1); }
    // endregion

    // region fix
    [Entry(3)]
    void StartFlat()
    {
        Dress2();                       // StartFlat → Dress2 → Comb2 → Part2: 3 deep
    }

    static void Dress2() { Comb2(); }
    static void Comb2() { Part2(); }
    static void Part2() { Wait.Frames(1); }
    // endregion
}
