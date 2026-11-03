// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Text;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Compiler;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Layout;
using Ffx.Atel.Decompiler.Naming;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Cli;

/// <summary><c>atel decompile</c>, <c>atel compile</c> and <c>atel roundtrip</c>.</summary>
internal static class SourceCommands {
    public const string Usage = """
          decompile <script>... [-o <dir>] to C#: a folder per script, <dir>/<Namespace as folders>/,
                                           with a file per worker, and projects, a solution and
                                           Directory.Build.props (else one file to stdout);
                                           `all` for every script; --text-comments <lang> writes
                                           // "…" with the message after each Text[n] (comments only)
          compile <dir|file.cs> -o <out> [--library <dir>]
                                           C# back to the script file, from a script's folder (every
                                           .cs under it but obj/, bin/) or one file, with the library
                                           classes of the nearest Library/ above it (the game's file gives
                                           the other sections); --check also compiles it as C#.
                                           --debug natives|drop|error (--no-debug = drop): Debug.*
                                           calls to the loader's natives (the default; their texts go
                                           to <out>.debug.json), left out, or refused
          roundtrip [filter] [--check]     decompile and compile every script (path contains
                                           filter), compare with the game's bytes
          --no-library                     (decompile, roundtrip) keep duplicated workers in
                                           every script instead of library base classes
          --sdk <dir|nupkg>                (decompile, export) the Ffx.Atel.Sdk package to copy into
                                           <dir>/.atel/packages/ with a nuget.config naming it; else
                                           looked for beside atel, or left to your NuGet sources
          --no-sdk                         (decompile, export) projects reference .atel/Ffx.Atel.Game.dll
                                           instead of using the SDK (no analyzers)
          --names <dir>                    (decompile, roundtrip) take the hand-written names for
                                           workers, entries and variables from <dir>/events/kami0000.toml,
                                           … instead of ffx-atel-sharp's data/names/ (built in;
                                           docs/source-repo.md, "Names")
        """;

    /// <summary>How <c>decompile -o</c> writes the projects: by the SDK, with its package if found or given, or the old way.</summary>
    internal sealed record ProjectOptions(GameReference Reference, string? SdkPackage, bool RequirePackage = false) {
        public static readonly ProjectOptions Default = new(GameReference.Sdk, null);
    }

    public static int Decompile(VbfArchive archive, List<string> args, string? output, bool share, ProjectOptions? projectOptions = null, GameLanguage? textComments = null,
                                string? namesFolder = null) {
        if (namesFolder is not null && !Directory.Exists(namesFolder)) throw new ArgumentException($"--names {namesFolder}: no such folder");
        if (args.Count == 0) throw new ArgumentException("decompile <script>... [-o <dir>]");
        List<string> paths = args is ["all"] ? GameData.AllScriptPaths(archive)
            : args.Select(a => GameData.Resolve(archive, a) ?? throw new ArgumentException($"no script {a}")).ToList();
        if (output is null && paths.Count > 1) throw new ArgumentException("more than one script needs -o <dir>");
        ScriptMessages? messages = textComments is null ? null : new(archive);
        string Comment(string path, string source) =>
            messages?.Display(path, textComments!.Value) is { } texts ? TextComments.Comment(source, texts) : source;
        if (output is null) {
            Console.Out.Write(Comment(paths[0], ScriptDecompiler.Decompile(paths[0], ScriptFile.Parse(archive.Read(paths[0])), overlay: namesFolder is null ? null : NameOverlay.Load(namesFolder, paths[0])).Source));
            return 0;
        }
        List<(string Path, byte[] Bytes)> files = [.. paths.Select(p => (p, archive.Read(p)))];
        DecompiledScript[] decompiled = new DecompiledScript[files.Count];
        Parallel.For(0, files.Count, i => decompiled[i] = ScriptDecompiler.Decompile(files[i].Path, ScriptFile.Parse(files[i].Bytes), overlay: namesFolder is null ? null : NameOverlay.Load(namesFolder, files[i].Path)));
        SharedLibrary library = Share(decompiled, share);
        long[] sizes = new long[files.Count];
        Parallel.For(0, files.Count, i => {
            List<SourceFile> sources = SourceLayout.Files(library.Scripts[i]);
            sizes[i] = sources.Sum(f => (long)f.Text.Length);
            // The comments go in after the sizes, which choose the projects: the same layout either way.
            if (messages is not null) sources = [.. sources.Select(f => f with { Text = Comment(files[i].Path, f.Text) })];
            WriteFiles(output, sources);
        });
        WriteFiles(output, library.Files());
        Dictionary<string, string> projects = ProjectLayout.Assign(decompiled.Select((d, i) => (d.Names, sizes[i])));
        WriteProjects(output, projects.Values, library.Classes.Count > 0 ? [SharedLibrary.Namespace] : [], projectOptions ?? ProjectOptions.Default);
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
    ///     The project files at the root (<see cref="ProjectLayout"/>). With the SDK: <c>global.json</c> names
    ///     this build's version, and the SDK package, when found, goes to <c>.atel/packages/</c> with a
    ///     <c>nuget.config</c> naming it, so the tree builds without any other package source. Without
    ///     it: the reference assembly beside them in <c>.atel/</c>, which <c>Directory.Build.props</c> points at.
    /// </summary>
    internal static void WriteProjects(string root, IEnumerable<string> projectFolders, IReadOnlyCollection<string> libraryFolders, ProjectOptions options) {
        string tools = Path.Combine(root, ProjectLayout.ToolFolder);
        if (options.Reference == GameReference.Assembly) {
            WriteFiles(root, ProjectLayout.Files(projectFolders, libraryFolders, GameReference.Assembly, null, localFeed: false));
            string game = typeof(Atel.Worker).Assembly.Location;
            Directory.CreateDirectory(tools);
            File.Copy(game, Path.Combine(tools, ProjectLayout.GameAssembly), overwrite: true);
            string docs = Path.ChangeExtension(game, ".xml");
            if (File.Exists(docs)) File.Copy(docs, Path.Combine(tools, Path.GetFileName(docs)), overwrite: true);
            return;
        }
        string version = SdkVersion();
        string? package = FindSdkPackage(options.SdkPackage, version);
        if (package is null && (options.SdkPackage is not null || options.RequirePackage)) {
            throw new ArgumentException($"no {ProjectLayout.SdkPackage}.{version}.nupkg {(options.SdkPackage is null ? "beside atel" : "in " + options.SdkPackage)}: " +
                                        "dotnet pack src/Ffx.Atel.Sdk, then --sdk <dir>; or --no-sdk");
        }
        WriteFiles(root, ProjectLayout.Files(projectFolders, libraryFolders, GameReference.Sdk, version, localFeed: package is not null));
        if (package is not null) {
            string feed = Path.Combine(tools, ProjectLayout.PackageFolder);
            if (Directory.Exists(feed)) Directory.Delete(feed, recursive: true);
            Directory.CreateDirectory(feed);
            // NuGet's file name, whatever the case of the copy found (the package cache's is lower case).
            File.Copy(package, Path.Combine(feed, $"{ProjectLayout.SdkPackage}.{version}.nupkg"));
        }
        else {
            Console.Error.WriteLine($"the projects use {ProjectLayout.SdkPackage} {version}: a NuGet source must have it (docs/sdk.md), or decompile again with --sdk <dir> or --no-sdk");
        }
    }

    /// <summary>This build's version without the commit (<c>0.1.0-alpha</c>): the SDK package's version.</summary>
    internal static string SdkVersion() {
        string version = typeof(SourceCommands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    /// <summary>
    ///     The SDK package of <paramref name="version"/>: <paramref name="given"/> (the file, or a folder
    ///     holding it), else beside atel: in the package itself (atel runs from <c>tools/atel/</c> of the
    ///     extracted package, which NuGet keeps next to its <c>.nupkg</c>), or in this repo's
    ///     <c>artifacts/package/&lt;configuration&gt;/</c> (<c>dotnet pack src/Ffx.Atel.Sdk</c>).
    /// </summary>
    internal static string? FindSdkPackage(string? given, string version) {
        string name = $"{ProjectLayout.SdkPackage}.{version}.nupkg";
        if (given is not null) {
            if (File.Exists(given)) return given;
            return Directory.Exists(given) ? FindIn(given) : null;
        }
        string here = AppContext.BaseDirectory;
        string configuration = Path.GetFileName(Path.TrimEndingDirectorySeparator(here));
        foreach (string dir in new[] { here, Path.Combine(here, "..", ".."), Path.Combine(here, "..", "..", "..", "package", configuration) }) {
            if (Directory.Exists(dir) && FindIn(dir) is string found) return found;
        }
        return null;

        string? FindIn(string dir) => Directory.EnumerateFiles(dir, "*.nupkg").FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Writes source files under a root folder.</summary>
    internal static void WriteFiles(string root, IEnumerable<SourceFile> files) {
        foreach (SourceFile f in files) {
            string path = Path.Combine(root, f.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, f.Text);
        }
    }

    public static int Compile(VbfArchive archive, List<string> args, string? output, bool check, string? libraryFolder, CompileOptions options) {
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
        CompiledScript script = ScriptCompiler.Compile(trees, library, options);
        foreach (string w in script.Warnings) Console.Error.WriteLine($"warning: {w}");
        string path = GameData.Find(archive, script.Category, script.Name) ?? throw new ArgumentException($"the game has no {script.Category} {script.Name}");
        ScriptFile vanilla = ScriptFile.Parse(archive.Read(path));
        File.WriteAllBytes(output, script.ApplyTo(vanilla).ToArray());
        // The texts of Debug.Print, for the loader (docs/formats/debug.md).
        if (script.DebugStrings.Count > 0) {
            File.WriteAllText(output + ".debug.json", System.Text.Json.JsonSerializer.Serialize(script.DebugStrings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }

    public static int RoundTrip(VbfArchive archive, List<string> args, string? output, bool check, bool share, string? namesFolder = null) {
        if (namesFolder is not null && !Directory.Exists(namesFolder)) throw new ArgumentException($"--names {namesFolder}: no such folder");
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
                decompiled[i] = ScriptDecompiler.Decompile(files[i].Path, ScriptFile.Parse(files[i].Bytes), overlay: namesFolder is null ? null : NameOverlay.Load(namesFolder, files[i].Path));
                totals.Add(decompiled[i]!.Stats);
            }
            catch (Exception ex) when (ex is ScriptFormatException or InvalidOperationException or FormatException) {
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
