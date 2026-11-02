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
}
