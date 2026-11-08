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
///     Runs this method at a point inside the vanilla entry method <paramref name="method"/>: before or after the
///     statement that holds the <see cref="Occurrence"/>-th call to <see cref="Before"/> or <see cref="After"/>, counted
///     in the order the calls stand in the decompiled source
///     (<c>[Inject(nameof(OnTurn), Before = nameof(Btl.UseCommand), Occurrence = 2)]</c>).
/// </summary>
/// <remarks>
///     The call is named as it is written there: <c>nameof(Btl.UseCommand)</c> or <c>"Btl.UseCommand"</c> for a native,
///     <c>"Motion.Play"</c> for a member, <c>nameof(Dialogue.Say)</c> for an inline method, <c>"W02.OpenAsync"</c> for a
///     request. The statement is the innermost one holding the call: for a call in an <c>if</c>'s condition, the whole
///     <c>if</c>. Jumps that come from inside the statement (a loop's back edge, an <c>if</c>'s end) stay inside it: code
///     injected before it runs once as the statement starts, code injected after it once as it ends; a <c>return</c> in
///     the statement leaves before the code after it. A <c>return;</c> in the injected method ends the entry.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class InjectAttribute(string method) : Attribute {
    public string Method { get; } = method;

    /// <summary>Runs before the statement with this call.</summary>
    public string? Before { get; set; }

    /// <summary>Runs after the statement with this call.</summary>
    public string? After { get; set; }

    /// <summary>Which call of that name in the method: 1 (the default) for the first.</summary>
    public int Occurrence { get; set; } = 1;
}

/// <summary>The data areas of a script block, for <see cref="Data.At{T}"/>.</summary>
public enum DataArea {
    /// <summary>The shared data (storage 4), from the variables' offset 0.</summary>
    Shared,
    /// <summary>The "other" area (block <c>+0x2c</c>, 0x40 bytes).</summary>
    Other,
    /// <summary>The event data (storage 6).</summary>
    Event,
    /// <summary>The area data (block <c>+0x28</c>).</summary>
    Area,
    /// <summary>The worker's private data (storage 3), in a worker's static constructor; events only.</summary>
    Private,
}

/// <summary>
///     The bytes of the script's data areas, for a mod's initial values where no variable covers them:
///     <c>Data.At&lt;ushort&gt;(DataArea.Shared, 0x64) = 5;</c> in a static constructor (docs/language.md, "Initial values
///     and data edits").
/// </summary>
public static class Data {
    /// <summary>The <typeparamref name="T"/> (<c>byte</c> … <c>int</c>, <c>float</c>) at <paramref name="offset"/> of <paramref name="area"/>.</summary>
    public static ref T At<T>(DataArea area, int offset) where T : unmanaged => throw Runtime.NotRunnable();
}

/// <summary>
///     Switches a named dead block on (<c>[assembly: EnableCut(nameof(Cut.ZanarkandCrowdFacingReaction))]</c>):
///     the linker turns the block's <c>jmp</c> into a no-op of the same length in the player's script, so the
///     block runs where it stands, as <c>if (true)</c> (docs/research/dead-code.md, "Switching a block on").
///     <paramref name="name"/> is a constant of <see cref="Ffx.Cut"/>; <c>atel compile-mod</c> finds the scripts
///     and signatures of its blocks in <c>data/cut-names.toml</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class EnableCutAttribute(string name) : Attribute {
    public string Name { get; } = name;
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
