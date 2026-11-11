// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Events.Test.Test0500.Test0500;

namespace Events.Test.Test0500;

[Event("test0500")]
[Workers(typeof(Wakka), typeof(Tidus))]
[SharedData(0x10, "")]
public static partial class Test0500
{
    [Var(Storage.Shared, 0x0000)] public static int Talks;
}

public sealed partial class Wakka : Actor
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
    void OnTalk()
    {
        // region say
        Dialogue.Say(Text[12]);                                 // the usual line
        Dialogue.Say(Text[13], window: 1, y: 224, style: 0);    // another window, higher up
        // endregion

        // region voiced
        AutoTalk = true;                        // Wakka's mouth moves as he speaks
        Dialogue.SayWithVoice(0x85355cb, Text[14]);
        AutoTalk = false;
        // endregion

        // region natives
        Common.MsgSetText(0, Text[15]);         // the message natives take a TextId too
        // endregion
        Talks = Talks + 1;
        return;
    }
}

public sealed partial class Tidus : Actor
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
}
