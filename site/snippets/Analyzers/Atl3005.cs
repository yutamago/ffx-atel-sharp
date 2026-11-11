// SPDX-License-Identifier: MIT
#pragma warning disable ATL3005 // the bad example: SnippetTests checks that it raises ATL3005
using Ffx;
using Ffx.Atel;
using static Snippets.Atl3005.Test3005;

namespace Snippets.Atl3005;

[Event("test3005")]
[Workers(typeof(Guard))]
public static partial class Test3005
{
    public static int Frames;
}

public sealed partial class Guard : Actor
{
    [Register(0)] public static float F0;

    // region bad
    [Entry(2)]
    void Pause()
    {
        Wait.Seconds(F0);           // ATL3005
    }
    // endregion

    // region fix
    [Entry(3)]
    void PauseFor()
    {
        const float Pause = 1.5f;
        Wait.Seconds(Pause);        // a constant: Wait.Frames(45)
        Wait.Frames(Frames);        // a value known only at run time: count frames
    }
    // endregion
}
