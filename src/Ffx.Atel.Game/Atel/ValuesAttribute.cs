// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Atel;

/// <summary>
///     The <c>int</c> constants of <paramref name="constants"/> name the values of this native parameter or
///     result: <c>[Values(typeof(Battle))] int battle</c> takes <c>Battle.Kami00_00</c>. Used where the
///     values are <c>int</c> constants rather than an enum (as <see cref="Story"/>), because the scripts store
///     them in <c>int</c> variables and registers and compare them there; the decompiler writes a constant
///     with a name as the name, and the compiler reads the name as its number.
/// </summary>
/// <param name="constants">A static class of <c>public const int</c> fields.</param>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, Inherited = false)]
public sealed class ValuesAttribute(Type constants) : Attribute {
    public Type Constants { get; } = constants;
}
