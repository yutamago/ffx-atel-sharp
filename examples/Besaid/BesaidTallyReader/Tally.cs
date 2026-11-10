// SPDX-License-Identifier: MIT
using Ffx.Atel;

namespace BesaidTallyReader;

/// <summary>Another mod's saved field, read by its key: yuta.besaidtally declares and writes it.</summary>
public static class Tally
{
    [Saved(Mod = "yuta.besaidtally", Key = "BesaidTally.Tally.Defeated")] public static ushort Defeated;
}
