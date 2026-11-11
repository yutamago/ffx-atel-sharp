// SPDX-License-Identifier: MIT

namespace Ffx;

/// <summary>
///     A message of the script, by its number: <c>Text[12]</c>. <see cref="Dialogue.Say"/> and the other
///     dialogue methods take one.
/// </summary>
/// <remarks>
///     An <c>int</c> converts to it implicitly (a computed number), so <c>Text[n]</c> is only for a known message
///     (docs/plan.md, "Text").
/// </remarks>
public readonly struct TextId {
    /// <summary>A message by a computed index: no code.</summary>
    public static implicit operator TextId(int index) => throw Atel.Runtime.NotRunnable();
}

/// <summary>
///     The script's messages, the lines its text boxes show: <c>Text[12]</c> is message 12. In a mod,
///     <see cref="New"/> adds a message of your own.
/// </summary>
/// <remarks><c>Text[12]</c> compiles to the number itself; it is no code of its own.</remarks>
public sealed class TextTable {
    private TextTable() {
    }

    /// <summary>Message <paramref name="index"/> of the script's table.</summary>
    public TextId this[int index] => throw Atel.Runtime.NotRunnable();

    /// <summary>
    ///     A new message, in a mod: <c>Dialogue.Say(Text.New("Nice throw!"));</c>. The text is a string literal.
    /// </summary>
    /// <remarks>
    ///     Its number is given when the loader links the mod (after the last message of the script), the same in
    ///     every language: the mod's source language (<c>mod.toml</c>'s <c>source_language</c>) gets the text, the
    ///     other languages a copy of it, reported as untranslated. The text is written as <c>atel text export</c>
    ///     writes messages: control codes in braces, <c>{{</c> for a brace.
    /// </remarks>
    public TextId New(string text) => throw Atel.Runtime.NotRunnable();
}
