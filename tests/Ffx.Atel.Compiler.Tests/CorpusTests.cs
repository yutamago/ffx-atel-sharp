// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Decompiler;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>Tests over every shipped script. Need <c>FFX_GAME_DIR</c>; skipped without it.</summary>
public sealed class CorpusTests(ITestOutputHelper output) {
    private static VbfArchive OpenOrSkip() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        return GameData.OpenArchive(dir!);
    }

    /// <summary>
    ///     Phase 3's bar: every script decompiles to C# statements (and what stays <c>Asm</c>) that
    ///     compile as C# against the reference assembly without errors or warnings, and compile back,
    ///     from the source text and the vanilla file's other sections, to the same file bytes.
    /// </summary>
    [Fact]
    public void EveryScriptRoundTripsThroughSource() {
        using VbfArchive archive = OpenOrSkip();
        // Largest first, one at a time: the biggest scripts take the longest to check.
        List<(string Path, byte[] Bytes)> files = [.. GameData.AllScriptPaths(archive).Select(p => (p, archive.Read(p))).OrderByDescending(f => f.Item2.Length)];
        ConcurrentBag<string> failures = [];
        long sourceBytes = 0, identical = 0, diagnostics = 0;
        DecompileTotals totals = new();
        Stopwatch clock = Stopwatch.StartNew();
        Parallel.ForEach(Partitioner.Create(files, EnumerablePartitionerOptions.NoBuffering), f => {
            (string path, byte[] original) = f;
            try {
                ScriptFile vanilla = ScriptFile.Parse(original);
                DecompiledScript d = ScriptDecompiler.Decompile(path, vanilla);
                Interlocked.Add(ref sourceBytes, d.Source.Length);
                totals.Add(d.Stats);
                SyntaxTree tree = ScriptCompiler.Parse(d.Source, path);
                IReadOnlyList<Diagnostic> found = ScriptCompiler.Check([tree]);
                Interlocked.Add(ref diagnostics, found.Count);
                if (found.Count > 0) failures.Add($"{path}: {found[0]}");
                byte[] rebuilt = ScriptCompiler.Compile([tree]).ApplyTo(vanilla).ToArray();
                if (rebuilt.AsSpan().SequenceEqual(original)) Interlocked.Increment(ref identical);
                else failures.Add($"{path}: file differs");
            }
            catch (Exception ex) when (ex is ScriptFormatException or CompileException or AssemblyException) {
                failures.Add($"{path}: {ex.Message}");
            }
        });
        output.WriteLine($"{identical} scripts round-trip through C#: {sourceBytes / 1e6:0.0} MB of source, {diagnostics} C# diagnostics, {clock.Elapsed.TotalSeconds:0.0} s");
        output.WriteLine(totals.ToString());
        Assert.Empty(failures.Take(20));
        Assert.Equal(1621, identical);
    }
}
