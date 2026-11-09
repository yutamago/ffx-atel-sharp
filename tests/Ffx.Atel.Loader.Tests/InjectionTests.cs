// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Text;
using Ffx.Atel.Linker;

namespace Ffx.Atel.Loader.Tests;

/// <summary>Runtime injection packages: the loader's <c>inject/</c> folder (docs/formats/debug.md).</summary>
public sealed class InjectionTests : IDisposable {
    private readonly string _dir = Directory.CreateTempSubdirectory("atel-inject-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Mods => Path.Combine(_dir, "mods");
    private string Loader => Path.Combine(Mods, "yuta.atel");
    private string Efl => Path.Combine(Loader, "efl", "x");

    private const string Script = "ffx_ps2/test/m995.bin";

    private static byte[] Vanilla() {
        ScriptImage image = new() { Author = "t"u8.ToArray(), Name = "995"u8.ToArray(), Shared = new byte[0x20], Other = new byte[0x40] };
        WorkerAssembler a = new();
        a.Add("e0:\n pushimm 1\n popi0\n end\ne1:\n end");
        AssembledWorker w = a.Finish();
        image.Workers.Add(new WorkerImage { Type = 2, Code = w.Code, Entries = w.Entries, Labels = w.Labels });
        byte[] block = image.Write();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    /// <summary>A postfix on entry 0 that sets i0 to <paramref name="value"/>, with a debug text and a message.</summary>
    private static ScriptPackage Package(byte[] vanilla, byte value) {
        WorkerImage w = ScriptImage.FromBlock(ScriptFile.Parse(vanilla).Script).Workers[0];
        ScriptPackage p = new() { File = Script, Script = "m995", DebugStrings = [$"value {value}"], Messages = [new PackageMessage(3, "Hi")] };
        p.Vanilla.Workers.Add(new WorkerRef(0, "W00", VanillaHash.Worker(w)));
        p.Vanilla.Entries.Add(new EntryRef(0, "Init", [0], 0, w.Entries[1], VanillaHash.Range(w, 0, w.Entries[1])));
        p.Patches.Add(new EntryPatch { Kind = PatchKind.Postfix, Worker = 0, Name = "After", Target = "Init", Code = new Fragment { Bytes = [0xae, value, 0, 0x59] } });
        return p;
    }

    private void Inject(ScriptPackage p, string name = "m995.json") {
        string file = Path.Combine(Loader, Injection.Folder, "sub", name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, p.ToJson());
    }

    private static string Code(byte[] file) {
        System.Text.StringBuilder sb = new();
        Assembly.DisassembleWorker(sb, ScriptFile.Parse(file).Script, 0);
        return sb.ToString();
    }

    [Fact]
    public void InjectedPackagesLinkAfterTheModsAndCanBeLinkedAgain() {
        byte[] vanilla = Vanilla();
        Directory.CreateDirectory(Loader);
        File.WriteAllLines(Path.Combine(Mods, ModLoader.LoadOrderFile), ["yuta.atel"]);
        Inject(Package(vanilla, 7));
        AtelMod injected = Injection.Read(Loader);
        Assert.Empty(injected.Errors);
        Assert.Equal(["value 7"], injected.DebugStrings);

        LinkRun run = ModLoader.Link(Mods, Efl, path => path == Script ? vanilla : null, new HashSet<string>(), injected);
        Assert.Equal([Script], run.Written);
        Assert.Equal([Injection.ModId], run.Linked.Select(m => m.Id));
        string served = Path.Combine(Efl, "ffx_ps2", "test", "m995.bin");
        Assert.Contains("pushimm 7\n    popi0\n    end", Code(File.ReadAllBytes(served)), StringComparison.Ordinal);

        // Changed: linked again from the vanilla file, not on top of the last result.
        Inject(Package(vanilla, 9));
        LinkedFile? f = Assert.Single(Injection.Relink(run.Linked, Injection.Read(Loader), Script, _ => vanilla));
        Assert.NotNull(f?.Result);
        string code = Code(f!.Result!.File);
        Assert.Contains("pushimm 9\n    popi0\n    end", code, StringComparison.Ordinal);
        Assert.DoesNotContain("pushimm 7", code, StringComparison.Ordinal);

        // Removed: nothing left to link.
        File.Delete(Path.Combine(Loader, Injection.Folder, "sub", "m995.json"));
        Assert.Empty(Injection.Relink(run.Linked, Injection.Read(Loader), Script, _ => vanilla));
    }

    [Fact]
    public void ABrokenPackageIsAnError() {
        Directory.CreateDirectory(Path.Combine(Loader, Injection.Folder));
        File.WriteAllText(Path.Combine(Loader, Injection.Folder, "bad.json"), "{ not json");
        AtelMod injected = Injection.Read(Loader);
        Assert.Empty(injected.Packages);
        Assert.Single(injected.Errors);
    }

    [Fact]
    public void AMessagesOnlyPackageForABattleLinks() {
        // As in docs/research/loader.md's in-game steps: texts for battle kami00_00's table, no code.
        byte[] vanilla = Vanilla();
        const string battle = "ffx_ps2/ffx/master/jppc/battle/btl/kami00_00/kami00_00.bin";
        ScriptPackage p = ScriptPackage.FromJson($$"""{ "file": "{{battle}}", "script": "kami00_00", "messages": [ { "index": 0, "text": "Atel" } ] }""");
        Assert.Equal((Ffx.Atel.Bytecode.ScriptCategory.Battle, "kami00_00"), Ffx.Atel.Bytecode.GameData.Identify(p.File));
        AtelMod injected = new() { Id = Injection.ModId };
        injected.Packages[battle] = p;
        LinkedFile linked = Assert.Single(Injection.Relink([], injected, battle, path => path == battle ? vanilla : null));
        Assert.Null(linked.Error);
        Assert.Equal(vanilla, linked.Result!.File);
    }

    private static byte[] Table() => new MessageTable([
        new Message([0x30, 0x31], 0x0100),
        new Message(null, 0),
        new Message([0x32], 7),
    ]).Write();

    [Fact]
    public void TheTableInMemoryIsMeasuredFromItsEntries() {
        byte[] table = Table();
        byte[] memory = [.. table, 0x55, 0x55, 0x55];    // what follows it in memory
        int? length = Injection.TableLength(o => BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan(o)), o => memory[o]);
        Assert.Equal(table.Length, length);
        Assert.Null(Injection.TableLength(_ => 3, _ => 0));
    }

    [Fact]
    public void MessagesAreSetByIndex() {
        byte[] result = Injection.SetMessages(Table(), [(2, [0x40, 0x41]), (5, [0x42])]);
        IReadOnlyList<Message> m = MessageTable.Parse(result).Messages;
        Assert.Equal(6, m.Count);
        Assert.Equal([0x30, 0x31], m[0].Text);
        Assert.Null(m[1].Text);                 // still the .ebp's own
        Assert.Equal([0x40, 0x41], m[2].Text);
        Assert.Equal(7, m[2].Flags);            // flags kept
        Assert.Equal([], m[3].Text!);           // the gap: empty
        Assert.Equal([0x42], m[5].Text);
        Assert.Throws<InvalidDataException>(() => Injection.SetMessages(Table(), [(-1, [])]));
    }

    [Fact]
    public void TheRunningEventAndLanguage() {
        Assert.True(Injection.IsEvent("ka/kami0000", "kami0000"));
        Assert.True(Injection.IsEvent("ka/kami0000", "KAMI0000"));
        Assert.False(Injection.IsEvent("ka/kami0000", "kami0001"));
        Assert.Equal(GameLanguage.English, Injection.Language(1));
        Assert.Equal(GameLanguage.German, Injection.Language(4));
        Assert.Equal(GameLanguage.Korean, Injection.Language(9));
        Assert.Equal(GameLanguage.Chinese, Injection.Language(10));
        Assert.Null(Injection.Language(18));
    }
}
