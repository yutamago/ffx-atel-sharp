// SPDX-License-Identifier: MIT

using System;
using System.Threading;

namespace Ffx.Frames;

/// <summary>What the window's live numbers count.</summary>
public enum Counter {
    /// <summary>Presents (the render thread's flips): the drawn frame rate.</summary>
    Presents,
    /// <summary>Drawn frames the mod decided (<c>updateFFX</c> calls).</summary>
    Frames,
    /// <summary>Ticks the mod handed the game.</summary>
    PlannedTicks,
    /// <summary>World ticks that ran (<c>TkUpdateEventJump</c>, the scripts' tick).</summary>
    WorldTicks,
    /// <summary>Game updates (<c>Sg_MainLoop</c>).</summary>
    Updates,
    TickFrames,
    RedrawFrames,
    RepeatFrames,
}

/// <summary>
///     Counters bumped from the game and render threads, turned into rates once a second by the window (render thread).
/// </summary>
public sealed class FrameStats {
    private static readonly int N = Enum.GetValues<Counter>().Length;
    private readonly long[] _counts = new long[N];
    private readonly long[] _last = new long[N];
    private readonly double[] _rates = new double[N];
    private double _since = double.NaN;

    public void Add(Counter c, long n = 1) => Interlocked.Add(ref _counts[(int)c], n);

    public long Total(Counter c) => Interlocked.Read(ref _counts[(int)c]);

    /// <summary>The rate per second over the last full window.</summary>
    public double Rate(Counter c) => _rates[(int)c];

    /// <summary>Recomputes the rates when at least <paramref name="window"/> seconds passed since the last time (one thread only).</summary>
    /// <returns>Whether the rates changed.</returns>
    public bool Sample(double now, double window = 1.0) {
        if (double.IsNaN(_since)) {
            _since = now;
            for (int i = 0; i < N; i++) _last[i] = Interlocked.Read(ref _counts[i]);
            return false;
        }
        double span = now - _since;
        if (span < window) return false;
        for (int i = 0; i < N; i++) {
            long c = Interlocked.Read(ref _counts[i]);
            _rates[i] = (c - _last[i]) / span;
            _last[i] = c;
        }
        _since = now;
        return true;
    }
}
