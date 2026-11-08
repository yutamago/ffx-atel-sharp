// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Linker;

namespace Ffx.Atel.Loader.Tests;

public sealed class ModLoaderTests : IDisposable {
    private readonly string _dir = Directory.CreateTempSubdirectory("atel-mods-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Mods => Path.Combine(_dir, "mods");
    private string Efl => Path.Combine(Mods, "yuta.atel", "efl", "x");

    private const string Script = "ffx_ps2/test/m996.bin";

    /// <summary>A monster file with one worker: entry 0 sets a register and ends, entry 1 ends.</summary>
    private static byte[] Vanilla() {
        ScriptImage image = new() { Author = "t"u8.ToArray(), Name = "996"u8.ToArray(), Shared = new byte[0x20], Other = new byte[0x40] };
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

    private void AtelMod(string id, byte[] vanilla, string? toml = null) {
        ScriptImage v = ScriptImage.FromBlock(ScriptFile.Parse(vanilla).Script);
        WorkerImage w = v.Workers[0];
        ScriptPackage p = new() { File = Script, Script = "m996" };
        p.Vanilla.Workers.Add(new WorkerRef(0, "W00", VanillaHash.Worker(w)));
        p.Vanilla.Entries.Add(new EntryRef(0, "Init", [0], 0, w.Entries[1], VanillaHash.Range(w, 0, w.Entries[1])));
        // pushimm 2; popi0
        p.Patches.Add(new EntryPatch { Kind = PatchKind.Postfix, Worker = 0, Name = $"After_{id}", Target = "Init", Code = new Fragment { Bytes = [0xae, 2, 0, 0x59] } });
        string file = Path.Combine(Mods, id, AtelMod_.PackageDir, "ffx_ps2", "test", "m996.bin.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, p.ToJson());
        if (toml is not null) File.WriteAllText(Path.Combine(Mods, id, AtelMod_.ManifestFile), toml);
    }

    [Fact]
    public void LinksTheAtelModsOfTheLoadOrderIntoItsFolder() {
        byte[] vanilla = Vanilla();
        Directory.CreateDirectory(Path.Combine(Mods, "yuta.atel"));
        Directory.CreateDirectory(Path.Combine(Mods, "some.other.mod", "efl"));
        AtelMod("first", vanilla);
        AtelMod("second", vanilla, "requires = [\"first\"]");
        AtelMod("disabled", vanilla);
        File.WriteAllLines(Path.Combine(Mods, ModLoader.LoadOrderFile), ["yuta.atel", "some.other.mod", "", " first ", "second"]);

        LinkRun run = ModLoader.Link(Mods, Efl, path => path == Script ? vanilla : null, new HashSet<string>());
        Assert.Equal(["first", "second"], run.Mods);
        Assert.Equal([Script], run.Written);
        Assert.DoesNotContain(run.Messages, m => m.Severity == LinkSeverity.Error);
        string served = Path.Combine(Efl, "ffx_ps2", "test", "m996.bin");
        ScriptBlock b = ScriptFile.Parse(File.ReadAllBytes(served)).Script;
        System.Text.StringBuilder sb = new();
        Assembly.DisassembleWorker(sb, b, 0);
        // Both postfixes, in load order, after the original.
        Assert.Contains("pushimm 2\n    popi0\n    pushimm 2\n    popi0\n    end", sb.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesAFileTheAsmPathRebuildsAlone() {
        byte[] vanilla = Vanilla();
        AtelMod("first", vanilla);
        File.WriteAllLines(Path.Combine(Mods, ModLoader.LoadOrderFile), ["first"]);
        LinkRun run = ModLoader.Link(Mods, Efl, _ => vanilla, new HashSet<string> { Script });
        Assert.Empty(run.Written);
        Assert.Contains(run.Messages, m => m.Severity == LinkSeverity.Conflict);
    }

    [Fact]
    public void WithoutALoadOrderThereIsNothingToLink() {
        LinkRun run = ModLoader.Link(Mods, Efl, _ => throw new InvalidOperationException("never read"), new HashSet<string>());
        Assert.Empty(run.Mods);
        Assert.Empty(run.Written);
    }

    /// <summary>
    ///     Saved fields through the loader's link: one table for the load order before any file is linked; a mod whose fields
    ///     do not fit is left out with an error, and the linked script's variable is storage 0 at its field's offset.
    /// </summary>
    [Fact]
    public void PlacesTheModsSavedFieldsBeforeLinking() {
        byte[] vanilla = Vanilla();
        AtelMod("counter", vanilla);
        string package = Path.Combine(Mods, "counter", AtelMod_.PackageDir, "ffx_ps2", "test", "m996.bin.json");
        ScriptPackage p = ScriptPackage.FromJson(File.ReadAllText(package));
        p.Variables.Add(new NewVariable { Name = "Count", Type = 5, Storage = 0, Saved = "Counter.Progress.Count" });
        p.Format = p.NeededFormat();
        File.WriteAllText(package, p.ToJson());
        File.WriteAllText(Path.Combine(Mods, "counter", AtelMod_.PackageDir, SavedFile.FileName),
            new SavedFile { Fields = [new SavedField { Key = "Counter.Progress.Count", Type = 5 }] }.ToJson());
        Directory.CreateDirectory(Path.Combine(Mods, "huge", AtelMod_.PackageDir));
        File.WriteAllText(Path.Combine(Mods, "huge", AtelMod_.PackageDir, SavedFile.FileName),
            new SavedFile { Fields = [new SavedField { Key = "Huge.Table", Type = 0, Length = SaveArea.Length, Array = true }] }.ToJson());
        File.WriteAllLines(Path.Combine(Mods, ModLoader.LoadOrderFile), ["yuta.atel", "counter", "huge"]);

        LinkRun run = ModLoader.Link(Mods, Efl, path => path == Script ? vanilla : null, new HashSet<string>());
        Assert.Equal(["counter"], run.Linked.Select(m => m.Id));
        Assert.Contains(run.Messages, m => m.Mod == "huge" && m.Severity == LinkSeverity.Error && m.Text.Contains("do not fit", StringComparison.Ordinal));
        Assert.Equal(SaveArea.Start, run.Saved.Find("counter/Counter.Progress.Count")!.Offset);
        Assert.Equal([Script], run.Written);
        ScriptImage linked = ScriptImage.FromBlock(ScriptFile.Parse(File.ReadAllBytes(Path.Combine(Efl, "ffx_ps2", "test", "m996.bin"))).Script);
        VarEntry count = linked.Variables[^1];
        Assert.Equal((0, SaveArea.Start, 5), (count.Storage, count.Offset, count.Type));
    }
}

/// <summary>The linker's mod folder names (the test's own helper is called <c>AtelMod</c>).</summary>
file static class AtelMod_ {
    public const string PackageDir = Linker.AtelMod.PackageDir;
    public const string ManifestFile = Linker.AtelMod.ManifestFile;
}
