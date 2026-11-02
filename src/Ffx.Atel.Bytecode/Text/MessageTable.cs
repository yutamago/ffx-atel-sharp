// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace Ffx.Atel.Bytecode.Text;

/// <summary>One message of a <see cref="MessageTable"/>.</summary>
/// <param name="Text">The encoded text without its <c>00</c>; null (offset 0) for the same entry of the event file's own table.</param>
/// <param name="Flags">The high byte is a choice's option count (<c>Msg_Flags</c>, <c>0x0086bf10</c>).</param>
/// <param name="Alternate">The alternate text and flags (<c>Msg_TextAlt</c>), when they differ from the text's.</param>
public sealed record Message(byte[]? Text, ushort Flags, (byte[]? Text, ushort Flags)? Alternate = null);

/// <summary>
///     A message table: an event's dialogue or a battle's texts, one file per language
///     (<see cref="GameLanguages.EventMessagesPath"/>), loaded by <c>TKMallocAndReadEventMessage</c>
///     (<c>0x0088cac0</c>) to the script's ctrl work <c>+0x34</c>, where <c>Text[n]</c> reads.
/// </summary>
/// <remarks>
///     Ported from ffx-rust's <c>crates/ffx-text/src/messages.rs</c>.
///     <code>
///     n x 8 bytes   entries: u16 text offset, u16 flags, u16 alternate text offset, u16 alternate flags
///     ...           the strings, each ending in 00
///     </code>
///     Offsets are from the start of the file; the first string follows the entries, so the first offset
///     gives the count. An offset of 0 falls back to the same entry of the event file's own table (section 1
///     of the <c>.ebp</c>, the original Japanese, same layout). In every shipped table the strings follow each
///     other in entry order and each alternate equals its text.
/// </remarks>
public sealed class MessageTable {
    private const int EntryLength = 8;

    public IReadOnlyList<Message> Messages { get; }

    public MessageTable(IReadOnlyList<Message> messages) => Messages = messages;

    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);

    /// <summary>Parses a table.</summary>
    /// <exception cref="InvalidDataException">Offsets outside the file, or strings not in entry order.</exception>
    public static MessageTable Parse(ReadOnlySpan<byte> bytes) {
        if (bytes.Length < EntryLength) throw new InvalidDataException("message table shorter than one entry");
        // The entries end where the first string starts; a table of fallbacks only is all entries.
        int first = bytes.Length;
        for (int at = 0; at + EntryLength <= first; at += EntryLength) {
            foreach (int o in (ReadOnlySpan<int>)[at, at + 4]) {
                int v = U16(bytes, o);
                if (v != 0) first = Math.Min(first, v);
            }
        }
        if (first % EntryLength != 0) throw new InvalidDataException("message table entries do not end on an entry");
        int count = first / EntryLength;
        List<int> starts = new(count);
        for (int i = 0; i < count; i++) {
            int text = U16(bytes, i * EntryLength), alt = U16(bytes, i * EntryLength + 4);
            if (text != 0) starts.Add(text);
            if (alt != text && alt != 0) starts.Add(alt);
        }
        for (int i = 1; i < starts.Count; i++) {
            if (starts[i - 1] >= starts[i]) throw new InvalidDataException("message table strings out of order");
        }
        byte[] all = bytes.ToArray();
        byte[]? String(int at) {
            if (at == 0) return null;
            int end = starts.FirstOrDefault(s => s > at, all.Length);
            if (end > all.Length || at >= end || all[end - 1] != 0) throw new InvalidDataException("message table string without its 00");
            return all[at..(end - 1)];
        }
        List<Message> messages = new(count);
        for (int i = 0; i < count; i++) {
            int o = i * EntryLength;
            int text = U16(bytes, o), alt = U16(bytes, o + 4);
            ushort flags = U16(bytes, o + 2), altFlags = U16(bytes, o + 6);
            messages.Add(new Message(String(text), flags, alt != text || altFlags != flags ? (String(alt), altFlags) : null));
        }
        return new MessageTable(messages);
    }

    /// <summary>Writes the table: strings in entry order, an alternate that differs after its text.</summary>
    /// <exception cref="InvalidDataException">The strings outgrow the 16-bit offsets.</exception>
    public byte[] Write() {
        int baseOffset = Messages.Count * EntryLength;
        List<byte> head = new(baseOffset), strings = [];
        ushort Put(byte[]? text) {
            if (text is null) return 0;
            int at = baseOffset + strings.Count;
            if (at > ushort.MaxValue) throw new InvalidDataException("more than 64 KiB of text");
            strings.AddRange(text);
            strings.Add(0);
            return (ushort)at;
        }
        Span<byte> word = stackalloc byte[2];
        foreach (Message m in Messages) {
            ushort text = Put(m.Text);
            (ushort alt, ushort altFlags) = m.Alternate switch {
                null => (text, m.Flags),
                var (t, f) when Same(t, m.Text) => (text, f),
                var (t, f) => (Put(t), f),
            };
            foreach (ushort v in (ReadOnlySpan<ushort>)[text, m.Flags, alt, altFlags]) {
                BinaryPrimitives.WriteUInt16LittleEndian(word, v);
                head.AddRange(word);
            }
        }
        head.AddRange(strings);
        return [.. head];
    }

    private static bool Same(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}
