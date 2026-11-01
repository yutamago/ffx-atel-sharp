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
    ///     Phase 2's bar: every script decompiles to C# that compiles as C# against the reference
    ///     assembly without errors or warnings, and compiles back, from the source text and the
    ///     vanilla file's other sections, to the same file bytes.
    /// </summary>
    [Fact]
    public void EveryScriptRoundTripsThroughSource() {
        using VbfArchive archive = OpenOrSkip();
        List<string> paths = GameData.AllScriptPaths(archive);
        ConcurrentBag<string> failures = [];
        long sourceBytes = 0, asmLines = 0, identical = 0, diagnostics = 0;
        Stopwatch clock = Stopwatch.StartNew();
        Parallel.ForEach(paths, path => {
            byte[] original = archive.Read(path);
            try {
                ScriptFile vanilla = ScriptFile.Parse(original);
                string source = ScriptDecompiler.Decompile(path, vanilla).Source;
                Interlocked.Add(ref sourceBytes, source.Length);
                Interlocked.Add(ref asmLines, source.Split('\n').Count(l => l.StartsWith("            ", StringComparison.Ordinal)));
                SyntaxTree tree = ScriptCompiler.Parse(source, path);
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
        Assert.Empty(failures.Take(20));
        Assert.Equal(1621, identical);
        output.WriteLine($"{identical} scripts round-trip through C#: {sourceBytes / 1e6:0.0} MB of source, {asmLines} instructions in Asm, {diagnostics} C# diagnostics, {clock.Elapsed.TotalSeconds:0.0} s");
    }
}
