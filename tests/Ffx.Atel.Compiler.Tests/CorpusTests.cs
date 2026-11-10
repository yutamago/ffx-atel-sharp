// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Layout;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>Tests over every shipped script. Need <c>FFX_GAME_DIR</c>; skipped without it.</summary>
public sealed class CorpusTests(ITestOutputHelper output) {
    private static VbfArchive OpenOrSkip() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        return GameData.OpenArchiveExclusive(dir!);
    }

    /// <summary>
    ///     Phase 4's bar: every script decompiles to structured C# without any <c>Asm</c>, which compiles
    ///     as C# against the reference assembly without errors or warnings (but ATL0001 and ATL2006), and compiles back, from the
    ///     source text and the vanilla file's other sections, to the same file bytes. Since Phase 6 the
    ///     source is the script's folder: the script class, a file per worker and the data, with the
    ///     workers that are exact copies deriving from library classes.
    /// </summary>
    /// <summary>Analyzer warnings the vanilla code legitimately gives; they inform new code.</summary>
    private static bool VanillaWarning(Diagnostic d) => d.Severity == DiagnosticSeverity.Warning && d.Id is "ATL0001" or "ATL2006";

    [Fact]
    public void EveryScriptRoundTripsThroughSource() {
        using VbfArchive archive = OpenOrSkip();
        // Largest first, one at a time: the biggest scripts take the longest to check.
        List<(string Path, byte[] Bytes)> files = [.. GameData.AllScriptPaths(archive).Select(p => (p, archive.Read(p))).OrderByDescending(f => f.Item2.Length)];
        ConcurrentBag<string> failures = [];
        long sourceBytes = 0, fileCount = 0, identical = 0, diagnostics = 0, registerWarnings = 0;
        DecompileTotals totals = new();
        Stopwatch clock = Stopwatch.StartNew();
        // Every script first, then the library of the workers they share (Phase 6), then each script against it.
        DecompiledScript[] decompiled = new DecompiledScript[files.Count];
        Parallel.For(0, files.Count, i => {
            decompiled[i] = ScriptDecompiler.Decompile(files[i].Path, ScriptFile.Parse(files[i].Bytes));
            totals.Add(decompiled[i].Stats);
        });
        SharedLibrary shared = SharedWorkers.Build([.. decompiled.Select(d => d.Parts)]);
        // Helper names by signature (docs/language.md, "Repeated code"): every one names a helper the decompiler wrote.
        HashSet<string> helpers = [.. totals.HelperSignatures];
        foreach (string signature in InlineNames.Default.Signatures.Where(s => !helpers.Contains(s))) {
            failures.Add($"helpers: {signature} in data/inline-names.toml names no helper");
        }
        // Library names by signature (docs/source-repo.md, "Library names"): a class with a signature has users
        // of that one signature (a class whose body names siblings may have none), and every name in
        // data/library-names.toml still names a class.
        Dictionary<string, string?> signatures = decompiled.SelectMany(d => d.Parts.Workers.Select(w => (Key: $"{d.Parts.Names.Namespace}.{w.Name}", w.Signature)))
            .ToDictionary(x => x.Key, x => x.Signature, StringComparer.Ordinal);
        foreach (SharedWorker c in shared.Classes.Where(c => c.Signature is not null && c.Users.Any(u => signatures[u] != c.Signature))) failures.Add($"library: the users of {c.Name} differ in signature");
        foreach (string signature in LibraryNames.Default.Names.Keys.Where(s => !shared.Classes.Any(c => c.Signature == s))) {
            failures.Add($"library: {signature} in data/library-names.toml names no class");
        }
        // Dead block names by signature (docs/research/dead-code.md, "Names"): every one names a block.
        HashSet<string> cuts = [.. totals.CutSignatures];
        foreach (string signature in CutNames.Default.Names.Keys.Where(s => !cuts.Contains(s))) {
            failures.Add($"cuts: {signature} in data/cut-names.toml names no dead block");
        }
        List<SourceFile> libraryFiles = shared.Files();
        ScriptLibrary library = ScriptLibrary.FromTrees(libraryFiles.Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath)));
        IReadOnlyList<Diagnostic> libraryAll = library.Check();
        List<Diagnostic> libraryDiagnostics = [.. libraryAll.Where(x => !VanillaWarning(x))];
        registerWarnings += libraryAll.Count - libraryDiagnostics.Count;
        diagnostics += libraryDiagnostics.Count;
        foreach (Diagnostic d in libraryDiagnostics.Take(5)) failures.Add($"library: {d}");
        sourceBytes += libraryFiles.Sum(f => (long)f.Text.Length);
        fileCount += libraryFiles.Count;
        Parallel.ForEach(Partitioner.Create(Enumerable.Range(0, files.Count).ToList(), EnumerablePartitionerOptions.NoBuffering), i => {
            (string path, byte[] original) = files[i];
            try {
                ScriptFile vanilla = ScriptFile.Parse(original);
                // The folder layout: a file per worker, big workers in parts, the data in its own file.
                List<SourceFile> sources = SourceLayout.Files(shared.Scripts[i]);
                Interlocked.Add(ref sourceBytes, sources.Sum(s => (long)s.Text.Length));
                Interlocked.Add(ref fileCount, sources.Count);
                List<SyntaxTree> trees = [.. sources.Select(s => ScriptCompiler.Parse(s.Text, s.RelativePath))];
                // ATL0001 (a register across a wait that another entry writes) and ATL2006 (a request
                // at a reserved priority) are warnings the original code gives (docs/analyzers.md):
                // counted, not failures.
                IReadOnlyList<Diagnostic> all = ScriptCompiler.Check(trees, library);
                List<Diagnostic> found = [.. all.Where(x => !VanillaWarning(x))];
                Interlocked.Add(ref registerWarnings, all.Count - found.Count);
                Interlocked.Add(ref diagnostics, found.Count);
                if (found.Count > 0) failures.Add($"{path}: {found[0]}");
                byte[] rebuilt = ScriptCompiler.Compile(trees, library).ApplyTo(vanilla).ToArray();
                if (rebuilt.AsSpan().SequenceEqual(original)) Interlocked.Increment(ref identical);
                else failures.Add($"{path}: file differs");
            }
            catch (Exception ex) when (ex is ScriptFormatException or CompileException or AssemblyException) {
                failures.Add($"{path}: {ex.Message}");
            }
        });
        output.WriteLine($"{identical} scripts round-trip through C#: {sourceBytes / 1e6:0.0} MB of source in {fileCount} files, {diagnostics} C# diagnostics (and {registerWarnings} ATL0001/ATL2006 warnings the original code gives), {clock.Elapsed.TotalSeconds:0.0} s");
        output.WriteLine($"{shared.SharedWorkers} workers derive from {shared.Classes.Count} library classes, {shared.SavedChars / 1e6:0.0} MB saved");
        output.WriteLine(totals.ToString());
        output.WriteLine(totals.FlowSummary());
        output.WriteLine(totals.NameSummary());
        output.WriteLine(totals.InlineSummary());
        Assert.Empty(failures.Take(20));
        Assert.Equal(1621, identical);
        Assert.Equal(0, totals.AsmInstructions);
        Assert.Equal(0, totals.FlatWorkers);
        Assert.NotEmpty(shared.Classes);
    }
}
