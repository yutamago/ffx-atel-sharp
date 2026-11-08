// SPDX-License-Identifier: MIT
using Ffx.Atel;

namespace LarvaCountReader;

/// <summary>Another mod's saved field, read by its key: yuta.larvacount declares and writes it.</summary>
public static class Tally
{
    [Saved(Mod = "yuta.larvacount", Key = "LarvaCount.Progress.LarvasMet")] public static ushort LarvasMet;
}
