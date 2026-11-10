// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Atel;

// What a mod writes on top of the decompiled vanilla source (docs/plan.md, "Mods";
// docs/formats/mod-package.md). A mod extends a vanilla worker with `partial class W01 { … }`:
// a method without an attribute is a new entry, these attributes patch a vanilla one. A static
// constructor sets initial values (Data, and Ffx.MonsterStats / Ffx.MonsterRewards).

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
///     statement that holds the <see cref="Occurrence"/>-th place of <see cref="Before"/> or <see cref="After"/>, counted
///     in the order they stand in the decompiled source
///     (<c>[Inject(nameof(OnTurn), Before = nameof(Btl.UseCommand), Occurrence = 2)]</c>, <c>[Inject(nameof(Main), After = nameof(Self.Hp))]</c>).
/// </summary>
/// <remarks>
///     A call is named as it is written there: <c>nameof(Btl.UseCommand)</c> or <c>"Btl.UseCommand"</c> for a native,
///     <c>"Motion.Play"</c> for a member, <c>nameof(Dialogue.Say)</c> for an inline method, <c>"W02.OpenAsync"</c> for a
///     request. A member property is named in full (<c>nameof(Self.Hp)</c>, <c>nameof(Yaw)</c>,
///     <c>"Camera.Eye.Position"</c>, <c>"Actors.Hp"</c>): its places are its reads and assignments. A native or a member
///     named in full also counts whatever compiles to its native, written either way: <c>Self.Hp = 1;</c> is a place of
///     <c>Btl.SetActorProperty</c>, and <c>Btl.SetActorProperty(ActorProperty.Hp, 1);</c> one of <c>Self.Hp</c>, so a
///     re-export that writes more natives as members moves no anchor. The statement is the innermost one holding the
///     place: for one in an <c>if</c>'s condition, the whole <c>if</c>. Jumps that come from inside the statement (a
///     loop's back edge, an <c>if</c>'s end) stay inside it: code injected before it runs once as the statement starts,
///     code injected after it once as it ends; a <c>return</c> in the statement leaves before the code after it. A
///     <c>return;</c> in the injected method ends the entry.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class InjectAttribute(string method) : Attribute {
    public string Method { get; } = method;

    /// <summary>Runs before the statement with this call or member.</summary>
    public string? Before { get; set; }

    /// <summary>Runs after the statement with this call or member.</summary>
    public string? After { get; set; }

    /// <summary>Which place of that call or member in the method: 1 (the default) for the first.</summary>
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

/// <summary>
///     A field whose value is kept with the save game: <c>[Saved] public static ushort LarvasMet;</c> in a mod's plain static
///     class (state several scripts share) or in its part of a script class (docs/language.md, "Saved fields"). It is a
///     variable like any other in code. Its initializer is the value on a new game and in a save made without it.
/// </summary>
/// <remarks>
///     <para>
///         The field is a storage 0 variable in the save's free tail (<see cref="Ffx.Save.ModArea"/>). The loader gives each
///         enabled mod's fields their offsets there when the game starts, and keeps their values by key in its own file per
///         save, so neither the game's save file nor the order of the mods matters. The key is the declaring type and the
///         field (<c>LarvaCount.Progress.LarvasMet</c>), or <see cref="Key"/>; renaming the field changes the key unless
///         <see cref="Key"/> keeps the old one. A field is a <c>byte</c> … <c>int</c>, a <c>float</c>, or a 1-D or 2-D
///         array of one with its size; all of a mod's fields share 1,032 bytes with the other mods'.
///     </para>
///     <para>
///         <b>Another mod's field</b>: with <see cref="Mod"/>, the field names a field of that mod by its key, to read it:
///         <c>[Saved(Mod = "yuta.larvacount", Key = "LarvaCount.Progress.LarvasMet")] public static ushort LarvasMet;</c>.
///         The type and size are that mod's; there is no initializer, and code only reads it. The mod's <c>mod.toml</c>
///         lists the other mod in <c>requires</c>; the loader leaves the mod out when the other one is not linked or has no
///         such field.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class SavedAttribute : Attribute {
    /// <summary>The key the value is kept under, instead of <c>&lt;type&gt;.&lt;field&gt;</c>: a renamed field keeps its old one.</summary>
    public string? Key { get; set; }

    /// <summary>The id of the mod whose field this is (with <see cref="Key"/>); null for the mod's own.</summary>
    public string? Mod { get; set; }
}
