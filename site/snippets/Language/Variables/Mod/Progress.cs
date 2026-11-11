// SPDX-License-Identifier: MIT
using Ffx.Atel;

// region saved
namespace MyMod;

public static class Progress
{
    [Saved] public static ushort TimesMet;              // 0 on a new game
    [Saved] public static byte Tier = 1;                // 1 on a new game
    [Saved] public static byte[] Flags = new byte[8];
    [Saved(Key = "MyMod.Progress.Met")] public static ushort Seen;   // renamed, keeps its old key
}
// endregion
