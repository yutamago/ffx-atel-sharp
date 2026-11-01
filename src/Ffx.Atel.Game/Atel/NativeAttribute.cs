// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Atel;

/// <summary>Marks a method as a native function of the game: the compiler emits <c>func id</c> for a call.</summary>
/// <param name="id">The native's ID: group in bits 12–15, index below.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = false)]
public sealed class NativeAttribute(int id) : Attribute {
    public int Id { get; } = id;

    /// <summary>The native has a poll handler: the call may span frames until it reports done.</summary>
    public bool Polls { get; init; }
}

/// <summary>Members of the reference assembly only describe the game; they never run on .NET.</summary>
public static class Runtime {
    public static Exception NotRunnable() => new InvalidOperationException("Atel code runs in the game's script interpreter, not on .NET.");
}
