// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Frames;

/// <summary>
///     How one drawn frame runs its ticks: as <see cref="Loops"/> whole game updates (<c>Sg_MainLoop</c>, like the
///     booster), each running some ticks of the world update (the exec frames, <c>Sg_SetExecFrames</c>, at most 99 each).
///     Only the last update draws: the game skips the others' graphics (<c>g_GraphicSkipFrame</c>, set while
///     <c>g_VoiceHold</c> is 2 or more).
/// </summary>
/// <remarks>
///     <para>
///         Whole updates are exact: everything that runs once per <c>Sg_MainLoop</c> (the characters' middle step, the
///         field animation, the fades, the pad) keeps pace with the world. Exec frames are cheaper but leave those
///         once-per-update parts behind (docs/research/frame-timing.md, "(b) Fast-forward"), so the plan uses them only
///         past <c>maxLoops</c> ticks: at ×1 to ×4 at 30 fps every tick is a whole update.
///     </para>
///     <para>
///         The update that draws always runs one tick. Its effect loop runs one pass per exec frame, and every pass adds
///         a VFX path (<c>op_ot_draw</c> → <c>graphicVFXSyncPath</c>) to the scene's path table, which has 16 entries and
///         no bound check (<c>PhyreScene_VFXSyncPath</c>, <c>0x00654cc0</c>): a 17th path overwrites the path count and
///         the next ones write over the heap. The updates before it skip their graphics, so their passes add no paths
///         and they take all the exec frames (docs/research/frame-timing.md, "Fast-forward: why ×18 crashed").
///     </para>
/// </remarks>
public readonly record struct TickPlan(int Ticks, int Loops) {
    /// <summary>The most ticks one update's world loop runs (<c>Sg_SetExecFrames</c> clamps to 0..99).</summary>
    public const int MaxExec = 99;

    /// <summary>The fewest updates a plan with exec frames needs: one that runs them and one that draws.</summary>
    public const int MinLoopsWithExec = 2;

    /// <summary>No ticks: a frame that only draws.</summary>
    public static TickPlan None => new(0, 0);

    /// <summary>The most ticks a frame of at most <paramref name="maxLoops"/> updates runs: 99 in each but the drawing one, which runs 1.</summary>
    public static int MaxTicks(int maxLoops) => (Math.Max(MinLoopsWithExec, maxLoops) - 1) * MaxExec + 1;

    /// <summary>
    ///     Spreads <paramref name="ticks"/> over at most <paramref name="maxLoops"/> updates (at least 2 once exec
    ///     frames are needed): one tick each while they fit, else one in the last (drawing) update and the rest spread
    ///     evenly over the others. More than <see cref="MaxTicks"/> are cut to that.
    /// </summary>
    public static TickPlan For(int ticks, int maxLoops) {
        if (ticks <= 0) return None;
        maxLoops = Math.Max(1, maxLoops);
        if (ticks <= maxLoops) return new TickPlan(ticks, ticks);
        maxLoops = Math.Max(MinLoopsWithExec, maxLoops);
        ticks = Math.Min(ticks, MaxTicks(maxLoops));
        return new TickPlan(ticks, Math.Min(ticks, maxLoops));
    }

    /// <summary>Whether update <paramref name="loop"/> (0-based) is the one that draws: the last.</summary>
    public bool Draws(int loop) => Loops > 0 && loop == Loops - 1;

    /// <summary>The ticks update <paramref name="loop"/> (0-based) runs; 0 past the last. The last runs 1.</summary>
    public int ExecFor(int loop) {
        if (Loops <= 0 || loop < 0 || loop >= Loops) return 0;
        if (Loops == Ticks || Draws(loop)) return 1;
        int rest = Ticks - 1, others = Loops - 1;
        int each = rest / others, extra = rest % others;
        return each + (loop < extra ? 1 : 0);
    }
}
