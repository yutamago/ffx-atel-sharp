// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Layout;
using Ffx.Atel.Decompiler.Naming;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>
///     Comments read off the code (who requests an entry, the story index) and library class names by
///     signature (docs/source-repo.md, "Notes", "Library names"): comments and names only, the same bytes.
/// </summary>
public sealed class FlowNotesTests {
    private static string Monster(string n) => $"ffx_ps2/ffx/master/jppc/battle/mon/_{n}/{n}.bin";

    /// <summary>
    ///     W00's Main switches on the story and requests W01's entry 2 in one case; W00's Init calls the
    ///     function worker W02.
    /// </summary>
    private static byte[] StoryFile() {
        const string source = """
            using Ffx;
            using Ffx.Atel;
            using static Monsters.M997.M997;

            namespace Monsters.M997;

            [Monster("m997")]
            [Workers(typeof(W00), typeof(W01), typeof(W02))]
            public static partial class M997
            {
                [Var(Storage.Save, Save.StoryProgress)] public static ushort StoryProgress;
            }

            public sealed class W00 : Worker2
            {
                protected override void Init()
                {
                    W02.Run();
                    return;
                }

                protected override async void Main()
                {
                    switch (StoryProgress)
                    {
                        case Story.S00006_Znkd1300:
                            await W01.E2Async();
                            break;
                        default:
                            break;
                    }
                    return;
                }
            }

            public sealed class W01 : Worker2
            {
                protected override void Init()
                {
                    return;
                }

                protected override void Main()
                {
                    Common.WaitForever();
                    return;
                }

                [Entry(2)]
                public static async Task E2Async()
                {
                    return;
                }
            }

            public sealed class W02 : Worker0
            {
                public static void Run()
                {
                    return;
                }
            }
            """;
        SyntaxTree tree = ScriptCompiler.Parse(source, "m997.cs");
        Assert.Empty(ScriptCompiler.Check([tree]));
        byte[] block = ScriptCompiler.Compile([tree]).Block();
        byte[] file = new byte[0x40 + (block.Length + 0x3f) / 0x40 * 0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x40);
        block.CopyTo(file, 0x40);
        return file;
    }

    private static List<SyntaxTree> Parse(IEnumerable<SourceFile> files) => [.. files.Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath))];

    private static DecompiledScript Decompile(string name, ScriptFile file) => ScriptDecompiler.Decompile(Monster(name), file, overlay: NameOverlay.Empty);

    [Fact]
    public void EntriesNameWhoRequestsAndCallsThem() {
        byte[] original = StoryFile();
        ScriptFile file = ScriptFile.Parse(original);
        ScriptSource parts = Decompile("m997", file).Parts;
        WorkerSource w01 = parts.Workers[1], w02 = parts.Workers[2];
        Assert.Contains("    /// <summary>Requested by W00.Main.</summary>\n    [Entry(2)]\n    public static async Task E2Async()", w01.Text, StringComparison.Ordinal);
        Assert.Contains("    /// <summary>Called by W00.Init.</summary>\n    public static void Run()", w02.Text, StringComparison.Ordinal);
        // The comments stay out of the body, which decides what is shared.
        Assert.All(parts.Workers, w => Assert.DoesNotContain("///", w.Body, StringComparison.Ordinal));
        Assert.Equal("/// <summary>E2Async: requested by W00.Main.</summary>\n", w01.ClassNotes);
        Assert.Equal(original, ScriptCompiler.Compile([ScriptCompiler.Parse(parts.SingleFile(), "m997.cs")]).ApplyTo(file).ToArray());
        Assert.Equal(original, ScriptCompiler.Compile(Parse(SourceLayout.Files(parts))).ApplyTo(file).ToArray());
    }

    [Fact]
    public void TheScriptClassHasTheStoryIndex() {
        ScriptSource parts = Decompile("m997", ScriptFile.Parse(StoryFile())).Parts;
        Assert.StartsWith("/// <summary>\n/// The story index:", parts.ScriptClass, StringComparison.Ordinal);
        Assert.Contains("""
            /// <code>
            /// W00.Main:
            ///   Story.S00006_Znkd1300: W01.E2Async
            ///   otherwise: -
            /// </code>
            /// </summary>
            [Monster("m997")]
            """.ReplaceLineEndings("\n"), parts.ScriptClass, StringComparison.Ordinal);
    }

    [Fact]
    public void ASharedWorkersNotesGoOnItsClass() {
        byte[] original = StoryFile();
        ScriptFile file = ScriptFile.Parse(original);
        SharedLibrary shared = SharedWorkers.Build([Decompile("m997", file).Parts, Decompile("m996", file).Parts], LibraryNames.Empty);
        ScriptLibrary library = ScriptLibrary.FromTrees(Parse(shared.Files()));
        Assert.Empty(library.Check());
        foreach (ScriptSource script in shared.Scripts) {
            WorkerSource w01 = script.Workers[1];
            Assert.StartsWith("Shared", w01.Base, StringComparison.Ordinal);
            Assert.Contains($"/// <summary>E2Async: requested by W00.Main.</summary>\npublic sealed partial class W01 : {w01.Base}\n", w01.Text, StringComparison.Ordinal);
            Assert.Equal(original, ScriptCompiler.Compile(Parse(SourceLayout.Files(script)), library).ApplyTo(file).ToArray());
        }
        Assert.All(shared.Files(), f => Assert.DoesNotContain("Requested by", f.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void ASignatureIsTheCodeNotTheScript() {
        ScriptFile file = ScriptFile.Parse(StoryFile());
        ScriptSource a = Decompile("m997", file).Parts, b = Decompile("m996", file).Parts;
        Assert.All(a.Workers, w => Assert.Matches("^[0-9a-f]{16}$", w.Signature));
        Assert.Equal(a.Workers.Select(w => w.Signature), b.Workers.Select(w => w.Signature));
        Assert.Equal(3, a.Workers.Select(w => w.Signature).Distinct().Count());
    }

    [Fact]
    public void LibraryClassesTakeTheirNamesBySignature() {
        byte[] original = StoryFile();
        ScriptFile file = ScriptFile.Parse(original);
        ScriptSource a = Decompile("m997", file).Parts, b = Decompile("m996", file).Parts;
        SharedLibrary numbered = SharedWorkers.Build([a, b], LibraryNames.Empty);
        string signature = a.Workers[1].Signature!;
        LibraryNames names = LibraryNames.Parse($"""
            [classes]
            "{signature}" = "Waiter"   # W01

            [summaries]
            "{signature}" = "Waits <forever> & answers."
            """, "test.toml");
        SharedLibrary named = SharedWorkers.Build([a, b], names);
        // The named class takes its name; the others keep their numbers.
        Assert.Equal(numbered.Classes.Select(c => c.Signature == signature ? "Waiter" : c.Name), named.Classes.Select(c => c.Name));
        SourceFile waiter = named.Files().Single(f => f.RelativePath == "Library/Waiter.cs");
        Assert.Contains("/// <summary>Waits &lt;forever&gt; &amp; answers.</summary>\npublic abstract class Waiter : Worker2\n", waiter.Text, StringComparison.Ordinal);
        Assert.Contains($"// Signature {signature} ", waiter.Text, StringComparison.Ordinal);
        ScriptLibrary library = ScriptLibrary.FromTrees(Parse(named.Files()));
        Assert.Empty(library.Check());
        foreach (ScriptSource script in named.Scripts) {
            Assert.Equal("Waiter", script.Workers[1].Base);
            Assert.Equal(original, ScriptCompiler.Compile(Parse(SourceLayout.Files(script)), library).ApplyTo(file).ToArray());
        }
    }

    [Fact]
    public void TwoClassesOfTheSameCodeGetTheNameAndTheName2() {
        ScriptFile file = ScriptFile.Parse(StoryFile());
        ScriptSource a = Decompile("m997", file).Parts, b = Decompile("m996", file).Parts, c = Decompile("m995", file).Parts;
        // The same code written differently in c (as when a request makes it async in one script).
        ScriptSource Changed(ScriptSource s) => s with { Workers = [.. s.Workers.Select(w => w.Number == 1 ? w with { Body = w.Body + "    // written differently\n" } : w)] };
        string signature = a.Workers[1].Signature!;
        LibraryNames names = LibraryNames.Parse($"[classes]\n\"{signature}\" = \"Waiter\"\n", "test.toml");
        SharedLibrary shared = SharedWorkers.Build([Changed(a), b, c, Changed(Decompile("m994", file).Parts), Changed(Decompile("m993", file).Parts)], names);
        // Three users of the changed body, two of the other: the larger class gets the name.
        Assert.Equal(3, shared.Classes.Single(x => x.Name == "Waiter").Users.Count);
        Assert.Equal(2, shared.Classes.Single(x => x.Name == "Waiter_2").Users.Count);
    }

    [Theory]
    [InlineData("[classes]\n\"0123\" = \"A\"\n", "16 lower-case hex digits")]
    [InlineData("[classes]\n\"0123456789abcdef\" = \"Shared0001\"\n", "numbered library name")]
    [InlineData("[classes]\n\"0123456789abcdef\" = \"1A\"\n", "not a C# name")]
    [InlineData("[classes]\n\"0123456789abcdef\" = \"A\"\n\"fedcba9876543210\" = \"A\"\n", "given twice")]
    [InlineData("[classes]\n\"0123456789abcdef\" = \"A\"\n\"0123456789abcdef\" = \"B\"\n", "named twice")]
    [InlineData("[summaries]\n\"0123456789abcdef\" = \"x\"\n", "names no class")]
    [InlineData("\"0123456789abcdef\" = \"A\"\n", "before any section")]
    [InlineData("[workers]\n", "unknown section")]
    public void ABadLibraryNamesFileStops(string text, string message) {
        FormatException ex = Assert.Throws<FormatException>(() => LibraryNames.Parse(text, "test.toml"));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmbeddedLibraryNamesRead() {
        Assert.NotEmpty(LibraryNames.Default.Names);
        // Not the name of a type of the reference assembly (Ffx, Ffx.Atel), which the scripts also import.
        HashSet<string> types = [.. typeof(Atel.Worker).Assembly.GetExportedTypes().Select(t => t.Name)];
        Assert.DoesNotContain(LibraryNames.Default.Names.Values, types.Contains);
    }
}
