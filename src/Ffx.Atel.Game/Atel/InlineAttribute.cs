// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Atel;

/// <summary>
///     Marks a method as an inline method (a macro): a call to it is not a call, the compiler writes the
///     method's code in place of the call, with each parameter replaced by the argument given for it. The
///     code is exactly what the statements would give if written out at the call site, so a script that
///     uses one compiles to the same bytes as the written-out code (docs/encoding.md, "Inline methods").
/// </summary>
/// <remarks>
///     <para>
///         An inline method returns nothing, declares no locals and calls only natives and other inline
///         methods (never itself, directly or through others). Its parameters are <c>int</c>, <c>float</c>
///         or <c>TextId</c>, optionally with a constant default.
///     </para>
///     <para>
///         An argument is substituted where the parameter is used, as in a C macro: it is evaluated each
///         time the parameter is used, at that point. So an argument is a constant, a variable or an
///         expression of them (<c>V104[V191 + 1] + 20</c>), never a call or anything with an effect.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class InlineAttribute : Attribute {
    /// <summary>
    ///     The code may wait (it calls a native that polls): the analyzers treat a call like a wait. The
    ///     compiler checks that this is set exactly when the code calls a native that polls.
    /// </summary>
    public bool Waits { get; init; }
}
