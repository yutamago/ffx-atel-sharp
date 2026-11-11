// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: bsvr0000 is Besaid village.
namespace Events.Bs_Besaid.Bsvr0000;

// SprsHan039_33 is the old woman who walks around the village (her model is SprsHan039). Talking to her runs her OnTalk.
public sealed partial class SprsHan039_33
{
    /// <summary>Runs after the vanilla OnTalk, after her own line: one more line, a new message of the mod.</summary>
    [Postfix(nameof(OnTalk))]
    void Gossip()
    {
        Common.ControlDisable();
        // The first line of a window is the speaker's name ({name:Wakka}); the villagers have none, so it is empty.
        Dialogue.Say(Text.New("\nThey say Wakka once kicked a blitzball\nall the way to Kilika, ya?"));
        Common.ControlEnable();
    }
}
