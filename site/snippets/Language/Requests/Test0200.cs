// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Events.Test.Test0200.Test0200;

namespace Events.Test.Test0200;

[Event("test0200")]
[Workers(typeof(Director), typeof(Lulu), typeof(Counter))]
[SharedData(0x20, "")]
public static partial class Test0200
{
    [Var(Storage.Shared, 0x0000)] public static int Looks;
}

// region requestable
public sealed partial class Lulu : Actor
{
    protected override void Init()
    {
        return;
    }

    protected override void Main()
    {
        Wait.Forever();
        return;
    }

    [Entry(2)]
    public static async Task LookAroundAsync(Priority priority = Priority.P1)
    {
        Looks = Looks + 1;
        Turn.To(1.5f);
        Turn.To(-1.5f);
        return;
    }
}
// endregion

public sealed partial class Director : LogicWorker
{
    protected override void Init()
    {
        return;
    }

    protected override async void Main()
    {
        // region request-forms
        _ = Lulu.LookAroundAsync();                 // queue it and carry on
        await Lulu.LookAroundAsync();               // wait until it has ended
        await Lulu.LookAroundAsync().Started;       // wait until it has begun
        _ = Lulu.LookAroundAsync(Priority.P3);      // at level 3
        // endregion

        // region parameters-call
        int total = await Counter.AddAsync(2, 3);
        // endregion
        Looks = total;

        // region wait-seconds
        Wait.Seconds(1.5f);         // the same as Wait.Frames(45)
        Wait.Frames(45);
        const float Pause = 0.5f;
        Wait.Seconds(Pause);        // a const works too
        // endregion
        Wait.Forever();
        return;
    }
}

public sealed partial class Counter : LogicWorker
{
    protected override void Init()
    {
        return;
    }

    protected override void Main()
    {
        Wait.Forever();
        return;
    }

    // region parameters
    [Entry(2)]
    public static async Task<int> AddAsync(int a, int b)
    {
        return a + b;
    }
    // endregion
}
