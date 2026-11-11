// SPDX-License-Identifier: MIT
#pragma warning disable ATL4004 // the bad example: SnippetTests checks that it raises ATL4004
using Ffx;
using Ffx.Atel;
using static Snippets.Atl4004.Test4004;

namespace Snippets.Atl4004;

[Monster("m4004")]
[Workers(typeof(Ai))]
public static partial class Test4004
{
    public static int Met;
}

// region fix
public static class Tally
{
    [Saved(Mod = "yuta.mymod", Key = "MyMod.Progress.TimesMet")] public static ushort TimesMet;
}
// endregion

public sealed partial class Ai : BattleWorker
{
    // region bad
    [Postfix(nameof(Init))]
    void Reset()
    {
        Tally.TimesMet = 0;             // ATL4004
    }
    // endregion

    // region fix-use
    [Postfix(nameof(Main))]
    void ReadIt()
    {
        Met = Tally.TimesMet;           // reading it is fine
    }
    // endregion
}
