// SPDX-License-Identifier: MIT
#pragma warning disable ATL2002 // the bad example: SnippetTests checks that it raises ATL2002
using Ffx;
using Ffx.Atel;
using static Snippets.Atl2002.Test2002;

namespace Snippets.Atl2002;

[Event("test2002")]
[Workers(typeof(Director), typeof(Lulu))]
public static partial class Test2002
{

}

// region bad
public sealed partial class Lulu : Actor
{
    [Entry(2)]
    public static void Wave()
    {
        Motion.Play(0x71050, 0, 1, 0, -1);
    }
}

public sealed partial class Director : LogicWorker
{
    [Entry(2)]
    void Start()
    {
        Lulu.Wave();                    // ATL2002
    }
}
// endregion

// region fix
public sealed partial class Lulu
{
    [Entry(3)]
    public static async Task WaveAsync()
    {
        Motion.Play(0x71050, 0, 1, 0, -1);
    }
}

public sealed partial class Director
{
    [Entry(3)]
    void StartWaving()
    {
        _ = Lulu.WaveAsync();
    }
}
// endregion
