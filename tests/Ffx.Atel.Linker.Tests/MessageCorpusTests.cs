// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Text;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Linker.Tests;

/// <summary>A new message (<c>Text.New</c>) linked onto an event of the player's game. Needs <c>FFX_GAME_DIR</c>; skipped without it.</summary>
public sealed class MessageCorpusTests(ITestOutputHelper output) {
    private const string Event = "ffx_ps2/ffx/master/jppc/event/obj/ka/kami0000/kami0000.ebp";
    private const string Text = "Atel: a new message, Text.New works!";

    [Fact]
    public void ANewMessageGoesIntoEveryLanguagesTable() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        using VbfArchive archive = GameData.OpenArchive(dir!);
        byte[]? Read(string path) => archive.Find(path) is { } e ? archive.Read(e) : null;
        byte[] vanilla = Read(Event)!;

        // A prefix on worker 0's first method: MsgSetText(1, the new message).
        WorkerImage w0 = ScriptImage.FromBlock(ScriptFile.Parse(vanilla).Script).Workers[0];
        uint start = w0.Entries[0], end = w0.Entries.Where(e => e > start).DefaultIfEmpty((uint)w0.Code.Length).Min();
        ScriptPackage p = new() { File = Event, Script = "kami0000", Messages = [new PackageMessage(null, Text)] };
        p.Vanilla.Workers.Add(new WorkerRef(0, "W00", VanillaHash.Worker(w0)));
        p.Vanilla.Entries.Add(new EntryRef(0, "First", [.. Enumerable.Range(0, w0.Entries.Length).Where(e => w0.Entries[e] == start)], start, end, VanillaHash.Range(w0, start, end)));
        p.Patches.Add(new EntryPatch { Kind = PatchKind.Prefix, Worker = 0, Name = "Say", Target = "First", Code = LinkerTests.Frag("pushimm 1\npushimm @newmessage:0\nfunc_facc 0x0064") });
        AtelMod mod = new() { Id = "test", Manifest = new ModManifest { SourceLanguage = "en" } };
        mod.Packages[Event] = p;

        List<LinkedFile> linked = ModSet.Link([mod], Read);
        LinkResult script = linked.Single(f => f.Path == Event).Result!;
        foreach (LinkMessage m in script.Messages) output.WriteLine(m.ToString());
        Assert.True(script.Changed);
        int index = Assert.Single(script.NewMessages).Index;
        Assert.Equal(MessageLinker.FirstFree(Event, Read), index);

        ScriptMessages texts = new(archive);
        int tables = 0;
        foreach (GameLanguage language in GameLanguages.All) {
            string path = language.EventMessagesPath("kami0000");
            if (Read(path) is not byte[] before) continue;
            tables++;
            MessageTable old = MessageTable.Parse(before), now = MessageTable.Parse(linked.Single(f => f.Path == path).Result!.File);
            Assert.Equal(index + 1, now.Messages.Count);
            // The vanilla messages are unchanged; the new one is the text (in full-width letters where the charset has no ASCII).
            for (int i = 0; i < old.Messages.Count; i++) Assert.Equal(old.Messages[i].Text, now.Messages[i].Text);
            string decoded = texts.Charset(language).Display(now.Messages[index].Text!);
            output.WriteLine($"{language}: {old.Messages.Count} -> {now.Messages.Count}: {decoded}");
            char space = language == GameLanguage.Korean ? ' ' : '\u3000';
            string expected = language.IsAsian() ? new string([.. Text.Select(c => c == ' ' ? space : (char)(c - 0x21 + 0xff01))]) : Text;
            Assert.Equal(expected, decoded);
        }
        Assert.Equal(8, tables);
    }
}
