// SPDX-License-Identifier: MIT

namespace Ffx;

// Hand-written overloads of generated natives (Natives.g.cs): the same native, another C# type for an argument the
// native takes as it is.
public static partial class Btl
{
    /// <summary>
    ///     Prints a flag, 1 or 0, with the actor's index to the debug output: <see cref="DebugPrint(int)"/> for a
    ///     <c>bool</c> (the native prints its argument with <c>%d</c>), so a flag variable needs no <c>AsInt</c>.
    /// </summary>
    /// <remarks>Native <c>0x7015</c>, <c>Native_DebugPrint</c>, as <see cref="DebugPrint(int)"/>.</remarks>
    [Atel.Native(0x7015)]
    public static int DebugPrint(bool value) => throw Atel.Runtime.NotRunnable();
}
