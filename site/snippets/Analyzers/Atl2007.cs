// SPDX-License-Identifier: MIT
#pragma warning disable ATL2007 // the bad example: SnippetTests checks that it raises ATL2007
using Ffx;
using Ffx.Atel;
using static Snippets.Atl2007.Test2007;

namespace Snippets.Atl2007;

[Event("test2007")]
[Workers(typeof(Lulu))]
public static partial class Test2007
{

}

public sealed partial class Lulu : Actor
{
    // region bad
    public static async void LookAsync()    // ATL2007
    {
        Turn.To(1.5f);
    }
    // endregion

    // region fix
    public static async Task LookAroundAsync()
    {
        Turn.To(1.5f);
    }
    // endregion
}
