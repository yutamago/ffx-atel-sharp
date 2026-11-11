// SPDX-License-Identifier: MIT

using Ffx.Atel;

namespace Ffx;

/// <remarks>
///     <c>Move.To(10.7f, 0f, -13.66f, 0.31415927f)</c> and the other inline methods (<see cref="InlineAttribute"/>)
///     are a whole move as one call: the compiler writes the natives they stand for in place of the call, as the
///     game's scripts have them.
/// </remarks>
public sealed partial class MoveControl {
    /// <summary>
    ///     Moves the worker to (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>), turning it
    ///     towards the point on the way, and waits until it is there: sets the turn's speeds
    ///     (<c>TurnSetSpeed</c>, <c>TurnSetSpeed2</c>), the move's target (<c>MoveSetTarget</c>), the turn's yaw
    ///     to the target (<c>TurnSetYaw(GetYawToMoveTarget())</c>), starts the turn and the move
    ///     (<c>TurnStart(0, turnMode, 0)</c>, <c>MoveStart(0, moveMode, 0)</c>), waits for the move
    ///     (<c>MoveWait</c>) and stops the turn (<c>WorkerStopTurn</c>).
    /// </summary>
    /// <param name="x">The target's x.</param>
    /// <param name="y">The target's y.</param>
    /// <param name="z">The target's z.</param>
    /// <param name="turnSpeed">The turn's speed, in radians a frame (<c>TurnSetSpeed</c>).</param>
    /// <param name="turnSpeed2">The turn's second speed (<c>TurnSetSpeed2</c>; 0.17453294, 10°, in most moves).</param>
    /// <param name="turnMode">The turn's mode (53250 in most moves).</param>
    /// <param name="moveMode">The move's mode (49153 in most moves).</param>
    [Inline(Waits = true)]
    public void To(float x, float y, float z, float turnSpeed, float turnSpeed2 = 0.17453294f, int turnMode = 53250, int moveMode = 49153) {
        Common.TurnSetSpeed(turnSpeed);
        Common.TurnSetSpeed2(turnSpeed2);
        Common.MoveSetTarget(x, y, z);
        Common.TurnSetYaw(Common.GetYawToMoveTarget());
        Common.TurnStart(0, turnMode, 0);
        Common.MoveStart(0, moveMode, 0);
        Common.MoveWait();
        Common.WorkerStopTurn(Common.GetWorkerId(-1));
    }

    /// <summary>
    ///     Starts the turn and the move set up before (<c>TurnStart(0, turnMode, 0)</c>,
    ///     <c>MoveStart(0, moveMode, 0)</c>), waits for the move (<c>MoveWait</c>) and stops the turn
    ///     (<c>WorkerStopTurn</c>).
    /// </summary>
    /// <param name="turnMode">The turn's mode.</param>
    /// <param name="moveMode">The move's mode.</param>
    [Inline(Waits = true)]
    public void StartWithTurnAndWait(int turnMode, int moveMode) {
        Common.TurnStart(0, turnMode, 0);
        Common.MoveStart(0, moveMode, 0);
        Common.MoveWait();
        Common.WorkerStopTurn(Common.GetWorkerId(-1));
    }

    /// <summary>
    ///     Moves the worker to the move's target in <paramref name="frames"/> frames and waits until it is there:
    ///     <c>MoveSetFrames(0, frames)</c>, <c>MoveStart(0, mode, 0)</c>, <c>MoveWait</c>.
    /// </summary>
    /// <param name="frames">How many frames the move takes.</param>
    /// <param name="mode">The move's mode.</param>
    [Inline(Waits = true)]
    public void InFrames(float frames, int mode) {
        Common.MoveSetFrames(0, frames);
        Common.MoveStart(0, mode, 0);
        Common.MoveWait();
    }
}
