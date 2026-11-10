// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Events.Bs_Besaid.Bsvr0000.Bsvr0000;

// The namespace names the script: bsvr0000 is Besaid village.
namespace Events.Bs_Besaid.Bsvr0000;

/// <summary>
///     A new worker of the mod: the game starts it with the village's own workers when the map loads. Its Main prints
///     the map, the story progress and Tidus's position to the F6 overlay, once a second, until the map changes.
/// </summary>
public sealed partial class WhereAmI : LogicWorker
{
    protected override void Init()
    {
        return;
    }

    protected override void Main()
    {
        while (true)
        {
            Debug.Print($"bsvr0000, story {StoryProgress}: Tidus at x {Tidus.Pos.X:F0}, y {Tidus.Pos.Y:F0}, z {Tidus.Pos.Z:F0}");
            Wait.Frames(30);
        }
    }
}
