// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Frames;

/// <summary>
///     How many ticks fit into one frame's update time: the cost of a tick, measured on the frames that ran some, and a
///     budget of real time per frame. It keeps fast-forward from spiralling: without it, a scale the CPU cannot keep up
///     with makes each frame longer, a longer frame owes more ticks, and the frame time climbs until the 0.3 s cap.
/// </summary>
/// <remarks>
///     The cost is the update's time divided by its ticks, averaged. That counts the frame's fixed part (its draw) into
///     each tick, so it overestimates the cost of a tick when there are few; with an update time of <c>d + w × n</c> for
///     <c>n</c> ticks, the limit settles where the update takes the budget: <c>n = (budget − d) / w</c>.
/// </remarks>
public sealed class TickBudget {
    /// <summary>How much of each new measurement goes into the average.</summary>
    public const double Smoothing = 0.25;

    /// <summary>The cost assumed before the first measurement, seconds a tick.</summary>
    public const double InitialCost = 0.001;

    /// <summary>The average real time of one tick, seconds.</summary>
    public double SecondsPerTick { get; private set; } = InitialCost;

    /// <summary>Forgets the measurements.</summary>
    public void Reset() => SecondsPerTick = InitialCost;

    /// <summary>
    ///     Adds a frame that ran <paramref name="ticks"/> ticks in <paramref name="seconds"/> of update time. A
    ///     measurement is counted as at most <paramref name="budget"/> a tick, so one hitch (a load) cannot hold the
    ///     limit at 1 for long.
    /// </summary>
    public void Observe(double seconds, int ticks, double budget) {
        if (ticks <= 0 || !(seconds >= 0) || double.IsInfinity(seconds)) return;
        double each = seconds / ticks;
        if (budget > 0) each = Math.Min(each, budget);
        SecondsPerTick += (each - SecondsPerTick) * Smoothing;
    }

    /// <summary>The ticks that fit into <paramref name="budget"/> seconds, at least 1; <see cref="int.MaxValue"/> without a budget.</summary>
    public int Ticks(double budget) {
        if (!(budget > 0) || double.IsInfinity(budget)) return int.MaxValue;
        double n = Math.Floor(budget / Math.Max(SecondsPerTick, 1e-7));
        return n >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)n);
    }
}
