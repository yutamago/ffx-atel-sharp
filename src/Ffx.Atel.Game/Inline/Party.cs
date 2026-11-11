// SPDX-License-Identifier: MIT

using Ffx.Atel;

namespace Ffx;

/// <summary>The party's characters: heal them all, as a save sphere does, and similar steps of the game's own scripts.</summary>
/// <remarks>Each method is an inline method (<see cref="InlineAttribute"/>): the compiler writes the natives it stands for in place of the call.</remarks>
public static class Party {
    /// <summary>
    ///     Restores the HP and the MP of characters 0-6 and 8-17 (<c>CharRestoreHp</c>, <c>CharRestoreMp</c>), in the
    ///     order the save spheres do it: 0, 1, 6, 5, 3, 2, 4, then 8, 13, 9-12 and 14-17.
    /// </summary>
    [Inline]
    public static void RestoreAll() {
        Common.CharRestoreHp(0);
        Common.CharRestoreHp(1);
        Common.CharRestoreHp(6);
        Common.CharRestoreHp(5);
        Common.CharRestoreHp(3);
        Common.CharRestoreHp(2);
        Common.CharRestoreHp(4);
        Common.CharRestoreMp(0);
        Common.CharRestoreMp(1);
        Common.CharRestoreMp(6);
        Common.CharRestoreMp(5);
        Common.CharRestoreMp(3);
        Common.CharRestoreMp(2);
        Common.CharRestoreMp(4);
        Common.CharRestoreHp(8);
        Common.CharRestoreHp(13);
        Common.CharRestoreHp(9);
        Common.CharRestoreHp(10);
        Common.CharRestoreHp(11);
        Common.CharRestoreHp(12);
        Common.CharRestoreHp(14);
        Common.CharRestoreHp(15);
        Common.CharRestoreHp(16);
        Common.CharRestoreHp(17);
        Common.CharRestoreMp(8);
        Common.CharRestoreMp(13);
        Common.CharRestoreMp(9);
        Common.CharRestoreMp(10);
        Common.CharRestoreMp(11);
        Common.CharRestoreMp(12);
        Common.CharRestoreMp(14);
        Common.CharRestoreMp(15);
        Common.CharRestoreMp(16);
        Common.CharRestoreMp(17);
    }
}
