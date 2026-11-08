// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ffx.Frames;

/// <summary>
///     The world matrices <c>Ch_DrawMain</c> handed the renderer corrected (drawn between ticks) in one game update, so that
///     a camera cut found later in the same update can send the game's own again.
/// </summary>
/// <remarks>
///     An entry is only good for the update that made it: an update that does not run <c>Ch_DrawMain</c> (a scene change,
///     a load) can free the characters' instance lists, and writing through one then crashed the game (2026-11-08, the
///     opening's flashback: <c>graphicCharacterInstanceSetWorldMatrix</c> on a list whose instance was gone). So
///     <see cref="Take"/> gives nothing from another update, and each entry also remembers the list's count and instance
///     array as they were, for the caller to check against the live list before it writes.
/// </remarks>
public sealed class WorldMatrixLog {
    /// <summary>One call: the instance list, the game's matrix, the model (−1: the whole list), and the list as it was.</summary>
    public readonly record struct Entry(nint List, Matrix4x4 Truth, int Model, uint Count, nint Instances);

    private readonly List<Entry> _entries = [];
    private int _update = int.MinValue;

    /// <summary>Entries held.</summary>
    public int Count => _entries.Count;

    /// <summary>A new game update (<c>Sg_MainLoop</c>): what the last one logged is dropped.</summary>
    public void BeginUpdate(int update) {
        _update = update;
        _entries.Clear();
    }

    /// <summary>Logs a corrected call in update <paramref name="update"/>; ignored when it is not the update begun.</summary>
    public void Add(int update, in Entry e) {
        if (update == _update) _entries.Add(e);
    }

    /// <summary>
    ///     The entries of update <paramref name="update"/> whose list <paramref name="intact"/> says is still as logged,
    ///     into <paramref name="into"/>; the log is emptied. None for another update.
    /// </summary>
    public int Take(int update, Func<Entry, bool> intact, List<Entry> into) {
        into.Clear();
        if (update == _update) {
            foreach (Entry e in _entries) {
                if (intact(e)) into.Add(e);
            }
        }
        _entries.Clear();
        return into.Count;
    }

    /// <summary>
    ///     Whether a live instance list is the one logged and safe for <c>graphicCharacterInstanceSetWorldMatrix</c>: the
    ///     same count (low 31 bits) and array, at most <paramref name="max"/> instances, and none of them null (the game's
    ///     function reads each one).
    /// </summary>
    public static bool Intact(in Entry e, uint countWord, nint instances, Func<int, nint> instanceAt, int max = 64) {
        uint n = countWord & 0x7fffffff;
        if (e.List == 0 || instances == 0 || n == 0 || n > max || n != e.Count || instances != e.Instances) return false;
        for (int i = 0; i < n; i++) {
            if (instanceAt(i) == 0) return false;
        }
        return true;
    }
}
