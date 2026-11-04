// SPDX-License-Identifier: MIT

using Ffx.Atel;

namespace Ffx;

/// <summary>
///     The worker's turn as one call (<c>Turn.To(1.57f)</c>). Each method is an inline method
///     (<see cref="InlineAttribute"/>): the compiler writes the natives below in place of the call, as the
///     game's scripts have them, so the call compiles to the same bytes as the lines it stands for.
/// </summary>
public sealed partial class TurnControl {
    /// <summary>
    ///     Turns the worker to <paramref name="yaw"/> with the model's turning motion, and waits until it has
    ///     turned: sets the turn's yaw (<c>TurnSetYaw</c>), asks which kind of turn that is
    ///     (<c>GetTurnKind</c>, kept in register <c>I3</c> as the game's code has it), plays that kind's motion
    ///     (<c>GetTurnMotion</c>), sets the turn's length from it (<c>GetTurnFrames(kind, small, large)</c>),
    ///     starts the turn (<c>TurnStart(0, mode, 0)</c>), waits for it (<c>TurnWait</c>) and releases the motion
    ///     (<c>ReleaseMotion</c>). Overwrites <c>I3</c>.
    /// </summary>
    /// <param name="yaw">The yaw to turn to, in radians; evaluated once, first (it may be a call: <c>Move.YawToTarget</c>).</param>
    /// <param name="small">The turn's frames for a small turn (<c>GetTurnFrames</c>'s second value; 15 in most turns).</param>
    /// <param name="large">The turn's frames for a large turn (30 in most turns).</param>
    /// <param name="mode">The turn's mode (<c>TurnStart</c>'s second value; 4099 in most turns).</param>
    [Inline(Waits = true)]
    [InlineLocal("kind", 3)]
    public void To([Eager] float yaw, int small = 15, int large = 30, int mode = 4099) {
        Common.TurnSetYaw(yaw);
        int kind = Common.GetTurnKind();
        Chr.SetMotion(Common.GetTurnMotion(kind));
        Common.TurnSetFrames(0f, Common.GetTurnFrames(kind, small, large));
        Common.TurnStart(0, mode, 0);
        Common.TurnWait();
        Chr.ReleaseMotion();
    }

    /// <summary>
    ///     Turns the worker to <paramref name="yaw"/> in <paramref name="frames"/> frames and waits until it has
    ///     turned: <c>TurnSetFrames(0, frames)</c>, <c>TurnSetYaw</c>, <c>TurnStart(0, mode, 0)</c>, <c>TurnWait</c>.
    ///     No motion is played.
    /// </summary>
    /// <param name="yaw">The yaw to turn to, in radians.</param>
    /// <param name="frames">How many frames the turn takes.</param>
    /// <param name="mode">The turn's mode (<c>TurnStart</c>'s second value; 4099 in most turns).</param>
    [Inline(Waits = true)]
    public void InFrames(float frames, float yaw, int mode = 4099) {
        Common.TurnSetFrames(0f, frames);
        Common.TurnSetYaw(yaw);
        Common.TurnStart(0, mode, 0);
        Common.TurnWait();
    }
}
