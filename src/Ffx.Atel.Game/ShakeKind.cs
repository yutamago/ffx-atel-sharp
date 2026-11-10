// SPDX-License-Identifier: MIT

namespace Ffx;

/// <summary>
///     How a camera or screen shake moves (<c>Camera.Eye.Shake</c>, <c>Camera.Focus.Shake</c>, <c>Camera.Screen.Shake</c>):
///     the wave and how its amplitude changes over the shake's frames. Each kind is its own native in the game (its
///     handler passes the kind to <c>MsCameraShake</c> or <c>MsScreenShake</c>); the compiler picks the native by the
///     constant.
/// </summary>
/// <remarks>
///     From FFX.exe's <c>MsCameraShakeProcess</c> (<c>0x007c50f0</c>; ffx-rust <c>crates/ffx-battle/src/shake.rs</c>,
///     <c>docs/formats/battle.md</c> "Shakes"). Each frame the shake's angle steps by its speed; the wave of the angle
///     is scaled by the amplitude and a jitter (<c>(32 + j) / 32</c>, <c>j</c> random within the jitter range). The
///     frames left count down from the length: a kind that fades out multiplies by frames left / length (full at the
///     start, 0 at the end), one that fades in by (length − frames left) / length. "Blend" is
///     <c>(sin a + cos(a + φ)) / 2</c>.
/// </remarks>
public enum ShakeKind {
    /// <summary>A sine wave that fades out (kind 1).</summary>
    SineFadeOut = 1,

    /// <summary>A sine wave that fades in (kind 2).</summary>
    SineFadeIn = 2,

    /// <summary>A blend, <c>(sin a + cos(a + 0.3)) / 2</c>, that fades out (kind 3).</summary>
    BlendFadeOut = 3,

    /// <summary>A blend, <c>(sin a + cos(a + 0.3)) / 2</c>, that fades in (kind 4).</summary>
    BlendFadeIn = 4,

    /// <summary>
    ///     A jerky blend that fades out (kind 5): <c>(sin a + cos(a + 0.1)) / 2</c>, but on odd frames, and at random
    ///     (a zero low nibble of the game's random number), half the last value instead.
    /// </summary>
    JerkyFadeOut = 5,

    /// <summary>The jerky blend of <see cref="JerkyFadeOut"/>, fading in (kind 6).</summary>
    JerkyFadeIn = 6,

    /// <summary>A sine wave at full amplitude for its frames (kind 7).</summary>
    Sine = 7,

    /// <summary>A blend, <c>(sin a + cos(a + 0.1)) / 2</c>, at full amplitude for its frames (kind 8).</summary>
    Blend = 8,
}
