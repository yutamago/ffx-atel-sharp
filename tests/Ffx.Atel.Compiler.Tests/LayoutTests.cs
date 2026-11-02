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
        Assert.Contains("[Register(0)] public static int I0;", files[2].Text, StringComparison.Ordinal);
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

    [Fact]
    public void ProjectsAreMapAreasAndBigAreasAreSplit() {
        SourceNames bj = SourceNames.ForPath("ffx_ps2/ffx/master/jppc/event/obj/bj/bjyt0400/bjyt0400.ebp");
        SourceNames bl = SourceNames.ForPath("ffx_ps2/ffx/master/jppc/event/obj/bl/bltz0000/bltz0000.ebp");
        SourceNames kino = SourceNames.ForPath("ffx_ps2/ffx/master/jppc/battle/btl/kino00_00/kino00_00.bin");
        SourceNames m001 = SourceNames.ForPath("ffx_ps2/ffx/master/jppc/battle/mon/_m001/m001.bin");
        Dictionary<string, string> projects = ProjectLayout.Assign([(bj, 1000), (bl, ProjectLayout.AreaLimit + 1), (kino, 1000), (m001, 1000)]);
        Assert.Equal("Events/Bj", projects[bj.Namespace]);
        Assert.Equal("Events/Bl/Bltz0000", projects[bl.Namespace]);
        Assert.Equal("Battles/Kino", projects[kino.Namespace]);
        Assert.Equal("Monsters", projects[m001.Namespace]);

        List<SourceFile> files = ProjectLayout.Files(projects.Values, ["Library"]);
        Assert.Contains(files, f => f.RelativePath == "Events/Bj/Events.Bj.csproj" && f.Text.Contains("<ProjectReference Include=\"../../Library/Library.csproj\" />", StringComparison.Ordinal));
        Assert.Contains(files, f => f.RelativePath == "Events/Bl/Bltz0000/Events.Bl.Bltz0000.csproj" && f.Text.Contains("\"../../../Library/Library.csproj\"", StringComparison.Ordinal));
        Assert.DoesNotContain("ProjectReference", files.Single(f => f.RelativePath == "Library/Library.csproj").Text, StringComparison.Ordinal);
        Assert.Contains("HintPath=\"$(AtelGameAssembly)\"", files.Single(f => f.RelativePath == "Directory.Build.props").Text, StringComparison.Ordinal);
        Assert.Equal(["Atel.slnx", "Battles.slnx", "Events.slnx", "Monsters.slnx"], files.Where(f => f.RelativePath.EndsWith(".slnx", StringComparison.Ordinal)).Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        Assert.Contains("Library/Library.csproj", files.Single(f => f.RelativePath == "Events.slnx").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SdkProjectsNameTheSdkAndItsVersion() {
        List<SourceFile> files = ProjectLayout.Files(["Monsters", "Events/Bj"], ["Library"], GameReference.Sdk, "1.2.3-alpha", localFeed: true);
        Assert.StartsWith("<Project Sdk=\"Ffx.Atel.Sdk\">", files.Single(f => f.RelativePath == "Monsters/Monsters.csproj").Text, StringComparison.Ordinal);
        Assert.Contains("\"Ffx.Atel.Sdk\": \"1.2.3-alpha\"", files.Single(f => f.RelativePath == "global.json").Text, StringComparison.Ordinal);
        Assert.Contains("<AtelProjectKind>Vanilla</AtelProjectKind>", files.Single(f => f.RelativePath == "Directory.Build.props").Text, StringComparison.Ordinal);
        Assert.Contains("value=\".atel/packages\"", files.Single(f => f.RelativePath == "nuget.config").Text, StringComparison.Ordinal);
        Assert.DoesNotContain(files, f => f.Text.Contains("HintPath", StringComparison.Ordinal));
        Assert.DoesNotContain(ProjectLayout.Files(["Monsters"], [], GameReference.Sdk, "1.2.3-alpha", localFeed: false), f => f.RelativePath == "nuget.config");
    }

    private static string Monster(string n) => $"ffx_ps2/ffx/master/jppc/battle/mon/_{n}/{n}.bin";

    [Fact]
    public void ExactCopiesDeriveFromALibraryClass() {
        byte[] original = ThreeEntries();
        ScriptFile file = ScriptFile.Parse(original);
        DecompiledScript a = ScriptDecompiler.Decompile(Monster("m997"), file), b = ScriptDecompiler.Decompile(Monster("m996"), file);
        SharedLibrary shared = SharedWorkers.Build([a.Parts, b.Parts]);
        SharedWorker c = Assert.Single(shared.Classes);
        Assert.Equal(("Shared0001", "Worker2"), (c.Name, c.Base));
        Assert.Equal(["Monsters.M996.W00", "Monsters.M997.W00"], c.Users);
        Assert.Equal(2, shared.SharedWorkers);

        List<SourceFile> libraryFiles = shared.Files();
        Assert.Equal("Library/Shared0001.cs", Assert.Single(libraryFiles).RelativePath);
        Assert.Contains("public abstract class Shared0001 : Worker2", libraryFiles[0].Text, StringComparison.Ordinal);
        ScriptLibrary library = ScriptLibrary.FromTrees(Parse(libraryFiles));
        Assert.Empty(library.Check());
        foreach (ScriptSource script in shared.Scripts) {
            List<SourceFile> files = SourceLayout.Files(script);
            SourceFile worker = files.Single(f => f.RelativePath.EndsWith("/W00.cs", StringComparison.Ordinal));
            Assert.Contains("using Library;", worker.Text, StringComparison.Ordinal);
            Assert.Contains("public sealed partial class W00 : Shared0001\n{\n}\n", worker.Text, StringComparison.Ordinal);
            List<SyntaxTree> trees = Parse(files);
            Assert.Empty(ScriptCompiler.Check(trees, library));
            Assert.Equal(original, ScriptCompiler.Compile(trees, library).ApplyTo(file).ToArray());
            CompileException ex = Assert.Throws<CompileException>(() => ScriptCompiler.Compile(trees));
            Assert.Contains("a library class", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WorkersThatNameTheirScriptsVariablesAreNotShared() {
        byte[] original = SourceTests.StatementsFile();
        ScriptFile file = ScriptFile.Parse(original);
        DecompiledScript a = ScriptDecompiler.Decompile(Monster("m997"), file), b = ScriptDecompiler.Decompile(Monster("m996"), file);
        SharedLibrary shared = SharedWorkers.Build([a.Parts, b.Parts]);
        // W00 (`return;`) is shared; W01 assigns V0 and calls W00, both declared by its script.
        Assert.Equal("Worker0", Assert.Single(shared.Classes).Base);
        Assert.All(shared.Scripts, s => Assert.Equal(["Shared0001", "Worker2"], s.Workers.Select(w => w.Base)));
        ScriptLibrary library = ScriptLibrary.FromTrees(Parse(shared.Files()));
        foreach (ScriptSource script in shared.Scripts) {
            Assert.Equal(original, ScriptCompiler.Compile(Parse(SourceLayout.Files(script)), library).ApplyTo(file).ToArray());
        }
    }

    [Fact]
    public void TheLibraryIsFoundAboveAScriptFolder() {
        byte[] original = ThreeEntries();
        ScriptFile file = ScriptFile.Parse(original);
        SharedLibrary shared = SharedWorkers.Build([ScriptDecompiler.Decompile(Monster("m997"), file).Parts, ScriptDecompiler.Decompile(Monster("m996"), file).Parts]);
        string root = Path.Combine(Path.GetTempPath(), "atel-library-" + Guid.NewGuid().ToString("N"));
        try {
            foreach (SourceFile f in shared.Files().Concat(SourceLayout.Files(shared.Scripts[0]))) {
                string path = Path.Combine(root, f.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, f.Text);
            }
            string folder = Path.Combine(root, "Monsters", "M997");
            ScriptLibrary library = ScriptLibrary.Find(folder);
            Assert.Single(library.Trees);
            Assert.Equal(original, ScriptCompiler.Compile(ScriptCompiler.ParseFolder(folder), library).ApplyTo(file).ToArray());
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }
}
