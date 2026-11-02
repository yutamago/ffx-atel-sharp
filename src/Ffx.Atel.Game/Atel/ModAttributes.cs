// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Atel;

// What a mod writes on top of the decompiled vanilla source (docs/plan.md, "Mods";
// docs/formats/mod-package.md). A mod extends a vanilla worker with `partial class W01 { … }`:
// a method without an attribute is a new entry, these attributes patch a vanilla one.

/// <summary>
///     Runs this method before the vanilla entry method <paramref name="method"/>
///     (<c>[Prefix(nameof(Init))]</c>); then the vanilla method runs. A <c>return;</c> ends the entry,
///     so the vanilla method does not run.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class PrefixAttribute(string method) : Attribute {
    public string Method { get; } = method;
}

/// <summary>
///     Runs this method after the vanilla entry method <paramref name="method"/>, wherever it ends the
///     entry (<c>return;</c> in the vanilla code). It runs off its end into the entry's end.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class PostfixAttribute(string method) : Attribute {
    public string Method { get; } = method;
}

/// <summary>Runs this method instead of the vanilla entry method <paramref name="method"/>.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ReplaceAttribute(string method) : Attribute {
    public string Method { get; } = method;
}

/// <summary>
///     Numbers the linker gives: a worker's number and an entry's, for requests to workers and entries
///     a mod adds (<c>Request(1, Link.Worker&lt;Helper&gt;(), Link.Entry&lt;W01&gt;(nameof(W01.Boost)))</c>).
///     For vanilla workers and entries they are the vanilla numbers.
/// </summary>
public static class Link {
    /// <summary>The number of worker <typeparamref name="T"/>.</summary>
    public static int Worker<T>() where T : Atel.Worker => throw Runtime.NotRunnable();

    /// <summary>The entry number of method <paramref name="method"/> of worker <typeparamref name="T"/> (<c>nameof(W01.Boost)</c>).</summary>
    public static int Entry<T>(string method) where T : Atel.Worker => throw Runtime.NotRunnable();
}
