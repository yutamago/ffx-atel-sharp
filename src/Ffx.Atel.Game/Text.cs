// SPDX-License-Identifier: MIT

namespace Ffx;

/// <summary>
///     A message of the script's message table, by its index: <c>Msg.SetText(window, Text[12])</c>. Message
///     natives take one; an <c>int</c> converts to it implicitly (a computed index), so <c>Text[n]</c> is
///     only for a known message (docs/plan.md, "Text").
/// </summary>
public readonly struct TextId {
    /// <summary>A message by a computed index: no code.</summary>
    public static implicit operator TextId(int index) => throw Atel.Runtime.NotRunnable();
}

/// <summary>The script's message table: <c>Text[12]</c> is message 12 (the constant itself, no code).</summary>
public sealed class TextTable {
    private TextTable() {
    }

    /// <summary>Message <paramref name="index"/> of the script's table.</summary>
    public TextId this[int index] => throw Atel.Runtime.NotRunnable();

    /// <summary>
    ///     A new message, in a mod: <c>Msg.SetText(1, Text.New("Nice throw!"))</c>. Its index is given when the
    ///     loader links the mod (after the last message of the script's tables), the same in every language:
    ///     the mod's source language (<c>mod.toml</c>'s <c>source_language</c>) gets the text, the other
    ///     languages a copy of it, reported as untranslated. The text is a literal, written as <c>atel text
    ///     export</c> writes messages (control codes in braces, <c>{{</c> for a brace).
    /// </summary>
    public TextId New(string text) => throw Atel.Runtime.NotRunnable();
}
