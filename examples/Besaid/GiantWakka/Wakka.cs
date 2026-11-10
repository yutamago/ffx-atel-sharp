// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

// The namespace names the script: bsvr0001 is Besaid village on the night after the trial, when Tidus can walk
// around and Wakka stands in the square.
namespace Events.Bs_Besaid.Bsvr0001;

// Wakka is the actor worker that loads Wakka's model (worker 2).
public sealed partial class Wakka
{
    /// <summary>Runs after the vanilla Init, which loads Wakka's model when the map loads: the model doubles in size.</summary>
    [Postfix(nameof(Init))]
    void Giant()
    {
        Chr.SetScaleAll(2);
    }
}
