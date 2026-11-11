// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: bsil0000 is Besaid beach until the party leaves the island (bsil0100 after).
namespace Events.Bs_Besaid.Bsil0000;

// W19 is the beach's map worker: its Main fades the screen in and gives the player control when Tidus arrives.
public sealed partial class W19
{
    /// <summary>
    ///     Runs inside the vanilla Main, after the statement that gives the player control: the camera shakes for
    ///     90 frames (3 seconds) on all three axes (which: 7, the bits for x, y and z), fading out. The values are
    ///     those of a shake the game itself uses.
    /// </summary>
    [Inject(nameof(Main), After = nameof(Common.ControlEnable))]
    void Quake()
    {
        Camera.Eye.Shake(ShakeKind.SineFadeOut, which: 7, speed: 3.14f, frames: 90, amplitude: 20, jitter: 0);
    }
}
