// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Frames;

/// <summary>
///     How one drawn frame runs its ticks: as <see cref="Loops"/> whole game updates (<c>Sg_MainLoop</c>, each with
///     its own draw, like the booster), each running some ticks of the world update (the exec frames,
///     <c>Sg_SetExecFrames</c>, at most 99 each).
/// </summary>
/// <remarks>
///     Whole updates are exact: everything that runs once per <c>Sg_MainLoop</c> (the characters' middle step, the
///     field animation, the fades, the pad) keeps pace with the world. Exec frames are cheaper (one draw for many
///     ticks) but leave those once-per-update parts behind (docs/research/frame-timing.md, "(b) Fast-forward"), so
///     the plan uses them only past <c>maxLoops</c> ticks: at ×1 to ×4 at 30 fps every tick is a whole update.
/// </remarks>
public readonly record struct TickPlan(int Ticks, int Loops) {
    /// <summary>The most ticks one update's world loop runs (<c>Sg_SetExecFrames</c> clamps to 0..99).</summary>
    public const int MaxExec = 99;

    /// <summary>No ticks: a frame that only draws.</summary>
    public static TickPlan None => new(0, 0);

    /// <summary>
    ///     Spreads <paramref name="ticks"/> over at most <paramref name="maxLoops"/> updates, as evenly as possible.
    ///     More than <paramref name="maxLoops"/> × 99 ticks are cut to that.
    /// </summary>
    public static TickPlan For(int ticks, int maxLoops) {
        if (ticks <= 0) return None;
        maxLoops = Math.Max(1, maxLoops);
        ticks = Math.Min(ticks, maxLoops * MaxExec);
        return new TickPlan(ticks, Math.Min(ticks, maxLoops));
    }

    /// <summary>The ticks update <paramref name="loop"/> (0-based) runs; 0 past the last.</summary>
    public int ExecFor(int loop) {
        if (Loops <= 0 || loop < 0 || loop >= Loops) return 0;
        int each = Ticks / Loops, extra = Ticks % Loops;
        return each + (loop < extra ? 1 : 0);
    }
}
