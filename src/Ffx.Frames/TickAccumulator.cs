// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Frames;

/// <summary>
///     The fixed-step accumulator, as Unity's <c>FixedUpdate</c> loop: each drawn frame adds its real time, scaled,
///     and runs as many whole ticks as fit, 0 included; the rest carries to the next frame.
/// </summary>
/// <remarks>
///     <para>
///         FFX.exe's own accumulator (<c>Sg_FrameSync</c>, <c>0x00821e80</c>) never runs 0 ticks: with fewer than
///         2 due at 60 Hz it runs 1 and drops its carries, so a frame drawn faster than 1/30 s still ticks once
///         (docs/research/frame-timing.md, "Unity's timing model"). This one runs
///         ⌊(carry + min(dt, cap) × scale) × rate⌋ ticks, keeps the fraction, and is what the mod hands the game
///         instead.
///     </para>
///     <para>
///         The cap applies to the real time, before the scale, as the game's 0.3 s does: it keeps a hitch (a load,
///         a window drag) from turning into a burst of catch-up ticks, but lets a time scale of 100 run 100 times
///         as many ticks as the frame's time is worth. <c>maxTicks</c> bounds one frame's work; past it the excess is
///         dropped (the game then runs slower than asked, but stays responsive).
///     </para>
/// </remarks>
public sealed class TickAccumulator {
    /// <summary>A tick that is due up to this fraction short still runs, so that exact rates (60 Hz on a 30 Hz tick) don't jitter by a frame from rounding.</summary>
    private const double Slack = 1e-6;

    /// <summary>Game time not yet ticked, in seconds: at least 0 and less than one tick.</summary>
    public double Carry { get; private set; }

    /// <summary>The tick rate of the last <see cref="Advance"/>.</summary>
    public double Rate { get; private set; } = 30;

    /// <summary>How far the world is between its last tick and the next one, 0 to 1: the interpolation factor.</summary>
    public double Fraction => Math.Clamp(Carry * Rate, 0, 1);

    /// <summary>Forgets the carry (the mod switched on or off, a state the accumulator does not cover).</summary>
    public void Reset() => Carry = 0;

    /// <summary>
    ///     Adds one drawn frame and returns the ticks it runs.
    /// </summary>
    /// <param name="dt">The frame's real time in seconds; not a number or not above 0 counts as 0.</param>
    /// <param name="scale">The time scale (1 = real time); not above 0 counts as 0.</param>
    /// <param name="rate">Ticks per second of game time (30 in the world, 60 in the main menu).</param>
    /// <param name="cap">The most real time one frame counts, in seconds.</param>
    /// <param name="maxTicks">The most ticks one frame runs (at least 1).</param>
    public int Advance(double dt, double scale, double rate, double cap, int maxTicks) {
        if (!(rate > 0)) throw new ArgumentOutOfRangeException(nameof(rate), rate, "the tick rate must be above 0");
        if (!(dt > 0)) dt = 0;
        if (!(scale > 0)) scale = 0;
        if (cap > 0 && dt > cap) dt = cap;
        maxTicks = Math.Max(1, maxTicks);
        Rate = rate;
        double due = (Carry + dt * scale) * rate;
        if (double.IsInfinity(due) || double.IsNaN(due)) due = maxTicks;
        double whole = Math.Floor(due + Slack);
        if (whole >= maxTicks) {
            // Too much for one frame: run the most allowed and keep only the part of a tick.
            Carry = Math.Max(0, due - Math.Floor(due)) / rate;
            if (Carry * rate >= 1 - Slack) Carry = 0;
            return maxTicks;
        }
        Carry = Math.Max(0, due - whole) / rate;
        return (int)whole;
    }
}
