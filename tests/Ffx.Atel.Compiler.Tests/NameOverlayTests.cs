// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Naming;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>Hand-written names from the source repo's overlay (docs/source-repo.md, "Names"): names only, the same bytes.</summary>
public sealed class NameOverlayTests {
    private const string Path = "ffx_ps2/ffx/master/jppc/battle/mon/_m997/m997.bin";

    /// <summary>Two workers: W00 requests W01's entry 2 (and awaits it); a shared and a private variable.</summary>
    private static byte[] RequestsFile() {
        ScriptImage image = new() {
            Author = "test"u8.ToArray(), Name = "997"u8.ToArray(), Shared = new byte[0x20], Other = new byte[0x40], Event = new byte[0x10],
        };
        image.Variables.Add(new VarEntry(0x5600_0010, 1, 0));    // int, private 0x10
        image.Variables.Add(new VarEntry(0x0800_0004, 1, 0));    // byte, shared 0x04
        image.Variables.Add(new VarEntry(0x0800_0004, 1, 0));    // the same location again

        WorkerAssembler w0 = new();
        w0.PlaceEntry(0);
        w0.Add("""
                pushimm 1
                pushimm 1
                pushimm 2
                req0
                popfacc
                pushimm 1
                pushimm 1
                pushimm 2
                reqe0
                popfacc
                pushimm 3
                popv v1
                pushimm 4
                popv v2
                end
            """);
        AssembledWorker a0 = w0.Finish();
        image.Workers.Add(new WorkerImage { Type = 1, Code = a0.Code, Entries = a0.Entries, Labels = a0.Labels });

        WorkerAssembler w1 = new();
        w1.PlaceEntry(0);
        w1.Add("end");
        w1.PlaceEntry(1);
        w1.Add("end");
        w1.PlaceEntry(2);
        w1.Add("""
                pushimm 7
                popv v0
                end
            """);
        AssembledWorker a1 = w1.Finish();
        image.Workers.Add(new WorkerImage { Type = 1, Code = a1.Code, Entries = a1.Entries, Labels = a1.Labels });

        byte[] block = image.Write();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    private const string Names = """
        # m997: a test
        [workers]
        1 = "Helper"          # W01

        [entries]
        "1.2" = "WaveAsync"   # Async is added where needed, so it may be left out

        [variables]
        "Shared:0x04" = "Counter"
        "Private:0x0010" = "Steps"
        """;

    [Fact]
    public void NamesAreAppliedAndTheBytesStayTheSame() {
        byte[] original = RequestsFile();
        ScriptFile file = ScriptFile.Parse(original);
        DecompiledScript plain = ScriptDecompiler.Decompile(Path, file);
        Assert.Contains("_ = W01.E2Async();", plain.Source, StringComparison.Ordinal);

        DecompiledScript named = ScriptDecompiler.Decompile(Path, file, overlay: NameOverlay.Parse(Names, "monsters/m997.toml"));
        string[] expected = [
            "typeof(W00), typeof(Helper)",
            "public sealed partial class Helper : Worker1",
            "public static async Task WaveAsync()",
            "_ = Helper.WaveAsync();",
            "await Helper.WaveAsync();",
            "[Var(Storage.Private, 0x0010)] public static int Steps;",
            "[Var(Storage.Shared, 0x0004)] public static byte Counter;",
            "[Var(Storage.Shared, 0x0004)] public static byte Counter_2;",
            "Counter = 3;",
            "Counter_2 = 4;",
            "Steps = 7;",
        ];
        foreach (string line in expected) Assert.Contains(line, named.Source, StringComparison.Ordinal);

        SyntaxTree tree = ScriptCompiler.Parse(named.Source, "m997.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        Assert.Equal(original, ScriptCompiler.Compile([tree]).ApplyTo(file).ToArray());
    }

    [Theory]
    [InlineData("[workers]\n5 = \"Nobody\"", "the script has 2 workers")]
    [InlineData("[workers]\n0 = \"Same\"\n1 = \"Same\"", "two workers have the same name")]
    [InlineData("[workers]\n0 = \"W01\"", "default name")]
    [InlineData("[workers]\n0 = \"Common\"", "already a name")]
    [InlineData("[entries]\n\"1.0\" = \"Start\"", "no method starting with entry 0")]
    [InlineData("[variables]\n\"Event:0x40\" = \"Nothing\"", "no variable at Event:0x0040")]
    public void NamesThatDoNotFitAreRefused(string text, string message) {
        ScriptFile file = ScriptFile.Parse(RequestsFile());
        FormatException ex = Assert.Throws<FormatException>(() => ScriptDecompiler.Decompile(Path, file, overlay: NameOverlay.Parse(text, "monsters/m997.toml")));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.StartsWith("monsters/m997.toml", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1 = \"A\"", "before any section")]
    [InlineData("[actors]", "unknown section")]
    [InlineData("[workers]\nx = \"A\"", "a worker is its number")]
    [InlineData("[workers]\n1 = A", "quoted string")]
    [InlineData("[workers]\n1 = \"class\"", "not a C# name")]
    [InlineData("[workers]\n1 = \"2nd\"", "not a C# name")]
    [InlineData("[entries]\n\"1\" = \"A\"", "worker.entry")]
    [InlineData("[variables]\n\"Nowhere:0x10\" = \"A\"", "Storage:offset")]
    [InlineData("[workers]\n1 = \"A\"\n1 = \"B\"", "named twice")]
    public void MalformedFilesAreRefusedWithTheLine(string text, string message) {
        FormatException ex = Assert.Throws<FormatException>(() => NameOverlay.Parse(text, "f.toml"));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.StartsWith("f.toml:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentsAndNumbersAreRead() {
        NameOverlay o = NameOverlay.Parse("""
            # a comment
            [workers]   # trailing
            3 = "Gate"  # W03
            [variables]
            6:16 = "Seen"
            "Save:0x0a33" = "Flags"
            """, "f.toml");
        Assert.Equal("Gate", o.Workers[3]);
        Assert.Equal("Seen", o.Variables[(6, 16)]);
        Assert.Equal("Flags", o.Variables[(0, 0xa33)]);
        Assert.Same(NameOverlay.Empty, NameOverlay.Load(null, Path));    // data/names/ has no m997
    }

    [Fact]
    public void AnotherFolderLaidOutAsDataNamesIsRead() {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "atel-names-" + Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(System.IO.Path.Combine(root, "monsters"));
            File.WriteAllText(System.IO.Path.Combine(root, "monsters", "m997.toml"), Names);
            NameOverlay o = NameOverlay.Load(root, Path);
            Assert.Equal("Helper", o.Workers[1]);
            Assert.Equal("monsters/m997.toml", o.Source);
            Assert.Same(NameOverlay.Empty, NameOverlay.Load(root, "ffx_ps2/ffx/master/jppc/battle/mon/_m996/m996.bin"));
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EveryEmbeddedFileIsAScriptsNamesFile() {
        // data/names/<events|monsters|battles>/<script>.toml, each one parses.
        foreach (string f in NameOverlay.EmbeddedFiles) {
            string[] parts = f.Split('/');
            Assert.True(parts.Length == 2 && parts[0] is "events" or "monsters" or "battles" && parts[1].EndsWith(".toml", StringComparison.Ordinal), f);
        }
    }
}
