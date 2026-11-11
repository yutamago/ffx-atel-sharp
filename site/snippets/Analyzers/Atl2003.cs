// SPDX-License-Identifier: MIT
#pragma warning disable ATL2003 // the bad example: SnippetTests checks that it raises ATL2003
using Ffx;
using Ffx.Atel;
using static Snippets.Atl2003.Test2003;

namespace Snippets.Atl2003;

[Event("test2003")]
[Workers(typeof(Director))]
public static partial class Test2003
{

}

public sealed partial class Director : LogicWorker
{
    // region bad
    static Awaitable Later => default;      // an awaitable that is not a request

    [Entry(2)]
    async void Pause()
    {
        await Later;                        // ATL2003
    }
    // endregion

    // region fix
    [Entry(3)]
    void PauseForAFrame()
    {
        Wait.Frames(1);                     // a native that waits does so by itself
    }
    // endregion
}
