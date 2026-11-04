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
        Assert.Equal(["Monsters/M999/M999.cs", "Monsters/M999/M999.Data.cs", "Monsters/M999/M999.Layout.cs", "Monsters/M999/W00.cs", "Monsters/M999/W01.cs"], files.Select(f => f.RelativePath));
        Assert.All(files, f => Assert.Contains("namespace Monsters.M999;", f.Text, StringComparison.Ordinal));
        // The data areas, also the workers' private data, are in the data part; the pins in the layout part.
        Assert.Contains("[SharedData(", files[1].Text, StringComparison.Ordinal);
        Assert.Contains("[PrivateData(", files[1].Text, StringComparison.Ordinal);
        Assert.Contains("[Header(", files[2].Text, StringComparison.Ordinal);
        // The main file and the workers' files hold no layout and no data.
        foreach (SourceFile code in new[] { files[0], files[3], files[4] }) {
            foreach (string pin in new[] { "[Header(", "[Pin(", "[Ints(", "[Floats(", "[Worker(", "Data(" }) Assert.DoesNotContain(pin, code.Text, StringComparison.Ordinal);
        }

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
            ["Monsters/M997/M997.cs", "Monsters/M997/M997.Data.cs", "Monsters/M997/M997.Layout.cs", "Monsters/M997/W00.cs", "Monsters/M997/W00.2.cs", "Monsters/M997/W00.3.cs", "Monsters/M997/W00.4.cs"],
            files.Select(f => f.RelativePath));
        Assert.Contains("public sealed partial class W00 : BattleWorker", files[3].Text, StringComparison.Ordinal);
        Assert.Contains("[Register(0)] public static int I0;", files[3].Text, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class W00\n", files[6].Text, StringComparison.Ordinal);

        // Shuffled, the parts are still taken in path order, so the methods keep their layout.
        List<SyntaxTree> trees = Parse([files[5], files[3], files[6], files[0], files[2], files[4], files[1]]);
        Assert.Empty(ScriptCompiler.Check(trees));
        Assert.Equal(original, ScriptCompiler.Compile(trees).ApplyTo(file).ToArray());

        // In another order of the parts, the code is laid out differently.
        List<SourceFile> swapped = [.. files];
        swapped[4] = files[4] with { RelativePath = "Monsters/M997/W00.5.cs" };
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
            Assert.Equal(5, ScriptCompiler.SourceFiles(folder).Count);
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
        Assert.Equal("Events/Bj_BaajTemple", projects[bj.Namespace]);
        Assert.Equal("Events/Bl_Blitzball/Bltz0000", projects[bl.Namespace]);
        Assert.Equal("Battles/Kino_MushroomRock", projects[kino.Namespace]);
        Assert.Equal("Monsters", projects[m001.Namespace]);

        List<SourceFile> files = ProjectLayout.Files(projects.Values, ["Library"]);
        Assert.Contains(files, f => f.RelativePath == "Events/Bj_BaajTemple/Events.Bj_BaajTemple.csproj" && f.Text.Contains("<ProjectReference Include=\"../../Library/Library.csproj\" />", StringComparison.Ordinal));
        Assert.Contains(files, f => f.RelativePath == "Events/Bl_Blitzball/Bltz0000/Events.Bl_Blitzball.Bltz0000.csproj" && f.Text.Contains("\"../../../Library/Library.csproj\"", StringComparison.Ordinal));
        Assert.DoesNotContain("ProjectReference", files.Single(f => f.RelativePath == "Library/Library.csproj").Text, StringComparison.Ordinal);
        Assert.Contains("HintPath=\"$(AtelGameAssembly)\"", files.Single(f => f.RelativePath == "Directory.Build.props").Text, StringComparison.Ordinal);
        Assert.Equal(["Atel.slnx", "Battles.slnx", "Blitzball.slnx", "Events.slnx", "Monsters.slnx", "Start.slnx"], files.Where(f => f.RelativePath.EndsWith(".slnx", StringComparison.Ordinal)).Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        Assert.Contains("Library/Library.csproj", files.Single(f => f.RelativePath == "Events.slnx").Text, StringComparison.Ordinal);
        // A single blitzball script is a project of about 2 GB to compile: only in Blitzball.slnx.
        foreach (string sln in new[] { "Atel.slnx", "Events.slnx" }) Assert.DoesNotContain("Bltz0000", files.Single(f => f.RelativePath == sln).Text, StringComparison.Ordinal);
        Assert.Contains("Events/Bl_Blitzball/Bltz0000/", files.Single(f => f.RelativePath == "Blitzball.slnx").Text, StringComparison.Ordinal);
        Assert.Contains(files, f => f.RelativePath == ".vscode/extensions.json");
    }

    [Fact]
    public void VariantsAreProjectsOfTheirOwnOutOfTheDefaultSolutions() {
        static SourceNames Event(string group, string map, string name) => SourceNames.ForPath($"ffx_ps2/ffx/master/jppc/event/obj/{group}/{map}/{name}.ebp");
        SourceNames kami = Event("ka", "kami0400", "kami0400"), thunder = Event("ka", "kami0400", "200thunder_kami0400");
        SourceNames test = Event("te", "test01", "test01"), pub = Event("te", "testpub0", "testpub0");
        SourceNames bltz = Event("bl", "bltz0000", "bltz0000"), cn = Event("bl", "bltz0000", "cn_bltz0000");
        SourceNames testBattle = SourceNames.ForPath("ffx_ps2/ffx/master/jppc/battle/btl/test00_00/test00_00.bin");
        Assert.True(ProjectLayout.IsVariant(thunder));
        Assert.True(ProjectLayout.IsVariant(cn));
        Assert.True(ProjectLayout.IsVariant(test));
        Assert.True(ProjectLayout.IsVariant(testBattle));
        Assert.False(ProjectLayout.IsVariant(kami));
        Assert.False(ProjectLayout.IsVariant(bltz));

        SourceNames[] all = [kami, thunder, test, pub, bltz, cn, testBattle];
        Dictionary<string, string> projects = ProjectLayout.Assign(all.Select(n => (n, n == bltz || n == cn ? ProjectLayout.AreaLimit : 1000L)));
        // A variant in an area of loaded scripts gets its own project; an area of variants only stays one.
        Assert.Equal("Events/Ka_ThunderPlains", projects[kami.Namespace]);
        Assert.Equal("Events/Ka_ThunderPlains/_200thunder_kami0400", projects[thunder.Namespace]);
        Assert.Equal("Events/Te", projects[test.Namespace]);
        Assert.Equal("Events/Te", projects[pub.Namespace]);
        Assert.Equal("Events/Bl_Blitzball/Cn_bltz0000", projects[cn.Namespace]);
        HashSet<string> variants = ProjectLayout.VariantFolders(all, projects);
        Assert.Equal(["Battles/Test", "Events/Bl_Blitzball/Cn_bltz0000", "Events/Ka_ThunderPlains/_200thunder_kami0400", "Events/Te"], variants.Order(StringComparer.Ordinal));

        List<SourceFile> files = ProjectLayout.Files(projects.Values.Distinct(), ["Library"], variants);
        string Text(string path) => files.Single(f => f.RelativePath == path).Text;
        foreach (string sln in new[] { "Atel.slnx", "Events.slnx", "Start.slnx", "Blitzball.slnx" }) {
            foreach (string v in variants) Assert.DoesNotContain(v + "/", Text(sln), StringComparison.Ordinal);
        }
        Assert.DoesNotContain("Battles.slnx", files.Select(f => f.RelativePath));       // its only area is a variant
        foreach (string v in variants) Assert.Contains(v + "/", Text("Variants.slnx"), StringComparison.Ordinal);
        Assert.Contains("Library/Library.csproj", Text("Variants.slnx"), StringComparison.Ordinal);
        Assert.Contains("Events/Bl_Blitzball/Bltz0000/", Text("Blitzball.slnx"), StringComparison.Ordinal);
        // The area's project leaves the variant's folder to the variant's project.
        Assert.Contains("<DefaultItemExcludes>$(DefaultItemExcludes);_200thunder_kami0400/**</DefaultItemExcludes>",
            Text("Events/Ka_ThunderPlains/Events.Ka_ThunderPlains.csproj"), StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultItemExcludes", Text("Events/Te/Events.Te.csproj"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStartSolutionHoldsTheThunderPlainsAndTheMonsters() {
        List<SourceFile> files = ProjectLayout.Files(["Monsters", "Events/Ka_ThunderPlains", "Events/Bj_BaajTemple", "Battles/Kami_ThunderPlains"], ["Library"]);
        string start = files.Single(f => f.RelativePath == "Start.slnx").Text;
        Assert.Contains("Events/Ka_ThunderPlains/", start, StringComparison.Ordinal);
        Assert.Contains("Battles/Kami_ThunderPlains/", start, StringComparison.Ordinal);
        Assert.Contains("Monsters/Monsters.csproj", start, StringComparison.Ordinal);
        Assert.DoesNotContain("Bj_BaajTemple", start, StringComparison.Ordinal);
        Assert.Contains("\"dotnet.defaultSolution\": \"Start.slnx\"", files.Single(f => f.RelativePath == ".vscode/settings.json").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptsThePcBuildDoesNotLoadSayItAtTheTop() {
        Assert.Contains("Not loaded by the PC build", AreaNames.ScriptNote("cn_bltz0000"), StringComparison.Ordinal);
        Assert.Contains("debug-menu switch", AreaNames.ScriptNote("200thunder_kami0400"), StringComparison.Ordinal);
        Assert.Null(AreaNames.ScriptNote("kami0400"));
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
        Assert.Equal(("Shared0001", "BattleWorker"), (c.Name, c.Base));
        Assert.Equal(["Monsters.M996.W00", "Monsters.M997.W00"], c.Users);
        Assert.Equal(2, shared.SharedWorkers);

        List<SourceFile> libraryFiles = shared.Files();
        Assert.Equal("Library/Shared0001.cs", Assert.Single(libraryFiles).RelativePath);
        Assert.Contains("public abstract class Shared0001 : BattleWorker", libraryFiles[0].Text, StringComparison.Ordinal);
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
        Assert.Equal("FunctionWorker", Assert.Single(shared.Classes).Base);
        Assert.All(shared.Scripts, s => Assert.Equal(["Shared0001", "BattleWorker"], s.Workers.Select(w => w.Base)));
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
