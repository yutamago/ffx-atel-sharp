// SPDX-License-Identifier: MIT

namespace Ffx.Frames;

/// <summary>
///     What the game's frame limiter runs at. <c>Game_Main</c>'s loop (<c>0x0062ee50</c>) starts a frame once
///     <c>vsyncs × perVsync</c> seconds have passed since the last one and sleeps away the rest; the game multiplies
///     <c>g_FrameVsyncs</c> (<c>0x00c30e88</c>: 2 in the world, 1 in the main menu) by 1/59.94 s. The mod points those
///     two operands at its own values (<see cref="Limiter"/>).
/// </summary>
public static class FramePacing {
    /// <summary>The game's seconds per vsync: 1/59.94 (<c>0x00b412d8</c>).</summary>
    public const double GameVsync = 1001.0 / 60000.0;

    /// <summary>The frame interval for <paramref name="target"/>, in seconds; null for the game's own.</summary>
    /// <param name="monitorHz">The window's monitor refresh rate; 0 when unknown (then 60).</param>
    public static double? Interval(FpsTarget target, double fixedFps, double monitorHz) => target switch {
        FpsTarget.Game => null,
        FpsTarget.Monitor => 1.0 / (monitorHz >= FrameSettings.MinFps ? monitorHz : 60.0),
        FpsTarget.Fixed => 1.0 / System.Math.Clamp(fixedFps, FrameSettings.MinFps, FrameSettings.MaxFps),
        _ => 0.0,
    };

    /// <summary>
    ///     The limiter's two operands: the vsync count it loads and the seconds per vsync it multiplies by. For the
    ///     game's own pacing, its vsync count and 1/59.94 s; otherwise 1 and the interval (0: no wait).
    /// </summary>
    public static (int Vsyncs, double PerVsync) Limiter(double? interval, int gameVsyncs) =>
        interval is double s ? (1, s) : (gameVsyncs, GameVsync);
}
