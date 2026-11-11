// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Events.Test.Test0500.Test0500;

namespace Events.Test.Test0500;

public sealed partial class Wakka
{
    // region new-line
    [Postfix(nameof(OnTalk))]
    void OneMoreLine()
    {
        Dialogue.Say(Text.New("Nice throw, brudda!"));
    }
    // endregion

    // region debug
    [Prefix(nameof(OnTalk))]
    void Trace()
    {
        Debug.Print($"talks so far: {Talks}");
    }
    // endregion
}
