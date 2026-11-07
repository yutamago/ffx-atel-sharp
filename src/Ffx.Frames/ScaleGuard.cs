// SPDX-License-Identifier: MIT

using System;
using System.Threading;

namespace Ffx.Frames;

/// <summary>
///     Keeps a saved fast-forward scale from breaking the start-up: the session starts at ×1 and the scale ramps up to
///     the saved one over the first seconds of the world, and a scale above ×1 is written to the settings file only
///     once the game has run at it for a while (<see cref="CheckSeconds"/>).
/// </summary>
/// <remarks>
///     A scale that crashed the game was saved and crashed it again on the next start-up, on the first world frame:
///     that frame follows a load, its real time hits the 0.3 s cap, and it owes 9 × the scale ticks at once
///     (docs/research/frame-timing.md, "Fast-forward: why ×18 crashed"). Slow motion (×1 and below) is never limited:
///     it runs fewer ticks than the game.
/// </remarks>
public sealed class ScaleGuard {
    /// <summary>The ramp doubles the most scale allowed once per this many seconds of world frames.</summary>
    public const double DoublingSeconds = 1.0;

    /// <summary>How long the game runs at a scale before it is written to the settings file.</summary>
    public const double CheckSeconds = 5.0;

    /// <summary>The most real time one frame adds to the ramp and the check (a load does not count as playing).</summary>
    public const double MaxStep = 0.1;

    private double _limit = 1;
    private double _checked;
    private double _candidate = double.NaN;
    private double _stable;

    /// <param name="saved">The scale the settings file holds.</param>
    public ScaleGuard(double saved) {
        _checked = saved is > 0 and <= 1 ? saved : 1;
    }

    /// <summary>The most scale above ×1 allowed now: 1 at start-up, doubling each <see cref="DoublingSeconds"/> of world frames.</summary>
    public double Limit => Volatile.Read(ref _limit);

    /// <summary>Whether the ramp has reached <see cref="FrameSettings.MaxScale"/> and limits nothing any more.</summary>
    public bool Done => Limit >= FrameSettings.MaxScale;

    /// <summary>The highest scale the game ran at for <see cref="CheckSeconds"/> this session (or the saved one at or below ×1).</summary>
    public double Checked => Volatile.Read(ref _checked);

    /// <summary>The scale to run when <paramref name="asked"/> is asked: limited by the ramp above ×1.</summary>
    public double Effective(double asked) => asked > 1 ? Math.Max(1, Math.Min(asked, Limit)) : asked;

    /// <summary>
    ///     A frame of the world (a tick or a redraw, not a menu, a load or a movie) that ran at <paramref name="scale"/>
    ///     (1 where the scale was held at ×1) for <paramref name="dt"/> seconds of real time.
    /// </summary>
    public void WorldFrame(double dt, double scale) {
        if (!(dt > 0) || double.IsInfinity(dt)) return;
        dt = Math.Min(dt, MaxStep);
        if (!Done) Volatile.Write(ref _limit, Math.Min(FrameSettings.MaxScale, _limit * Math.Pow(2, dt / DoublingSeconds)));
        if (!(Math.Abs(scale - _candidate) <= 1e-3 * Math.Max(1, scale))) {
            _candidate = scale;
            _stable = 0;
            return;
        }
        _stable += dt;
        if (_stable >= CheckSeconds && _candidate > _checked) Volatile.Write(ref _checked, _candidate);
    }

    /// <summary>The scale to write to the settings file when <paramref name="asked"/> is set: at most the checked one above ×1.</summary>
    public double Persisted(double asked) => asked > 1 ? Math.Max(1, Math.Min(asked, Checked)) : asked;
}
