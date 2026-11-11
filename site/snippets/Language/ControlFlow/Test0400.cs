// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Events.Test.Test0400.Test0400;

namespace Events.Test.Test0400;

[Event("test0400")]
[Workers(typeof(Guard))]
[SharedData(0x10, "")]
public static partial class Test0400
{
    [Var(Storage.Save, Save.StoryProgress)] public static ushort StoryProgress;
    [Var(Storage.Shared, 0x0000)] public static int Mood;
    [Var(Storage.Shared, 0x0004)] public static int Flags;
    [Var(Storage.Shared, 0x0008)] public static bool Warned;
}

public sealed partial class Guard : Actor
{
    protected override void Init()
    {
        return;
    }

    protected override void Main()
    {
        // region if-else
        if (Mood > 3 && !Warned)
        {
            Warned = true;
        }
        else if (Mood < 0 || Common.Random16() < 100)
        {
            Mood = 0;
        }
        // endregion

        // region loops
        while (Mood < 10)
        {
            Mood = Mood + 1;
            Wait.Frames(1);
        }

        do
        {
            Mood = Mood - 1;
        } while (Mood > 5);

        for (int i = 0; i < 3; i++)
        {
            Motion.Play(0x71050, 0, 1, 0, -1);
            Motion.Wait();
        }
        // endregion

        // region switch
        switch (StoryProgress)
        {
            case < Story.S00111_Bsil0000:
                Mood = 1;
                break;
            case >= Story.S00111_Bsil0000 and <= Story.S00116_Bsil0000:
                Mood = 2;
                break;
            default:
                Mood = 3;
                break;
        }
        // endregion

        // region conditions
        if (AsBool(Flags & 4))      // any value as a condition
        {
            Mood = AsInt(Mood > 2); // a condition as a value: 0 or 1
        }
        if (Mood > 1 & Common.Random16() < 50)
        {
            Mood = 0;               // & runs both sides: Random16 is always called
        }
        // endregion

        // region dead-code
        if (false)
        {
            Mood = 99;              // kept in the script, never runs
        }
        // endregion
        return;
    }
}
