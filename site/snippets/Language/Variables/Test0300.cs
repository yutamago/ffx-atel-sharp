// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Events.Test.Test0300.Test0300;

namespace Events.Test.Test0300;

// region variables
[Event("test0300")]
[Workers(typeof(Keeper))]
public static partial class Test0300
{
    [Var(Storage.Save, Save.StoryProgress)] public static ushort StoryProgress;
    [Var(Storage.Shared, 0x0000)] public static int Visits;
    [Var(Storage.Shared, 0x0004)] public static bool DoorOpen;
    [Var(Storage.Event, 0x0000)] public static float[] Heights = new float[4];
    [Var(Storage.Private, 0x0000)] public static byte[,] Grid = new byte[3, 8];
}
// endregion

[SharedData(0x10, "")]
[EventData(0x10, "")]
public static partial class Test0300
{
}

[Worker(PrivateLength = 0x20)]
[PrivateData(0x20, "")]
public sealed partial class Keeper : Actor
{
    // region registers
    [Register(0)] public static int I0;
    [Register(1)] public static float F1;
    // endregion

    protected override void Init()
    {
        return;
    }

    protected override void Main()
    {
        // region use-variables
        Visits = Visits + 1;
        DoorOpen = true;
        Heights[2] = 1.5f;
        if (StoryProgress >= Story.S00111_Bsil0000)
        {
            Grid[1, 2] = 7;
        }
        // endregion

        // region locals
        int steps = 0;
        float speed = 2f;
        bool done = Visits > 3;
        for (int i = 0; i < 4; i++)
        {
            steps = steps + i;
            Wait.Frames(1);         // steps keeps its value across the wait
        }
        // endregion
        I0 = steps;
        F1 = speed;
        DoorOpen = done;
        return;
    }
}
