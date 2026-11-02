// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Compiler;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Layout;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Cli;

/// <summary><c>atel decompile</c>, <c>atel compile</c> and <c>atel roundtrip</c>.</summary>
internal static class SourceCommands {
    public const string Usage = """
          decompile <script>... [-o <dir>] to C#: a folder per script, <dir>/<Namespace as folders>/,
                                           with a file per worker, and projects, a solution and
                                           Directory.Build.props (else one file to stdout);
                                           `all` for every script
          compile <dir|file.cs> -o <out> [--library <dir>]
                                           C# back to the script file, from a script's folder (every
                                           .cs under it but obj/, bin/) or one file, with the library
                                           classes of the nearest Library/ above it (the game's file gives
                                           the other sections); --check also compiles it as C#
          roundtrip [filter] [--check]     decompile and compile every script (path contains
                                           filter), compare with the game's bytes
          --no-library                     (decompile, roundtrip) keep duplicated workers in
                                           every script instead of library base classes
        """;

    public static int Decompile(VbfArchive archive, List<string> args, string? output, bool share) {
        if (args.Count == 0) throw new ArgumentException("decompile <script>... [-o <dir>]");
        List<string> paths = args is ["all"] ? GameData.AllScriptPaths(archive)
            : args.Select(a => GameData.Resolve(archive, a) ?? throw new ArgumentException($"no script {a}")).ToList();
        if (output is null && paths.Count > 1) throw new ArgumentException("more than one script needs -o <dir>");
        if (output is null) {
            Console.Out.Write(ScriptDecompiler.Decompile(paths[0], ScriptFile.Parse(archive.Read(paths[0]))).Source);
            return 0;
        }
        List<(string Path, byte[] Bytes)> files = [.. paths.Select(p => (p, archive.Read(p)))];
        DecompiledScript[] decompiled = new DecompiledScript[files.Count];
        Parallel.For(0, files.Count, i => decompiled[i] = ScriptDecompiler.Decompile(files[i].Path, ScriptFile.Parse(files[i].Bytes)));
        SharedLibrary library = Share(decompiled, share);
        long[] sizes = new long[files.Count];
        Parallel.For(0, files.Count, i => {
            List<SourceFile> sources = SourceLayout.Files(library.Scripts[i]);
            sizes[i] = sources.Sum(f => (long)f.Text.Length);
            WriteFiles(output, sources);
        });
        WriteFiles(output, library.Files());
        Dictionary<string, string> projects = ProjectLayout.Assign(decompiled.Select((d, i) => (d.Names, sizes[i])));
        WriteProjects(output, projects.Values, library.Classes.Count > 0 ? [SharedLibrary.Namespace] : []);
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{decompiled.Length} scripts in {projects.Values.Distinct().Count()} projects; {LibrarySummary(library)}; dotnet build {Path.Combine(output, ProjectLayout.Solution)}"));
        return 0;
    }

    /// <summary>The scripts with their duplicated workers in the library, or as they are without <paramref name="share"/>.</summary>
    private static SharedLibrary Share(IReadOnlyList<DecompiledScript> scripts, bool share) =>
        share ? SharedWorkers.Build([.. scripts.Select(d => d.Parts)]) : new SharedLibrary([.. scripts.Select(d => d.Parts)], []);

    private static string LibrarySummary(SharedLibrary library) => string.Create(CultureInfo.InvariantCulture,
        $"{library.SharedWorkers} workers derive from {library.Classes.Count} library classes, {library.SavedChars / 1e6:0.0} MB of source saved");

    /// <summary>
    ///     The project files at the root (<see cref="ProjectLayout"/>), and the reference assembly beside
    ///     them in <c>.atel/</c>, which <c>Directory.Build.props</c> points at.
    /// </summary>
    internal static void WriteProjects(string root, IEnumerable<string> projectFolders, IReadOnlyCollection<string> libraryFolders) {
        WriteFiles(root, ProjectLayout.Files(projectFolders, libraryFolders));
        string game = typeof(Atel.Worker).Assembly.Location;
        string tools = Path.Combine(root, ProjectLayout.ToolFolder);
        Directory.CreateDirectory(tools);
        File.Copy(game, Path.Combine(tools, ProjectLayout.GameAssembly), overwrite: true);
        string docs = Path.ChangeExtension(game, ".xml");
        if (File.Exists(docs)) File.Copy(docs, Path.Combine(tools, Path.GetFileName(docs)), overwrite: true);
    }

    /// <summary>Writes source files under a root folder.</summary>
    internal static void WriteFiles(string root, IEnumerable<SourceFile> files) {
        foreach (SourceFile f in files) {
            string path = Path.Combine(root, f.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, f.Text);
        }
    }

    public static int Compile(VbfArchive archive, List<string> args, string? output, bool check, string? libraryFolder) {
        if (args.Count != 1 || output is null) throw new ArgumentException("compile <dir|file.cs> -o <out>");
        string input = args[0];
        List<SyntaxTree> trees = Directory.Exists(input) ? ScriptCompiler.ParseFolder(input) : [ScriptCompiler.Parse(File.ReadAllText(input), input)];
        ScriptLibrary library = libraryFolder is not null ? ScriptLibrary.FromFolder(libraryFolder)
            : ScriptLibrary.Find(Directory.Exists(input) ? input : Path.GetDirectoryName(Path.GetFullPath(input))!);
        if (check) {
            IReadOnlyList<Diagnostic> diagnostics = ScriptCompiler.Check(trees, library);
            foreach (Diagnostic d in diagnostics) Console.Error.WriteLine(d.ToString());
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) return 1;
        }
        CompiledScript script = ScriptCompiler.Compile(trees, library);
        string path = GameData.Find(archive, script.Category, script.Name) ?? throw new ArgumentException($"the game has no {script.Category} {script.Name}");
        ScriptFile vanilla = ScriptFile.Parse(archive.Read(path));
        File.WriteAllBytes(output, script.ApplyTo(vanilla).ToArray());
        return 0;
    }

    public static int RoundTrip(VbfArchive archive, List<string> args, string? output, bool check, bool share) {
        string filter = args.Count > 0 ? args[0] : "";
        List<string> paths = GameData.AllScriptPaths(archive).Where(p => p.Contains(filter, StringComparison.Ordinal)).ToList();
        ConcurrentBag<string> failures = [];
        long sourceBytes = 0, fileCount = 0, errors = 0, warnings = 0, identical = 0;
        DecompileTotals totals = new();
        Stopwatch clock = Stopwatch.StartNew();
        // Largest first, one at a time: the biggest scripts take the longest to check.
        List<(string Path, byte[] Bytes)> files = [.. paths.Select(p => (p, archive.Read(p))).OrderByDescending(f => f.Item2.Length)];

        // First every script, then the library of the workers they share, then each script against it.
        DecompiledScript?[] decompiled = new DecompiledScript?[files.Count];
        Parallel.For(0, files.Count, i => {
            try {
                decompiled[i] = ScriptDecompiler.Decompile(files[i].Path, ScriptFile.Parse(files[i].Bytes));
                totals.Add(decompiled[i]!.Stats);
            }
            catch (Exception ex) when (ex is ScriptFormatException or InvalidOperationException) {
                failures.Add($"{files[i].Path}: {ex.Message}");
            }
        });
        List<int> ok = [.. Enumerable.Range(0, files.Count).Where(i => decompiled[i] is not null)];
        SharedLibrary shared = Share([.. ok.Select(i => decompiled[i]!)], share);
        List<SourceFile> libraryFiles = shared.Files();
        ScriptLibrary library = ScriptLibrary.FromTrees(libraryFiles.Select(f => ScriptCompiler.Parse(f.Text, f.RelativePath)));
        sourceBytes += libraryFiles.Sum(f => (long)f.Text.Length);
        fileCount += libraryFiles.Count;
        if (output is not null) WriteFiles(output, libraryFiles);
        if (check) {
            IReadOnlyList<Diagnostic> diagnostics = library.Check();
            errors += diagnostics.Count(x => x.Severity == DiagnosticSeverity.Error);
            warnings += diagnostics.Count(x => x.Severity != DiagnosticSeverity.Error);
            foreach (Diagnostic d in diagnostics.Take(5)) failures.Add($"library: C#: {d}");
        }
        Parallel.ForEach(Partitioner.Create(Enumerable.Range(0, ok.Count).ToList(), EnumerablePartitionerOptions.NoBuffering), k => {
            (string path, byte[] original) = files[ok[k]];
            try {
                ScriptFile vanilla = ScriptFile.Parse(original);
                List<SourceFile> sources = SourceLayout.Files(shared.Scripts[k]);
                Interlocked.Add(ref sourceBytes, sources.Sum(s => (long)s.Text.Length));
                Interlocked.Add(ref fileCount, sources.Count);
                if (output is not null) WriteFiles(output, sources);
                List<SyntaxTree> trees = [.. sources.Select(s => ScriptCompiler.Parse(s.Text, s.RelativePath))];
                if (check) {
                    IReadOnlyList<Diagnostic> diagnostics = ScriptCompiler.Check(trees, library);
                    int e = diagnostics.Count(x => x.Severity == DiagnosticSeverity.Error);
                    Interlocked.Add(ref errors, e);
                    Interlocked.Add(ref warnings, diagnostics.Count - e);
                    if (diagnostics.FirstOrDefault() is Diagnostic first) failures.Add($"{path}: C#: {first}");
                }
                byte[] rebuilt = ScriptCompiler.Compile(trees, library).ApplyTo(vanilla).ToArray();
                if (rebuilt.AsSpan().SequenceEqual(original)) Interlocked.Increment(ref identical);
                else failures.Add($"{path}: differs");
            }
            catch (Exception ex) when (ex is ScriptFormatException or CompileException or AssemblyException) {
                failures.Add($"{path}: {ex.Message}");
            }
        });
        foreach (string f in failures.Order(StringComparer.Ordinal).Take(50)) Console.Error.WriteLine(f);
        string checks = check ? string.Create(CultureInfo.InvariantCulture, $"{errors} C# errors, {warnings} warnings; ") : "";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{identical} / {paths.Count} scripts round-trip byte for byte; {sourceBytes / 1e6:0.0} MB of source in {fileCount} files; {checks}{clock.Elapsed.TotalSeconds:0.0} s"));
        Console.WriteLine(LibrarySummary(shared));
        Console.WriteLine(totals.ToString());
        Console.WriteLine(totals.FlowSummary());
        Console.WriteLine(totals.NameSummary());
        Console.WriteLine("constructs dropped: " + string.Join(", ", ScriptDecompiler.DroppedReasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
        return identical == paths.Count && errors == 0 ? 0 : 1;
    }
}
