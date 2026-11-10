// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: bsvr0000 is Besaid village.
namespace Events.Bs_Besaid.Bsvr0000;

// SprsHan039_33 is the old woman who walks around the village. Talking to her runs her OnTalk.
public sealed partial class SprsHan039_33
{
    /// <summary>
    ///     Runs after the vanilla OnTalk: she plays motion 0x60111008 once, the one her model plays at the village's
    ///     celebration on the night after the trial (bsvr0100), and waits until it ends before she walks on.
    /// </summary>
    [Postfix(nameof(OnTalk))]
    void Dance()
    {
        Motion.Play(0x60111008, 0, 1, 0, -1);
        Motion.Wait();
    }
}
