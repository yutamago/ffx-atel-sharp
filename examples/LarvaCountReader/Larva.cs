// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

namespace Monsters.M209;

public sealed partial class Ai
{
    /// <summary>After the vanilla Init (and Larva Count's postfix, which loads first): the tally so far.</summary>
    [Postfix(nameof(Init))]
    void ShowTally()
    {
        Debug.Print($"Larva Count Reader: {LarvaCountReader.Tally.LarvasMet} Larva(s) met so far");
    }
}
