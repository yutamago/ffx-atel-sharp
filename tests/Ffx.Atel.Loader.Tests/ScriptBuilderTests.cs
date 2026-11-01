// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Loader.Tests;

public sealed class ScriptBuilderTests : IDisposable {
    private readonly string _dir = Directory.CreateTempSubdirectory("atel-loader-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Efl => Path.Combine(_dir, "efl", "x");

    [Theory]
    [InlineData("; ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin\n; 3 workers\n", "ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin")]
    [InlineData(";FFX_PS2\\FFX\\Master\\x.ebp\r\nworker 0 type 1\n", "ffx_ps2/ffx/master/x.ebp")]
    [InlineData("worker 0 type 1\n", null)]
    [InlineData("; 3 workers\n", null)]
    [InlineData(";\n", null)]
    public void ReadsTheArchivePathFromTheFirstLine(string text, string? expected) {
        Assert.Equal(expected, ScriptBuilder.ArchivePathOf(text));
    }

    [Fact]
    public void WithoutSourcesItOnlyClearsItsFolder() {
        string stale = Path.Combine(Efl, "ffx_ps2", "old.ebp");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "from an earlier run");

        // No scripts/ folder: the game folder is never opened.
        Assert.Empty(ScriptBuilder.Build(_dir, Path.Combine(_dir, "no-game"), Efl));
        Assert.True(Directory.Exists(Efl));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Efl));
    }

    private static string GameOrSkip() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        return dir!;
    }

    private string WriteSource(string name, string text) {
        string path = Path.Combine(_dir, ScriptBuilder.SourceDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private const string Larva = "ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin";

    /// <summary>The file the in-game test used: the Larva's script, unchanged and grown.</summary>
    [Fact]
    public void RebuildsOntoTheVanillaFileUnchangedAndGrown() {
        string game = GameOrSkip();
        byte[] vanilla;
        string text;
        using (VbfArchive archive = GameData.OpenArchive(game)) {
            vanilla = archive.Read(Larva);
            text = $"; {Larva}\n{Assembly.Disassemble(ScriptFile.Parse(vanilla).Script)}";
        }
        // Worker 1's init (entry 0) gets one more call, so its code no longer fits in place.
        int worker1 = text.IndexOf("worker 1 ", StringComparison.Ordinal);
        int end = text.IndexOf("    end\n", worker1, StringComparison.Ordinal);
        string grown = text[..end] + "    pushimm 0\n    pushimm 1234\n    func_facc 0x70ab\n" + text[end..];
        WriteSource("a/grown.asm", grown.Replace(Larva, Larva.ToUpperInvariant(), StringComparison.Ordinal));
        WriteSource("b/same.asm", text);
        WriteSource("nowhere.asm", "; ffx_ps2/no/such/file.bin\nworker 0 type 2\n");

        List<BuiltScript> built = ScriptBuilder.Build(_dir, game, Efl);

        Assert.Equal(["a/grown.asm", "b/same.asm", "nowhere.asm"], built.Select(b => b.Source));
        Assert.Equal("not in FFX_Data.vbf", built[2].Error);
        Assert.Equal("a/grown.asm already rebuilds this file", built[1].Error);
        BuiltScript big = built[0];
        Assert.Null(big.Error);
        Assert.Equal(Larva, big.ArchivePath);
        Assert.True(big.Grew);
        Assert.True(big.ScriptSize > big.ScriptRoom);

        byte[] written = File.ReadAllBytes(Path.Combine(Efl, Larva));
        Assert.Equal(big.RebuiltSize, written.Length);
        Assert.Equal(0, (written.Length - vanilla.Length) % ScriptFile.Align);
        ScriptFile file = ScriptFile.Parse(written);
        Assert.InRange(big.ScriptSize, file.SectionLength - ScriptFile.Align + 1, file.SectionLength);
        // Everything after the script section moved along unchanged.
        ScriptFile original = ScriptFile.Parse(vanilla);
        int shift = written.Length - vanilla.Length;
        int after = original.SectionStart + original.SectionLength;
        Assert.True(written.AsSpan(after + shift).SequenceEqual(vanilla.AsSpan(after)));
    }

    [Fact]
    public void AnUnchangedSourceGivesTheVanillaBytes() {
        string game = GameOrSkip();
        byte[] vanilla;
        using (VbfArchive archive = GameData.OpenArchive(game)) {
            vanilla = archive.Read(Larva);
            WriteSource("m209.asm", $"; {Larva}\n{Assembly.Disassemble(ScriptFile.Parse(vanilla).Script)}");
        }
        BuiltScript b = Assert.Single(ScriptBuilder.Build(_dir, game, Efl));
        Assert.Null(b.Error);
        Assert.Equal(vanilla, File.ReadAllBytes(Path.Combine(Efl, Larva)));
    }
}
