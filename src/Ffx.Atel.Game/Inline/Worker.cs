// SPDX-License-Identifier: MIT

namespace Ffx.Atel;

/// <summary>
///     Inline methods every worker has, called without a class name (<c>ChangeMap(Map.Bsvr0100, 2);</c>).
///     The compiler writes the natives below in place of the call, as the game's scripts have them.
/// </summary>
public abstract partial class Worker {
    /// <summary>
    ///     Changes to map <paramref name="map"/> at entrance <paramref name="entrance"/> with a fade:
    ///     <c>SetMapFadeFrames(fadeFrames)</c>, <c>SetMapFadeType(fadeType)</c>, <c>MapChange(map, entrance)</c>.
    /// </summary>
    /// <param name="map">The map to change to.</param>
    /// <param name="entrance">The entrance of the map the party arrives at.</param>
    /// <param name="fadeFrames">How many frames the fade takes (15 in most changes).</param>
    /// <param name="fadeType">The fade's type (0 in most changes).</param>
    [Inline]
    protected static void ChangeMap(Map map, int entrance, int fadeFrames = 15, int fadeType = 0) {
        Common.SetMapFadeFrames(fadeFrames);
        Common.SetMapFadeType(fadeType);
        Common.MapChange(map, entrance);
    }

    /// <summary>
    ///     Jumps to map <paramref name="map"/> at entrance <paramref name="entrance"/>, after setting the fade:
    ///     <c>SetMapFadeFrames(fadeFrames)</c>, <c>SetMapFadeType(fadeType)</c>, <c>MapJump(map, entrance)</c>.
    /// </summary>
    /// <param name="map">The map to jump to.</param>
    /// <param name="entrance">The entrance of the map.</param>
    /// <param name="fadeFrames">How many frames the fade takes.</param>
    /// <param name="fadeType">The fade's type (0 in most jumps).</param>
    [Inline]
    protected static void JumpToMap(Map map, int entrance, int fadeFrames, int fadeType = 0) {
        Common.SetMapFadeFrames(fadeFrames);
        Common.SetMapFadeType(fadeType);
        Common.MapJump(map, entrance);
    }
}
