// SPDX-License-Identifier: MIT
#pragma warning disable ATL3006 // the bad example: SnippetTests checks that it raises ATL3006
using Ffx;
using Ffx.Atel;
using static Snippets.Atl3006.Test3006;

namespace Snippets.Atl3006;

[Event("test3006")]
[Workers(typeof(Guard))]
public static partial class Test3006
{

}

public sealed partial class Guard : Actor
{
    // region bad
    [Entry(2)]
    void Blink()
    {
        Wait.Seconds(0.01f);        // ATL3006: 0.3 of a frame
    }
    // endregion

    // region fix
    [Entry(3)]
    void BlinkForAFrame()
    {
        Wait.Seconds(1f / 30);      // one frame
    }
    // endregion
}
