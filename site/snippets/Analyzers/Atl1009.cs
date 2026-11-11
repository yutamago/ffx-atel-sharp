// SPDX-License-Identifier: MIT
#pragma warning disable ATL1009, CS0219 // the bad example: SnippetTests checks that it raises ATL1009
using Ffx;
using Ffx.Atel;
using static Snippets.Atl1009.Test1009;

namespace Snippets.Atl1009;

[Event("test1009")]
[Workers(typeof(Guard))]
public static partial class Test1009
{
    public static float Speed;
}

public sealed partial class Guard : Actor
{
    // region bad
    [Entry(2)]
    void Slow()
    {
        double ratio = 0.5;             // ATL1009
        long frames = 100;              // ATL1009
    }
    // endregion

    // region fix
    [Entry(3)]
    void SlowIn32Bits()
    {
        float ratio = 0.5f;
        int frames = 100;
        Speed = Speed * ratio;
        Wait.Frames(frames);
    }
    // endregion
}
