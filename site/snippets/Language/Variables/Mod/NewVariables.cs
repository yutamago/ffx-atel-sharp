// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Events.Test.Test0300.Test0300;

namespace Events.Test.Test0300;

// region mod-variables
public static partial class Test0300
{
    public static int Greetings;                // a new variable: the linker places it
    public static byte[] Odds = { 10, 20, 70 }; // a new table with its values
    [Saved] public static short Wins;           // kept with the save, only this script uses it
}
// endregion

public sealed partial class Keeper
{
    // region use-saved
    [Postfix(nameof(Main))]
    void CountVisit()
    {
        Greetings = Greetings + 1;
        MyMod.Progress.TimesMet += 1;
        if (MyMod.Progress.TimesMet >= 10)
        {
            MyMod.Progress.Flags[0] = 1;
        }
    }
    // endregion
}
