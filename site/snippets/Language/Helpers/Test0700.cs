// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Events.Test.Test0700.Test0700;

namespace Events.Test.Test0700;

// region script-helper
[Event("test0700")]
[Workers(typeof(Chef))]
[SharedData(0x10, "")]
public static partial class Test0700
{
    [Var(Storage.Shared, 0x0000)] public static int Meals;
    [Var(Storage.Shared, 0x0004)] public static int Mood;

    // Callable as Twice(…) from every worker of the script.
    public static int Twice(int x) => x * 2;
}
// endregion

public sealed partial class Chef : Actor
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

    // region helper
    [Entry(2)]
    void OnTalk()
    {
        Meals = Scaled(Meals, 50);
        Serve();
        return;
    }

    static int Scaled(int value, int percent)
    {
        return value + value * percent / 100;
    }

    static void Serve()
    {
        Meals = Twice(Meals);
        Wait.Frames(10);
    }
    // endregion

    // region inline
    [Inline(Waits = true)]
    static void Bow(int frames)
    {
        Motion.Play(0x71050, 0, 1, 0, -1);
        Wait.Frames(frames);
        Motion.Wait();
    }

    [Entry(3)]
    void Greet()
    {
        Bow(10);                // the three lines above, written here with 10
        Bow(Meals + 5);         // and here with Meals + 5
        return;
    }
    // endregion

    [Entry(4)]
    void Rest()
    {
        // region inline-argument
        int mood = Common.Random16();
        Dialogue.Say(Text[12], window: mood);   // a variable, not the call itself
        // endregion
        Mood = mood;
        return;
    }
}
