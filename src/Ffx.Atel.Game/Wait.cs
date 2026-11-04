// SPDX-License-Identifier: MIT

using Ffx.Atel;

namespace Ffx;

public static partial class Wait
{
    /// <summary>
    ///     The script frames in one second: 30. A script frame is one tick of the world update (field, events,
    ///     battle), which FFX.exe runs 30 times a second at its own speed (docs/research/frame-timing.md).
    /// </summary>
    public const int FramesPerSecond = 30;

    /// <summary>
    ///     Waits <paramref name="seconds"/> seconds of game time: <see cref="Frames"/> of
    ///     <paramref name="seconds"/> × <see cref="FramesPerSecond"/>, rounded to the nearest frame (halves away
    ///     from zero). The compiler writes the frames in place of the call: <c>Wait.Seconds(1.23f)</c> compiles to
    ///     <c>Wait.Frames(37)</c> (36.9 frames), byte for byte; the decompiler writes <c>Wait.Frames</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <paramref name="seconds"/> is a constant (a literal, a <c>const</c>, or arithmetic of them;
    ///         ATL3005 otherwise, docs/analyzers.md): the frames are counted when the
    ///         script is compiled, so the script holds <c>WaitFrames(n)</c> as the game's own scripts do. The wait is
    ///         at least one frame (1/30 s); less is ATL3006.
    ///     </para>
    ///     <para>
    ///         Game time, not the wall clock: the frames run 30 a second while the game runs at its own speed, twice
    ///         or four times as fast with the PC's speed-up booster, and slower when the PC cannot keep up and the game
    ///         may not catch up. While a movie with a camera plays (<c>Native_b00a_MovieStartPicture</c>) a frame wait
    ///         counts the movie's frames, 29.97 a second.
    ///     </para>
    /// </remarks>
    /// <param name="seconds">How long to wait, in seconds of game time.</param>
    [Inline(Waits = true)]
    public static void Seconds([Constant] float seconds) => throw Atel.Runtime.NotRunnable();
}
