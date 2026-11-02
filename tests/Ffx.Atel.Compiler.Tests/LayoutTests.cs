// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Layout;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>The folder layout of Phase 6: a script as a folder, a file per worker, workers in parts.</summary>
public sealed class LayoutTests {
    /// <summary>A monster with one worker of three entries, each its own method, using a register.</summary>
    private static byte[] ThreeEntries() {
        ScriptImage image = new() { Author = "test"u8.ToArray(), Name = "997"u8.ToArray(), Shared = new byte[0x10], Other = new byte[0x40], Event = new byte[0x10] };
        WorkerAssembler w = new();
        for (int e = 0; e < 3; e++) {
            w.PlaceEntry(e);
            w.Add($"""
                pushimm {e + 1}
                popi0
                end
                """);
        }
        AssembledWorker a = w.Finish();
        image.Workers.Add(new WorkerImage { Type = 2, Code = a.Code, Entries = a.Entries, Labels = a.Labels });
        byte[] block = image.Write();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    private static List<SyntaxTree> Parse(IEnumerable<SourceFile> files) => [.. files.Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath))];

    [Fact]
    public void AScriptIsAFolderWithAFilePerWorker() {
        byte[] original = SourceTests.MadeUpFile();
        ScriptFile file = ScriptFile.Parse(original);
        List<SourceFile> files = ScriptDecompiler.Decompile(SourceTests.MonsterPath, file).Files();
        Assert.Equal(["Monsters/M999/M999.cs", "Monsters/M999/M999.Data.cs", "Monsters/M999/W00.cs", "Monsters/M999/W01.cs"], files.Select(f => f.RelativePath));
        Assert.All(files, f => Assert.Contains("namespace Monsters.M999;", f.Text, StringComparison.Ordinal));
        Assert.Contains("[SharedData(", files[1].Text, StringComparison.Ordinal);
        Assert.Contains("[PrivateData(", files[3].Text, StringComparison.Ordinal);

        // The files' order does not matter: the workers' order is [Workers]'s.
        List<SyntaxTree> trees = Parse(Enumerable.Reverse(files));
        Assert.Empty(ScriptCompiler.Check(trees));
        Assert.Equal(original, ScriptCompiler.Compile(trees).ApplyTo(file).ToArray());
    }

    [Fact]
    public void ABigWorkerIsSplitIntoPartsInPathOrder() {
        byte[] original = ThreeEntries();
        ScriptFile file = ScriptFile.Parse(original);
        DecompiledScript d = ScriptDecompiler.Decompile("ffx_ps2/ffx/master/jppc/battle/mon/_m997/m997.bin", file);
        List<SourceFile> files = d.Files(splitLines: 1, partLines: 1);
        Assert.Equal(
            ["Monsters/M997/M997.cs", "Monsters/M997/M997.Data.cs", "Monsters/M997/W00.cs", "Monsters/M997/W00.2.cs", "Monsters/M997/W00.3.cs", "Monsters/M997/W00.4.cs"],
            files.Select(f => f.RelativePath));
        Assert.Contains("public sealed partial class W00 : Worker2", files[2].Text, StringComparison.Ordinal);
        Assert.Contains("[Register(0)] public int I0;", files[2].Text, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class W00\n", files[5].Text, StringComparison.Ordinal);

        // Shuffled, the parts are still taken in path order, so the methods keep their layout.
        List<SyntaxTree> trees = Parse([files[4], files[2], files[5], files[0], files[3], files[1]]);
        Assert.Empty(ScriptCompiler.Check(trees));
        Assert.Equal(original, ScriptCompiler.Compile(trees).ApplyTo(file).ToArray());

        // In another order of the parts, the code is laid out differently.
        List<SourceFile> swapped = [.. files];
        swapped[3] = files[3] with { RelativePath = "Monsters/M997/W00.5.cs" };
        Assert.NotEqual(original, ScriptCompiler.Compile(Parse(swapped)).ApplyTo(file).ToArray());
    }

    [Fact]
    public void AFolderIsReadWithoutBuildOutput() {
        byte[] original = SourceTests.MadeUpFile();
        ScriptFile file = ScriptFile.Parse(original);
        string root = Path.Combine(Path.GetTempPath(), "atel-layout-" + Guid.NewGuid().ToString("N"));
        try {
            foreach (SourceFile f in ScriptDecompiler.Decompile(SourceTests.MonsterPath, file).Files()) {
                string path = Path.Combine(root, f.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, f.Text);
            }
            string folder = Path.Combine(root, "Monsters", "M999");
            Directory.CreateDirectory(Path.Combine(folder, "obj", "Debug"));
            File.WriteAllText(Path.Combine(folder, "obj", "Debug", "Generated.cs"), "this is not C#");
            Assert.Equal(4, ScriptCompiler.SourceFiles(folder).Count);
            Assert.Equal(original, ScriptCompiler.Compile(ScriptCompiler.ParseFolder(folder)).ApplyTo(file).ToArray());
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }
}
