// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Compiler;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Layout;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Analyzers.Tests;

/// <summary>The analyzers over every decompiled vanilla script. Needs <c>FFX_GAME_DIR</c>; skipped without it.</summary>
public sealed class CorpusTests(ITestOutputHelper output) {
    /// <summary>
    ///     Vanilla source is Atel: every script, in the folder layout <c>atel decompile</c> writes, and the
    ///     library of shared workers, on which no ATL rule reports an error. What the analyzers report at
    ///     all (warnings and hints too) is written to the test output.
    /// </summary>
    [Fact]
    public void VanillaSourceHasNoAtlErrors() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        using VbfArchive archive = GameData.OpenArchive(dir!);
        List<(string Path, byte[] Bytes)> files = [.. GameData.AllScriptPaths(archive).Select(p => (p, archive.Read(p))).OrderByDescending(f => f.Item2.Length)];
        Stopwatch clock = Stopwatch.StartNew();
        DecompiledScript[] decompiled = new DecompiledScript[files.Count];
        Parallel.For(0, files.Count, i => decompiled[i] = ScriptDecompiler.Decompile(files[i].Path, ScriptFile.Parse(files[i].Bytes)));
        SharedLibrary shared = SharedWorkers.Build([.. decompiled.Select(d => d.Parts)]);
        ScriptLibrary library = ScriptLibrary.FromTrees(shared.Files().Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath)));

        ConcurrentDictionary<string, int> counts = new();
        ConcurrentBag<string> examples = [];
        ConcurrentBag<string> errors = [];
        void Tally(IEnumerable<Diagnostic> found) {
            foreach (Diagnostic x in found.Where(x => x.Id.StartsWith("ATL", StringComparison.Ordinal) || x.Id == "AD0001")) {
                string key = $"{x.Id} {x.Severity}";
                if (counts.AddOrUpdate(key, 1, (_, n) => n + 1) <= 3) examples.Add($"{key}: {x}");
                if (x.Severity == DiagnosticSeverity.Error || x.Id == "AD0001") errors.Add(x.ToString());
            }
        }
        Microsoft.CodeAnalysis.CSharp.CSharpCompilation libraryCompilation = Harness.Compilation(library.Trees);
        MetadataReference libraryReference = libraryCompilation.ToMetadataReference();
        Tally(Harness.Analyze(libraryCompilation));
        Parallel.ForEach(Partitioner.Create(Enumerable.Range(0, files.Count).ToList(), EnumerablePartitionerOptions.NoBuffering), i => {
            List<SyntaxTree> trees = [.. SourceLayout.Files(shared.Scripts[i]).Select(s => ScriptCompiler.Parse(s.Text, s.RelativePath))];
            Tally(Harness.Analyze(Harness.Compilation(trees, libraryReference)));
        });
        output.WriteLine($"{files.Count} scripts and the library ({library.Trees.Count} files) analyzed in {clock.Elapsed.TotalSeconds:0.0} s");
        foreach ((string key, int n) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal)) output.WriteLine($"{key}: {n}");
        foreach (string e in examples.Order(StringComparer.Ordinal)) output.WriteLine(e);
        Assert.Empty(errors.Order(StringComparer.Ordinal).Take(20));
    }
}
