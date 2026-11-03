// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Text;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Bytecode.Tests;

/// <summary>The kernel table reader, on a made-up table; and every shipped table's names (with <c>FFX_GAME_DIR</c>).</summary>
public sealed class KernelTableTests(ITestOutputHelper output) {
    /// <summary>One segment, ids 5-7, 8-byte records of two string references; the strings share a suffix.</summary>
    private static byte[] MadeUp() {
        byte[] strings = [0x50, 0x51, 0x00, 0x52, 0x00];   // "AB", "C" (and "B" at 1)
        const int records = 3, size = 8, data = 8 + 12;
        byte[] file = new byte[data + records * size + strings.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(file, 1);
        Span<byte> seg = file.AsSpan(8);
        BinaryPrimitives.WriteUInt16LittleEndian(seg, 5);
        BinaryPrimitives.WriteUInt16LittleEndian(seg[2..], 7);
        BinaryPrimitives.WriteUInt16LittleEndian(seg[4..], size);
        BinaryPrimitives.WriteUInt16LittleEndian(seg[6..], records * size);
        BinaryPrimitives.WriteInt32LittleEndian(seg[8..], data);
        ushort[][] refs = [[0, 3], [1, 2], [2, 0]];
        for (int r = 0; r < records; r++) {
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(data + size * r), refs[r][0]);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(data + size * r + 4), refs[r][1]);
        }
        strings.CopyTo(file.AsSpan(data + records * size));
        return file;
    }

    [Fact]
    public void ReadsStringsByIdAndIndex() {
        KernelTable table = KernelTable.Parse(MadeUp());
        Assert.Equal([5, 6, 7], table.Ids);
        Assert.Equal([0x50, 0x51], table.String(5)!);
        Assert.Equal([0x52], table.String(5, 1)!);
        Assert.Equal([0x51], table.String(6)!);           // a pooled suffix
        Assert.Equal([], table.String(7)!);               // an empty string
        Assert.Null(table.String(4));
        Assert.Null(table.String(8));
        Assert.Null(table.String(5, 2));                   // past the record
        Assert.Equal("ffx_ps2/ffx/master/new_depc/battle/kernel/item.bin", KernelTable.PathOf(GameLanguage.German, "item"));
    }

    [Fact]
    public void RejectsSegmentsOutsideTheFile() {
        byte[] file = MadeUp();
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8 + 8), file.Length);
        Assert.Throws<InvalidDataException>(() => KernelTable.Parse(file));
        Assert.Throws<InvalidDataException>(() => KernelTable.Parse([1, 0, 0, 0]));
    }

    /// <summary>
    ///     The tables <c>atel names export</c> reads parse in every language, and every record's name decodes
    ///     without undecodable bytes. Nothing of the text is printed.
    /// </summary>
    [Fact]
    public void EveryNameTableParses() {
        using VbfArchive archive = CorpusTests.OpenOrSkip();
        ScriptMessages messages = new(archive);
        string[] tables = ["monster1", "monster2", "monster3", "item", "command", "monmagic1", "monmagic2", "a_ability", "important"];
        List<string> failures = [];
        int names = 0;
        foreach (GameLanguage language in GameLanguages.All) {
            Charset charset = messages.Charset(language);
            foreach (string t in tables) {
                string path = KernelTable.PathOf(language, t);
                if (archive.Find(path) is null) {
                    failures.Add($"{path}: missing");
                    continue;
                }
                KernelTable table = KernelTable.Parse(archive.Read(path));
                foreach (int id in table.Ids) {
                    if (table.String(id) is not byte[] bytes) {
                        failures.Add($"{path}: record {id} has no name");
                        continue;
                    }
                    if (charset.Decode(bytes).Contains("{raw:", StringComparison.Ordinal)) failures.Add($"{path}: record {id} does not decode");
                    names++;
                }
            }
        }
        output.WriteLine($"{names} names in {tables.Length} tables x {GameLanguages.All.Count} languages");
        foreach (string f in failures.Take(20)) output.WriteLine(f);
        Assert.Empty(failures.Take(20));
    }
}
