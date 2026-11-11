// SPDX-License-Identifier: MIT
#pragma warning disable ATL2006 // the bad example: SnippetTests checks that it raises ATL2006
using Ffx;
using Ffx.Atel;
using static Snippets.Atl2006.Test2006;

namespace Snippets.Atl2006;

[Event("test2006")]
[Workers(typeof(Director), typeof(Lulu))]
public static partial class Test2006
{

}

public sealed partial class Lulu : Actor
{
    [Entry(2)]
    public static async Task WaveAsync(Priority priority = Priority.P1)
    {
        Motion.Play(0x71050, 0, 1, 0, -1);
    }
}

public sealed partial class Director : LogicWorker
{
    // region bad
    [Entry(2)]
    void Start()
    {
        _ = Lulu.WaveAsync(Priority.P7_Init);   // ATL2006
    }
    // endregion

    // region fix
    [Entry(3)]
    void StartAtLevel3()
    {
        _ = Lulu.WaveAsync(Priority.P3);        // a level from 1 to 6
    }
    // endregion
}
