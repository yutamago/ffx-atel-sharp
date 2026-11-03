// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace Ffx.Atel.Bytecode.Text;

/// <summary>
///     A battle kernel table (<c>new_&lt;lang&gt;pc/battle/kernel/&lt;table&gt;.bin</c>): records by id, each starting with
///     references into a string area. Read only for the strings, the names <c>atel names export</c> writes.
/// </summary>
/// <remarks>
///     Ported from ffx-rust's <c>docs/formats/kernel.md</c> (<c>MsGetExcelData</c>, <c>0x007ab870</c>):
///     <code>
///     0x00        u16 segment count S, then 6 zero bytes
///     0x08        S x 12 bytes: u16 first id, u16 last id (inclusive), u16 record size, u16 data length, u32 data offset
///     ...         the records of each segment
///     ...         the string area, to the end of the file
///     </code>
///     A segment's strings start at its data offset + data length (one segment in every shipped table).
///     A record's string <i>i</i> is the <c>u16</c> at <c>4 i</c> of the record, an offset into the string area
///     (the second <c>u16</c> is not read by the game's lookups). Strings are pooled and may share suffixes.
/// </remarks>
public sealed class KernelTable {
    private readonly byte[]                                                       _bytes;
    private readonly List<(int First, int Last, int Size, int Data, int Strings)> _segments = [];

    private KernelTable(byte[] bytes) => _bytes = bytes;

    /// <summary>The archive path of a table in a language: <c>new_uspc/battle/kernel/command.bin</c>.</summary>
    public static string PathOf(GameLanguage language, string table) =>
        $"ffx_ps2/ffx/master/new_{language.GameCode()}pc/battle/kernel/{table}.bin";

    /// <summary>The record ids the table has, segment by segment.</summary>
    public IEnumerable<int> Ids => _segments.SelectMany(s => Enumerable.Range(s.First, s.Last - s.First + 1));

    /// <summary>Parses a table.</summary>
    /// <exception cref="InvalidDataException">A segment outside the file.</exception>
    public static KernelTable Parse(byte[] bytes) {
        if (bytes.Length < 8) throw new InvalidDataException("kernel table shorter than its header");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (8 + 12 * count > bytes.Length) throw new InvalidDataException("kernel table segments outside the file");
        KernelTable table = new(bytes);
        for (int i = 0; i < count; i++) {
            ReadOnlySpan<byte> d = bytes.AsSpan(8 + 12 * i, 12);
            int first = BinaryPrimitives.ReadUInt16LittleEndian(d), last = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]), length = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]);
            int data = BinaryPrimitives.ReadInt32LittleEndian(d[8..]);
            if (last < first || size == 0 || data < 0 || (long)data + length > bytes.Length || (long)(last - first + 1) * size > length) {
                throw new InvalidDataException($"kernel table segment {i} outside the file");
            }
            table._segments.Add((first, last, size, data, data + length));
        }
        return table;
    }

    /// <summary>
    ///     String <paramref name="index" /> of record <paramref name="id" /> (the name is string 0), its bytes up to
    ///     the <c>00</c>; null for an id the table does not have or an offset outside the string area.
    /// </summary>
    public byte[]? String(int id, int index = 0) {
        foreach ((int first, int last, int size, int data, int strings) in _segments) {
            if (id < first || id > last) continue;
            if (4 * index + 2 > size) return null;
            int at = data + size * (id - first) + 4 * index;
            int offset = strings + BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(at));
            if (offset >= _bytes.Length) return null;
            int end = Array.IndexOf(_bytes, (byte)0, offset);
            return _bytes[offset..(end < 0 ? _bytes.Length : end)];
        }
        return null;
    }
}
