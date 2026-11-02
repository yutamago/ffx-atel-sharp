// SPDX-License-Identifier: MIT

namespace Ffx;

/// <summary>
///     Debug output, through natives the Atel loader adds to the game (native group <c>F</c>, which the
///     game leaves empty; docs/formats/debug.md). The only place Atel takes a <c>string</c>.
/// </summary>
/// <remarks>
///     <para>
///         The game does not have these natives: a script that calls them runs only with the loader.
///         Built with <c>--debug drop</c> (<c>atel compile</c>, <c>atel compile-mod</c>) every call is left
///         out, its arguments included, as with <c>[Conditional]</c>; with <c>--debug error</c> a call is an
///         error.
///     </para>
///     <para>
///         The text is a string literal or an interpolated string. Each hole is an Atel expression
///         (<c>int</c>, <c>float</c>, <c>bool</c>, at most 16 per call) and may have an alignment and a .NET
///         format string: <c>$"hp {Hp,5} pos {X:F2}"</c>.
///     </para>
/// </remarks>
public static class Debug {
    /// <summary>
    ///     Writes a line to Fahrenheit's log: <c>Debug.Print($"hp = {Hp}");</c> (native <c>0xf000</c>: the
    ///     values, their count and the text's id).
    /// </summary>
    public static void Print(string text) => throw Atel.Runtime.NotRunnable();

    /// <summary>
    ///     Logs the worker's registers and stops in a .NET debugger attached to the game, if there is one
    ///     (native <c>0xf001</c>); the script then runs on.
    /// </summary>
    public static void Break() => throw Atel.Runtime.NotRunnable();
}
