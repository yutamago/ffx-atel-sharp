// SPDX-License-Identifier: MIT
#pragma warning disable ATL2005 // the bad example: SnippetTests checks that it raises ATL2005
using Ffx;
using Ffx.Atel;
using static Snippets.Atl2005.Test2005;

namespace Snippets.Atl2005;

[Event("test2005")]
[Workers(typeof(Director))]
public static partial class Test2005
{

}

public sealed partial class Director : LogicWorker
{
    [Entry(2)]
    public static async Task PrepareAsync()
    {
        Wait.Frames(10);
    }

    // region bad
    [Entry(3)]
    public static async Task StartAsync()
    {
        await PrepareAsync();       // ATL2005
    }
    // endregion

    // region fix
    [Entry(4)]
    public static async Task StartWithAHelperAsync()
    {
        Prepare();                  // a helper runs at once, on this worker
    }

    static void Prepare()
    {
        Wait.Frames(10);
    }
    // endregion
}
