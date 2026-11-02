// SPDX-License-Identifier: MIT

using System.Text;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Text;

namespace Ffx.Atel.Linker.Tests;

/// <summary>New messages (<c>Text.New</c>): numbered by the linker, added to every language's table (made-up files).</summary>
public sealed class MessageTests {
    private const string Script = "ffx_ps2/ffx/master/jppc/event/obj/te/test0000/test0000.ebp";

    /// <summary>A stand-in western charset (the shape of the real one): digits, space, punctuation, letters.</summary>
    private static byte[] WesternCharset() {
        StringBuilder table = new("0123456789 !-.?{}ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-");
        while (table.Length < 0x200) table.Append('·');
        return Encoding.UTF8.GetBytes(table.ToString());
    }

    private static readonly Charset Western = new(WesternCharset(), GameLanguage.English);

    private static byte[] Table(params string[] texts) => new MessageTable([.. texts.Select(t => new Message(Western.Encode(t), 0))]).Write();

    /// <summary>The game's files: the script, English (3 messages) and French (5) tables, the western charset; no other language.</summary>
    private static Dictionary<string, byte[]> Files(byte[] script) => new(StringComparer.Ordinal) {
        [Script] = script,
        [GameLanguage.English.EventMessagesPath("test0000")] = Table("Hello", "Bye", "Yes"),
        [GameLanguage.French.EventMessagesPath("test0000")] = Table("Bonjour", "Salut", "Oui", "Non", "Fin"),
        [GameLanguage.English.CharsetPath()] = WesternCharset(),
    };

    /// <summary>A mod whose prefix on worker 1's Init pushes its new messages into v0 (one per text).</summary>
    private static AtelMod Mod(byte[] vanilla, string id, string language, params string[] texts) {
        ScriptPackage p = LinkerTests.Package(vanilla, new EntryPatch {
            Kind = PatchKind.Prefix, Worker = 1, Name = "Say", Target = "Init",
            Code = LinkerTests.Frag(string.Join('\n', texts.Select((_, i) => $"pushimm @newmessage:{i}\npopv @var:0"))),
        });
        p.File = Script;
        p.Script = "test0000";
        p.Messages = [.. texts.Select(t => new PackageMessage(null, t))];
        AtelMod mod = new() { Id = id, Manifest = new ModManifest { SourceLanguage = language } };
        mod.Packages[Script] = p;
        return mod;
    }

    private static List<ushort> Pushes(byte[] file) {
        List<ushort> pushes = [];
        ScriptBlock b = ScriptFile.Parse(file).Script;
        foreach (Instr i in b.Decode(b.Workers[1])) {
            if (i.Code == Opcodes.PushImm && i.Operand is ushort v) pushes.Add(v);
        }
        return pushes;
    }

    [Fact]
    public void NewMessagesGoAfterTheLongestTableInEveryLanguage() {
        byte[] vanilla = LinkerTests.VanillaFile();
        Dictionary<string, byte[]> files = Files(vanilla);
        List<LinkedFile> linked = ModSet.Link([Mod(vanilla, "a", "en", "Nice throw!", "Again"), Mod(vanilla, "b", "fr", "Bien")], p => files.GetValueOrDefault(p));

        // The script: its prefixes push 5, 6 (mod a) and 7 (mod b): after French's 5 messages.
        LinkedFile script = linked.Single(f => f.Path == Script);
        Assert.Equal(["a", "b"], script.Result!.Applied);
        Assert.Equal([(5, "Nice throw!"), (6, "Again"), (7, "Bien")], script.Result.NewMessages.Select(m => (m.Index, m.Text)));
        Assert.Contains((ushort)5, Pushes(script.Result.File));
        Assert.Contains((ushort)7, Pushes(script.Result.File));

        // The tables: English grows from 3 to 8 (3 and 4 empty), French from 5 to 8; same index everywhere.
        MessageTable en = MessageTable.Parse(linked.Single(f => f.Path == GameLanguage.English.EventMessagesPath("test0000")).Result!.File);
        MessageTable fr = MessageTable.Parse(linked.Single(f => f.Path == GameLanguage.French.EventMessagesPath("test0000")).Result!.File);
        Assert.Equal(["Hello", "Bye", "Yes", "", "", "Nice throw!", "Again", "Bien"], en.Messages.Select(m => Western.Decode(m.Text)));
        Assert.Equal(["Bonjour", "Salut", "Oui", "Non", "Fin", "Nice throw!", "Again", "Bien"], fr.Messages.Select(m => Western.Decode(m.Text)));

        // Copies in another language than the mod's are reported as untranslated; the missing tables too.
        List<LinkMessage> report = script.Result.Messages;
        Assert.Contains(report, m => m.Mod == "a" && m.Text.Contains("untranslated in fr", StringComparison.Ordinal) && m.Text.Contains("5, 6", StringComparison.Ordinal));
        Assert.Contains(report, m => m.Mod == "b" && m.Text.Contains("untranslated in en", StringComparison.Ordinal));
        Assert.Contains(report, m => m.Severity == LinkSeverity.Warning && m.Text.Contains("no message table in ja", StringComparison.Ordinal));
    }

    [Fact]
    public void ACharacterTheCharsetLacksBecomesAQuestionMark() {
        byte[] vanilla = LinkerTests.VanillaFile();
        Dictionary<string, byte[]> files = Files(vanilla);
        List<LinkedFile> linked = ModSet.Link([Mod(vanilla, "a", "en", "Café ok")], p => files.GetValueOrDefault(p));
        MessageTable en = MessageTable.Parse(linked.Single(f => f.Path == GameLanguage.English.EventMessagesPath("test0000")).Result!.File);
        Assert.Equal(Western.Encode("Caf? ok"), en.Messages[5].Text);
        Assert.Contains(linked[0].Result!.Messages, m => m.Severity == LinkSeverity.Warning && m.Text.Contains("message 5", StringComparison.Ordinal));
    }

    [Fact]
    public void AScriptWithoutATableTakesNoNewMessages() {
        // A monster's messages are the battle's: the package is left out, with an error.
        byte[] vanilla = LinkerTests.VanillaFile();
        AtelMod mod = Mod(vanilla, "a", "en", "Hi");
        ScriptPackage p = mod.Packages[Script];
        p.File = "ffx_ps2/ffx/master/jppc/battle/mon/_m998/m998.bin";
        mod.Packages.Clear();
        mod.Packages[p.File] = p;
        LinkedFile f = Assert.Single(ModSet.Link([mod], path => path == p.File ? vanilla : null));
        Assert.False(f.Result!.Changed);
        Assert.Contains(f.Result.Messages, m => m.Severity == LinkSeverity.Error && m.Text.Contains("no message table", StringComparison.Ordinal));
    }
}
