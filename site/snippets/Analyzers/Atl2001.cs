// SPDX-License-Identifier: MIT
#pragma warning disable ATL2001, CS4014 // the bad example: SnippetTests checks that it raises ATL2001
using Ffx;
using Ffx.Atel;
using static Snippets.Atl2001.Test2001;

namespace Snippets.Atl2001;

[Event("test2001")]
[Workers(typeof(Director), typeof(Lulu))]
public static partial class Test2001
{

}

public sealed partial class Lulu : Actor
{
    [Entry(2)]
    public static async Task LookAroundAsync()
    {
        Turn.To(1.5f);
    }
}

public sealed partial class Director : LogicWorker
{
    // region bad
    [Entry(2)]
    void Start()
    {
        Lulu.LookAroundAsync();         // ATL2001
    }
    // endregion

    // region fix
    [Entry(3)]
    async void StartAndWait()
    {
        _ = Lulu.LookAroundAsync();     // queue it and carry on
        await Lulu.LookAroundAsync();   // or wait until it has ended
    }
    // endregion
}
