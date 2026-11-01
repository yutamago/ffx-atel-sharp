// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace Ffx.Atel.Bytecode.Script;

/// <summary>Which kind of file holds a script.</summary>
public enum ScriptFileKind {
    /// <summary>
    ///     A section count and table: monsters (<c>battle/mon/_mNNN/mNNN.bin</c>, 8 sections) and the
    ///     battle scripts (<c>battle/btl/*/*.bin</c>, 5, 7 or 8).
    /// </summary>
    Monster,
    /// <summary><c>event/**/*.ebp</c>.</summary>
    Event,
}

/// <summary>A file that holds a script block, with its other sections.</summary>
/// <remarks>
///     Ported from ffx-rust's <c>crates/ffx-script/src/container.rs</c>.
///     <list type="bullet">
///         <item>Monster and battle files: a <c>u32</c> section count (8 for monsters; 5, 7 or 8 in
///         <c>battle/btl</c>), then as many <c>u32</c> section offsets; the script is section 0. Battle
///         files' offsets may point past the end of the file.</item>
///         <item>Event files: <c>"EV01"</c>, then section offsets from <c>0x04</c> up to a <c>0xffffffff</c>
///         terminator, 0 for absent sections; the script is section 0, at <c>0x40</c>.</item>
///     </list>
///     In both, sections are in ascending order, each runs to the next, and offsets are multiples
///     of <c>0x40</c>.
/// </remarks>
public sealed class ScriptFile {
    /// <summary>Sections start on this boundary; growing one shifts later ones by a multiple of it.</summary>
    public const int Align = 0x40;

    private readonly byte[] _bytes;
    private readonly int[]  _offsetWords;

    public ScriptFileKind Kind { get; }

    /// <summary>Offset of the script section in the file.</summary>
    public int SectionStart { get; }

    /// <summary>Room the script section has, up to the next section.</summary>
    public int SectionLength { get; }

    public ScriptBlock Script { get; }

    public ReadOnlySpan<byte> Bytes => _bytes;

    private ScriptFile(ScriptFileKind kind, byte[] bytes, int[] offsetWords, int start, int length, ScriptBlock script) {
        Kind          = kind;
        _bytes        = bytes;
        _offsetWords  = offsetWords;
        SectionStart  = start;
        SectionLength = length;
        Script        = script;
    }

    private static uint? U32(byte[] b, int o) => o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) : null;

    /// <summary>Whether the bytes look like a file of either kind.</summary>
    public static bool IsScriptFile(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith("EV01"u8) || (bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) is >= 1 and <= MaxSections);

    private const int MaxSections = 16;

    /// <summary>Opens a monster or event file.</summary>
    /// <exception cref="ScriptFormatException">Neither kind of file, or a malformed script.</exception>
    public static ScriptFile Parse(byte[] bytes) {
        ScriptFileKind kind;
        int[] words;
        if (bytes.AsSpan().StartsWith("EV01"u8)) {
            kind = ScriptFileKind.Event;
            words = Enumerable.Range(1, 15).Select(i => i * 4).TakeWhile(o => U32(bytes, o) is uint v && v != 0xffff_ffff).ToArray();
        }
        else if (U32(bytes, 0) is uint count and >= 1 and <= MaxSections && bytes.Length >= 4 + 4 * (int)count) {
            kind = ScriptFileKind.Monster;
            words = Enumerable.Range(1, (int)count).Select(i => i * 4).ToArray();
        }
        else {
            throw new ScriptFormatException("neither a monster nor an event file");
        }
        int[] offsets = words.Select(o => U32(bytes, o)).Where(v => v is > 0).Select(v => checked((int)v!.Value)).ToArray();
        if (offsets.Length == 0) throw new ScriptFormatException("no script section");
        int start = offsets[0];
        int end = Math.Min(offsets.Where(o => o > start).DefaultIfEmpty(bytes.Length).First(), bytes.Length);
        if (start >= bytes.Length) throw new ScriptFormatException("script section past the end");
        for (int i = 1; i < offsets.Length; i++) {
            if (offsets[i - 1] > offsets[i]) throw new ScriptFormatException("sections out of order");
        }
        ScriptBlock script = ScriptBlock.Parse(bytes[start..end]);
        return new ScriptFile(kind, bytes, words, start, end - start, script);
    }

    /// <summary>The file's bytes (a copy).</summary>
    public byte[] ToArray() => (byte[])_bytes.Clone();

    /// <summary>
    ///     Puts a rebuilt script back. A script that grew past its section shifts every later
    ///     section by a multiple of <c>0x40</c>; one that shrank keeps the original padding.
    /// </summary>
    public ScriptFile ReplaceScript(ScriptBlock script) {
        int start = SectionStart, room = SectionLength;
        ReadOnlySpan<byte> replacement = script.Bytes;
        int grow = Math.Max(replacement.Length - room, 0);
        grow = (grow + Align - 1) / Align * Align;
        byte[] bytes = new byte[_bytes.Length + grow];
        _bytes.AsSpan(0, start).CopyTo(bytes);
        replacement.CopyTo(bytes.AsSpan(start));
        // Keep the original padding up to the next section.
        if (replacement.Length < room) {
            _bytes.AsSpan(start + replacement.Length, room - replacement.Length).CopyTo(bytes.AsSpan(start + replacement.Length));
        }
        _bytes.AsSpan(start + room).CopyTo(bytes.AsSpan(start + room + grow));
        if (grow > 0) {
            foreach (int w in _offsetWords) {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(w));
                if (v > start && v != 0xffff_ffff) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(w), checked(v + (uint)grow));
            }
        }
        return Parse(bytes);
    }
}
