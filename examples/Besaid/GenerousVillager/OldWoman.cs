// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: bsvr0000 is Besaid village.
namespace Events.Bs_Besaid.Bsvr0000;

// SprsHan039_33 is the old woman who walks around the village. Talking to her runs her OnTalk.
public sealed partial class SprsHan039_33
{
    /// <summary>Runs after the vanilla OnTalk: the first time in this save, she gives 1,000 gil.</summary>
    [Postfix(nameof(OnTalk))]
    void GiveGil()
    {
        if (GenerousVillager.Gift.Given == 0)
        {
            GenerousVillager.Gift.Given = 1;
            Common.GiveGil(1000);
            Common.ControlDisable();
            Dialogue.Say(Text.New("\nHere, take 1,000 gil, ya?\nBuy yourself something nice."));
            Common.ControlEnable();
        }
    }
}
