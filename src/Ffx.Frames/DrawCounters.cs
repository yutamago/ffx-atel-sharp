// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Frames;

/// <summary>
///     Counters that the game's draw steps itself, once per <c>Sg_MainLoop</c> that draws: on a Redraw frame they would
///     step once per drawn frame (too fast at a high frame rate). Before each tick's update the mod notes their values;
///     a Redraw update draws from those values (the same picture as the tick's), and the values the tick left are put
///     back after it.
/// </summary>
/// <remarks>
///     The counters, both on <c>Sg_MainLoop</c>'s world draw path (<c>Game.ReadDrawCounters</c>):
///     <list type="bullet">
///         <item>The title's water (<c>PhyreScene_drawMainMenuWaveEffect</c>, <c>0x0065b5b0</c>, from
///         <c>graphicDrawMainMenuWaterEffect</c> while <c>0x025d5ebc</c> is set): a flipbook of 70 textures
///         (<c>menu/titl/Water_%02d</c>), frame byte <c>0x00ccba09</c>, and a byte count <c>0x00ccba0a</c>.</item>
///         <item>The dream and recall scenes' noise (<c>graphicDrawDream</c>, <c>0x0063df70</c>, from
///         <c>recallSceneEffect</c>/<c>recallSceneEffect2</c> in <c>drawDreamEffect</c>): <c>0x00ccb46c</c> (16 bits) cycles 0, 1, 2 and
///         the speckles are drawn when it is 0.</item>
///     </list>
/// </remarks>
public sealed class DrawCounters {
    private readonly byte[] _beforeTick, _held;
    private bool _have;

    public DrawCounters(int size) {
        _beforeTick = new byte[size];
        _held = new byte[size];
    }

    /// <summary>The bytes the counters take.</summary>
    public int Size => _held.Length;

    /// <summary>Whether a tick's values are noted.</summary>
    public bool HaveTick => _have;

    /// <summary>A game update with ticks starts: notes the values it draws from.</summary>
    public void BeginTick(ReadOnlySpan<byte> now) {
        now[..Size].CopyTo(_beforeTick);
        _have = true;
    }

    /// <summary>A Redraw update starts: keeps the live values and writes the last tick's into <paramref name="counters"/>.</summary>
    public void BeginRedraw(Span<byte> counters) {
        counters[..Size].CopyTo(_held);
        if (_have) _beforeTick.CopyTo(counters);
    }

    /// <summary>A Redraw update ended: the live values back into <paramref name="counters"/>.</summary>
    public void EndRedraw(Span<byte> counters) => _held.CopyTo(counters);

    /// <summary>Forgets the tick's values (the mod off: nothing to draw again).</summary>
    public void Clear() => _have = false;
}
