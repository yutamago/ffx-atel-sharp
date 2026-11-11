// SPDX-License-Identifier: MIT

namespace Ffx;

/// <summary>
///     Prints values to Fahrenheit's log while you test a mod: <c>Debug.Print($"hp = {Hp}");</c>. A script that
///     calls it runs only with the Atel loader.
/// </summary>
/// <remarks>
///     <para>
///         These are natives the Atel loader adds to the game (native group <c>F</c>, which the game leaves empty;
///         docs/formats/debug.md). This is the only place Atel takes a <c>string</c>.
///     </para>
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
    /// <summary>Writes a line to Fahrenheit's log: <c>Debug.Print($"hp = {Hp}");</c>.</summary>
    /// <remarks>Native <c>0xf000</c>: the values, their count and the text's id.</remarks>
    public static void Print(string text) => throw Atel.Runtime.NotRunnable();

    /// <summary>
    ///     Logs the worker's registers and stops in a .NET debugger attached to the game, if there is one; then
    ///     the script runs on.
    /// </summary>
    /// <remarks>Native <c>0xf001</c>.</remarks>
    public static void Break() => throw Atel.Runtime.NotRunnable();
}
