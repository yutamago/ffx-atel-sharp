// SPDX-License-Identifier: MIT
#pragma warning disable ATL3004 // the bad example: SnippetTests checks that it raises ATL3004
using Ffx;
using Ffx.Atel;
using static Snippets.Atl3004.Test3004;

namespace Snippets.Atl3004;

[Event("test3004")]
[Workers(typeof(Guard))]
public static partial class Test3004
{

}

public sealed partial class Guard : Actor
{
    // region bad
    [Entry(2)]
    void Talk()
    {
        Dialogue.Say(Text[12], window: Common.Random16());    // ATL3004
    }
    // endregion

    // region fix
    [Entry(3)]
    void TalkInARandomWindow()
    {
        int window = Common.Random16();
        Dialogue.Say(Text[12], window: window);
    }
    // endregion
}
