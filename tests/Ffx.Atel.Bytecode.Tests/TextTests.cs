// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;
using Ffx.Atel.Bytecode.Text;

namespace Ffx.Atel.Bytecode.Tests;

/// <summary>The text codec and message tables, on made-up tables and bytes (no game text).</summary>
public sealed class TextTests {
    /// <summary>A stand-in table shaped like the real one: digits, space, punctuation, letters, a duplicate, braces, filler, a star.</summary>
    private static Charset Western() {
        StringBuilder table = new("0123456789 !-.{}ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-");
        while (table.Length < 0x200) table.Append('·');
        table.Append('★');
        return new Charset(Encoding.UTF8.GetBytes(table.ToString()), GameLanguage.English);
    }

    [Theory]
    [InlineData(new byte[] { 0x30, 0x3a, 0x3b })]
    [InlineData(new byte[] { 0x03, 0x01, 0x0a, 0x41, 0x05 })]
    [InlineData(new byte[] { 0x2d, 0x30 })]       // a two-byte character
    [InlineData(new byte[] { 0x5e })]             // the duplicate '-' at its second index
    [InlineData(new byte[] { 0x2c, 0x30 })]       // a filler character with no unique form
    [InlineData(new byte[] { 0x2c })]             // a lead byte without a trail
    [InlineData(new byte[] { 0x3e, 0x3f })]       // braces
    [InlineData(new byte[] { 0x04, 0x31 })]       // the second bank, past the table
    [InlineData(new byte[] { 0x13, 0x30, 0x09 })] // a control code missing its argument
    public void LosslessFormRoundTrips(byte[] bytes) {
        Charset cs = Western();
        string text = cs.Decode(bytes);
        Assert.Equal(bytes, cs.Encode(text));
    }

    [Fact]
    public void DecodesReadably() {
        Charset cs = Western();
        Assert.Equal("0 \n{0a:41}{{", cs.Decode([0x30, 0x3a, 0x03, 0x0a, 0x41, 0x3e, 0x00, 0x31]));
        Assert.Equal([.. cs.Encode("H"), .. cs.Encode("i!")], cs.Encode("Hi!"));
        Assert.Throws<TextEncodeException>(() => cs.Encode("é"));
        Assert.Throws<TextEncodeException>(() => cs.Encode("{zz}"));
        Assert.Equal("★", cs.Decode([0x2d, 0x90]));      // index 0x200, a two-byte code
        Assert.Equal("{c:100}", cs.Decode([0x2c, 0x60])); // a filler that is not its first occurrence
        Assert.Equal("·", cs.Display([0x2c, 0x60]));      // shown as it is
    }

    [Fact]
    public void DisplayNamesControlCodes() {
        Charset cs = Western();
        byte[] hi = cs.Encode("Hi");
        byte[] bytes = [
            0x13, 0x30, 0x3b, 0x3a, .. hi, 0x03,  // {name:Tidus}! Hi\n
            0x09, 0x34, 0x0a, 0x41, 0x0b, 0x05,   // {wait:4}{color:41}{button:5}
            0x10, 0x31, 0x12, 0x32, 0x23, 0x33,   // {choice:1}{var:2}{keyitem:3}
            0x19, 0x30, 0x07, 0xe3, 0x2a, 0x31,   // {macro:6:0}{space:3}{2a:31}
            0x01, 0x3e,                           // {break}{
        ];
        Assert.Equal("{name:Tidus}! Hi\n{wait:4}{color:41}{button:5}{choice:1}{var:2}{keyitem:3}{macro:6:0}{space:3}{2a:31}{break}{",
            cs.Display(bytes));
    }

    [Fact]
    public void JapaneseAndKoreanSubstitutions() {
        StringBuilder table = new("0123456789あ");
        while (table.Length < 0x40) table.Append('い');
        Charset jp = new(Encoding.UTF8.GetBytes(table.ToString()), GameLanguage.Japanese);
        Assert.Equal("あ", jp.Display([0x5c]));
        Charset kr = new(Encoding.UTF8.GetBytes(table.ToString()), GameLanguage.Korean);
        Assert.Equal(" ", kr.Display([0x3a]));
    }

    private static byte[] Table(params (byte[]? Text, ushort Flags)[] messages) =>
        new MessageTable([.. messages.Select(m => new Message(m.Text, m.Flags))]).Write();

    [Fact]
    public void MessageTablesParseAndWrite() {
        Charset cs = Western();
        byte[] hi = cs.Encode("HI"), yes = cs.Encode("YES\nNO");
        byte[] bytes = Table((hi, 0), (yes, 0x200));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(bytes));
        MessageTable table = MessageTable.Parse(bytes);
        Assert.Equal(2, table.Messages.Count);
        Assert.Equal(0x200, table.Messages[1].Flags);
        Assert.Equal(hi, table.Messages[0].Text);
        Assert.Equal(bytes, table.Write());

        // Offsets of 0: the event file's own table stands in.
        MessageTable fallback = MessageTable.Parse(new byte[16]);
        Assert.Equal(2, fallback.Messages.Count);
        Assert.Null(fallback.Messages[0].Text);

        // An alternate that differs follows its text.
        MessageTable alt = new([new Message(hi, 0, (yes, 1))]);
        Assert.Equal(yes, MessageTable.Parse(alt.Write()).Messages[0].Alternate!.Value.Text);
        Assert.Throws<InvalidDataException>(() => MessageTable.Parse([8, 0, 0, 0, 8, 0, 0, 0, 0x31]));
    }

    [Fact]
    public void EventFileSectionOneIsTheOriginalTable() {
        Charset cs = Western();
        byte[] table = Table((cs.Encode("ORIGINAL"), 0));
        // "EV01", section 0 at 0x40 (a stand-in script), section 1 at 0x80, the end mark.
        byte[] ebp = new byte[0x80 + table.Length];
        "EV01"u8.CopyTo(ebp);
        BinaryPrimitives.WriteUInt32LittleEndian(ebp.AsSpan(4), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(ebp.AsSpan(8), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(ebp.AsSpan(12), 0xffff_ffff);
        table.CopyTo(ebp, 0x80);
        MessageTable original = ScriptMessages.OriginalTable(ebp)!;
        Assert.Equal("ORIGINAL", cs.Decode(original.Messages[0].Text));
    }

    [Fact]
    public void LanguagesAndPaths() {
        Assert.Equal(GameLanguage.English, GameLanguages.Parse("en"));
        Assert.Equal(GameLanguage.English, GameLanguages.Parse("US"));
        Assert.Equal(GameLanguage.Japanese, GameLanguages.Parse("ja"));
        Assert.Equal(GameLanguage.Spanish, GameLanguages.Parse("sp"));
        Assert.Null(GameLanguages.Parse("xx"));
        Assert.Equal("ffx_ps2/ffx/master/jppc/ffx_encoding/ffxsjistbl_us.bin", GameLanguage.French.CharsetPath());
        Assert.Equal("ffx_ps2/ffx/master/new_depc/event/obj_ps3/zn/znkd0600/znkd0600.bin", GameLanguage.German.EventMessagesPath("znkd0600"));
        Assert.Equal("ffx_ps2/ffx/master/new_uspc/battle/btl/cdsp00_00/cdsp00_00.bin", GameLanguage.English.BattleMessagesPath("cdsp00_00"));
    }

    [Fact]
    public void CommentsFollowTextUses() {
        string?[] messages = ["Hello \"you\"\nthere", null, "Two"];
        string source = "a\n        Common.MsgSetText(0, Text[0]);\n        x = Text[1] + Text[2] + Text[0];\n        y = Text[9];\n        Text[2]; Text[2];";
        Assert.Equal(
            "a\n        Common.MsgSetText(0, Text[0]); // \"Hello \\\"you\\\"\\nthere\"\n        x = Text[1] + Text[2] + Text[0]; // 2: \"Two\"; 0: \"Hello \\\"you\\\"\\nthere\"\n        y = Text[9];\n        Text[2]; Text[2]; // \"Two\"",
            TextComments.Comment(source, messages));
    }
}
