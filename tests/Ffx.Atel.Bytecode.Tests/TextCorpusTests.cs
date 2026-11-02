// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Text;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Bytecode.Tests;

/// <summary>The message tables of the player's game. Need <c>FFX_GAME_DIR</c>; skipped without it. Nothing of the text is printed.</summary>
public sealed class TextCorpusTests(ITestOutputHelper output) {
    /// <summary>
    ///     Every shipped table (events and battles, every language) parses and writes back to the same bytes,
    ///     every event's own table (<c>.ebp</c> section 1) parses, and every message decodes in its language.
    /// </summary>
    [Fact]
    public void EveryMessageTableParsesAndWritesBack() {
        using VbfArchive archive = CorpusTests.OpenOrSkip();
        ScriptMessages messages = new(archive);
        int tables = 0, texts = 0, raw = 0;
        List<string> failures = [];
        foreach (GameLanguage language in GameLanguages.All) {
            string prefix = $"ffx_ps2/ffx/master/new_{language.GameCode()}pc/";
            Charset charset = messages.Charset(language);
            foreach (string path in archive.Entries.Select(e => e.Path).Where(p => (p.StartsWith(prefix + "event/obj_ps3/", StringComparison.Ordinal) || p.StartsWith(prefix + "battle/btl/", StringComparison.Ordinal))
                         && p.EndsWith(".bin", StringComparison.Ordinal))) { // the Japanese folders also hold .ftc files
                byte[] bytes = archive.Read(path);
                try {
                    MessageTable table = MessageTable.Parse(bytes);
                    if (!table.Write().AsSpan().SequenceEqual(bytes)) failures.Add($"{path}: writes back differently");
                    foreach (Message m in table.Messages.Where(m => m.Text is not null)) {
                        string decoded = charset.Decode(m.Text);
                        if (!charset.Encode(decoded).AsSpan().SequenceEqual(m.Text)) failures.Add($"{path}: a message does not encode back");
                        raw += decoded.Split("{raw:").Length - 1;
                        texts++;
                    }
                    tables++;
                }
                catch (Exception ex) when (ex is InvalidDataException or TextEncodeException) {
                    failures.Add($"{path}: {ex.Message}");
                }
            }
        }
        // An event with no table at all (none of its own, none in English) must not show messages.
        int events = 0, originals = 0, without = 0;
        foreach (string path in GameData.ScriptPaths(archive).Where(p => p.StartsWith(GameData.EventPrefix, StringComparison.Ordinal))) {
            events++;
            if (ScriptMessages.OriginalTable(archive.Read(path)) is not null) originals++;
            if (messages.Display(path, GameLanguage.English) is not null) continue;
            without++;
            string asm = Script.Assembly.Disassemble(Script.ScriptFile.Parse(archive.Read(path)).Script);
            // memochek, a developer map: calls MsgSetText, but has no section 1 and no table in any language.
            if ((asm.Contains(" 0x0064", StringComparison.Ordinal) || asm.Contains(" 0x0236", StringComparison.Ordinal)) && GameData.Identify(path)?.Name != "memochek") {
                failures.Add($"{path}: shows messages but has no table");
            }
        }
        output.WriteLine($"{tables} tables, {texts} messages ({raw} undecodable bytes); {originals} of {events} events have their own table, {without} have no table at all");
        foreach (string f in failures.Take(20)) output.WriteLine(f);
        Assert.Empty(failures.Take(20));
    }
}
