// SPDX-License-Identifier: MIT

using System.IO;

using Fahrenheit;

namespace Ffx.Atel.Loader;

/// <summary>
///     The Atel loader: links the enabled Atel mods onto the player's scripts and gives the game
///     the linked files. Phase 2 serves one recompiled script; see docs/plan.md.
/// </summary>
[FhLoad(FhGameId.FFX)]
public sealed class AtelLoaderModule : FhModule {
    public override bool init(FhModContext mod_context, FileStream global_state_file) {
        _logger.Info("Atel loader: nothing to link yet.");
        return true;
    }
}
