// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: bsvr0000 is Besaid village.
namespace Events.Bs_Besaid.Bsvr0000;

// SprsHan039_33 is the old woman who walks around the village. Talking to her runs her OnTalk.
public sealed partial class SprsHan039_33
{
    /// <summary>
    ///     Runs after the vanilla OnTalk: she plays the celebration motion once, from its first frame to its last,
    ///     and waits until it ends before she walks on.
    /// </summary>
    [Postfix(nameof(OnTalk))]
    void Dance()
    {
        // The motion her model plays at the village's celebration on the night after the trial (bsvr0100).
        const int CelebrationMotion = 0x60111008;
        Motion.Play(CelebrationMotion, unused: 0, loops: 1, startFrame: 0, endFrame: -1);
        Motion.Wait();
    }
}
