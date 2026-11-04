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
///         An inline method returns nothing and never calls itself, directly or through others. It calls
///         natives and other inline methods, with <c>if</c>, loops and <c>switch</c> around them, and may
///         read and assign the script's variables (an inline method of a worker also its registers). Its
///         parameters are <c>int</c>, <c>float</c>, <c>bool</c> or <c>TextId</c>, optionally with a constant
///         default. A local lives in a register named for it with <see cref="InlineLocalAttribute"/>.
///     </para>
///     <para>
///         An argument is substituted where the parameter is used, as in a C macro: it is evaluated each
///         time the parameter is used, at that point. So an argument is a constant, a variable or an
///         expression of them (<c>V104[V191 + 1] + 20</c>), never a call or anything with an effect;
///         except for an <see cref="EagerAttribute"/> parameter, which may take a call.
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

/// <summary>
///     A local of an inline method lives in register <paramref name="register"/> of the calling worker
///     (<c>I3</c> for an <c>int</c> local, <c>F3</c> for a <c>float</c>), as the game's own code has it, so
///     that a call writes the same code: <c>[InlineLocal("kind", 3)]</c> with <c>int kind = Turn.GetKind();</c>
///     in the body lowers to <c>I3 = Common.GetTurnKind();</c>. The call overwrites that register of the
///     worker that runs it.
/// </summary>
/// <param name="name">The local's name in the method's body.</param>
/// <param name="register">The register: 0-3 for an <c>int</c> local, 0-9 for a <c>float</c>.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class InlineLocalAttribute(string name, int register) : Attribute {
    /// <summary>The local's name in the method's body.</summary>
    public string Name { get; } = name;

    /// <summary>The register it lives in.</summary>
    public int Register { get; } = register;
}

/// <summary>
///     A parameter of an inline method whose argument is evaluated once, before the method's code runs, as
///     in an ordinary call, so it may be a call (<c>Turn.To(Common.AngleToPoint(…))</c>). The compiler
///     checks that the parameter is used once, in the method's first statement, where substituting the
///     argument evaluates it exactly then.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class EagerAttribute : Attribute;

/// <summary>
///     A parameter whose argument is a compile-time constant: a literal, a <c>const</c>, or arithmetic of them. The
///     compiler computes with the value when it compiles the call (<see cref="Ffx.Wait.Seconds"/> counts its frames
///     so); anything else is ATL3005.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class ConstantAttribute : Attribute;
