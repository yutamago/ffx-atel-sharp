// SPDX-License-Identifier: MIT
#pragma warning disable ATL2004 // the bad example: SnippetTests checks that it raises ATL2004
using Ffx;
using Ffx.Atel;
using static Snippets.Atl2004.Test2004;

namespace Snippets.Atl2004;

[Event("test2004")]
[Workers(typeof(Director))]
public static partial class Test2004
{

}

public sealed partial class Director : LogicWorker
{
    // region bad
    protected override void Init()
    {
        RequestWaitEnd(7, 0, 3);    // ATL2004: Init runs at level 7, so its own level-7 request is refused
    }
    // endregion

    // region fix
    protected override void Main()
    {
        RequestWaitEnd(1, 0, 3);    // Main runs at level 0: level 1 is above it, so entry 3 runs now
    }
    // endregion

    [Entry(2)]
    void Unused()
    {
        return;
    }

    [Entry(3)]
    void Prepare()
    {
        return;
    }
}
