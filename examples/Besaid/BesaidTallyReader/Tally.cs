// SPDX-License-Identifier: MIT
using Ffx.Atel;

namespace BesaidTallyReader;

/// <summary>Another mod's saved field, read by its key: your-name.besaidtally declares and writes it.</summary>
public static class Tally
{
    [Saved(Mod = "your-name.besaidtally", Key = "BesaidTally.Tally.Defeated")] public static ushort Defeated;
}
