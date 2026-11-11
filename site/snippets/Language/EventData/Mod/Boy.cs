// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Events.Test.Test0800.Test0800;

namespace Events.Test.Test0800;

// region worker-data
public sealed partial class Boy
{
    static Boy()
    {
        Steps = 12;     // this worker's own data
    }
}
// endregion
