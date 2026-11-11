// SPDX-License-Identifier: MIT
#pragma warning disable ATL2008 // the bad example: SnippetTests checks that it raises ATL2008
using Ffx;
using Ffx.Atel;
using static Snippets.Atl2008.Test2008;

namespace Snippets.Atl2008;

[Event("test2008")]
[Workers(typeof(Director), typeof(Counter))]
public static partial class Test2008
{
    public static int Total;
}

public sealed partial class Counter : LogicWorker
{
    [Entry(2)]
    public static async Task SetAsync(int value)
    {
        Total = value;
    }

    [Entry(3)]
    public static async Task<int> AddAsync(int a, int b)
    {
        return a + b;
    }
}

public sealed partial class Director : LogicWorker
{
    // region bad
    [Entry(2)]
    async void Start()
    {
        _ = Counter.SetAsync(1);                // ATL2008
        await Counter.SetAsync(2).Started;      // ATL2008
    }
    // endregion

    // region fix
    [Entry(3)]
    async void Add()
    {
        int sum = await Counter.AddAsync(1, 2); // one place, and it waits for the end
        sum = await Counter.AddAsync(sum, 3);
        Total = sum;
    }
    // endregion
}
