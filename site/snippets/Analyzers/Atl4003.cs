// SPDX-License-Identifier: MIT
#pragma warning disable ATL4003, CS8618 // the bad example: SnippetTests checks that it raises ATL4003
// SPDX-License-Identifier: MIT
using Ffx.Atel;

namespace Snippets.Atl4003;

// region bad
public static class Progress
{
    [Saved] public static byte[] Flags;         // ATL4003: a saved array needs its size
}
// endregion

// region fix
public static class Progress2
{
    [Saved] public static byte[] Flags = new byte[8];
}
// endregion
