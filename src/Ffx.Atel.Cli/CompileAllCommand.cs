// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Compiler;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Cli;

/// <summary>
///     <c>atel compile-all</c>: compiles every script of a source tree (what <c>atel decompile all -o</c>
///     writes, or the private source repo with hand edits) and compares each with the game's file.
/// </summary>
internal static class CompileAllCommand {
    public const string Usage = """
          compile-all <dir> [filter] [--check]
                                           compile every script folder under <dir> (a decompiled tree,
                                           with its Library/) and compare with the game's files; lists
                                           the scripts that differ and those the game has but <dir> lacks;
                                           filter: folders whose path contains it; --check also compiles
                                           them as C#
        """;

    /// <summary>What <see cref="Run"/> found.</summary>
    internal sealed record Result(int Identical, int Scripts, int Problems) {
        public bool Ok => Problems == 0 && Identical == Scripts;
    }

    public static int Run(VbfArchive archive, List<string> args, bool check) {
        if (args.Count is 0 or > 2) throw new ArgumentException("compile-all <dir> [filter] [--check]");
        return Compare(archive, args[0], args.Count > 1 ? args[1] : "", check).Ok ? 0 : 1;
    }

    /// <summary>Compiles the scripts under <paramref name="root"/> and prints what differs from the game.</summary>
    internal static Result Compare(VbfArchive archive, string root, string filter, bool check) {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new ArgumentException($"no folder {root}");
        Stopwatch clock = Stopwatch.StartNew();
        string libraryFolder = Path.Combine(root, "Library");
        ScriptLibrary library = Directory.Exists(libraryFolder) ? ScriptLibrary.FromFolder(libraryFolder) : ScriptLibrary.Empty;
        List<string> folders = [.. ScriptFolders(root).Where(f => Relative(root, f).Contains(filter, StringComparison.Ordinal))];
        // Largest first: the biggest scripts take the longest.
        folders = [.. folders.OrderByDescending(f => ScriptCompiler.SourceFiles(f).Sum(p => new FileInfo(p).Length))];

        ConcurrentBag<string> problems = [];
        ConcurrentBag<string> differ = [];
        ConcurrentDictionary<string, string> compiled = new(StringComparer.Ordinal);
        long errors = 0, warnings = 0;
        int identical = 0;
        if (check && library.Trees.Count > 0) {
            IReadOnlyList<Diagnostic> diagnostics = library.Check();
            errors += diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            warnings += diagnostics.Count(d => d.Severity != DiagnosticSeverity.Error);
            foreach (Diagnostic d in diagnostics.Take(5)) problems.Add($"Library: C#: {d}");
        }
        Parallel.ForEach(Partitioner.Create(folders, EnumerablePartitionerOptions.NoBuffering), folder => {
            string name = Relative(root, folder);
            try {
                List<SyntaxTree> trees = ScriptCompiler.ParseFolder(folder);
                if (check) {
                    IReadOnlyList<Diagnostic> diagnostics = ScriptCompiler.Check(trees, library);
                    int e = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
                    Interlocked.Add(ref errors, e);
                    Interlocked.Add(ref warnings, diagnostics.Count - e);
                    if (diagnostics.FirstOrDefault() is Diagnostic first) problems.Add($"{name}: C#: {first}");
                }
                CompiledScript script = ScriptCompiler.Compile(trees, library);
                string? path = GameData.Find(archive, script.Category, script.Name);
                if (path is null) {
                    problems.Add($"{name}: the game has no {script.Category.ToString().ToLowerInvariant()} {script.Name}");
                    return;
                }
                if (!compiled.TryAdd(path, name)) {
                    problems.Add($"{name}: {path} is also compiled from {compiled[path]}");
                    return;
                }
                byte[] original = archive.Read(path);
                byte[] rebuilt = script.ApplyTo(ScriptFile.Parse(original)).ToArray();
                if (rebuilt.AsSpan().SequenceEqual(original)) Interlocked.Increment(ref identical);
                else differ.Add($"{name}: differs from {path}");
            }
            catch (Exception ex) when (ex is ScriptFormatException or CompileException or AssemblyException or IOException) {
                problems.Add($"{name}: {ex.Message}");
            }
        });

        List<string> missing = filter.Length > 0 ? []
            : [.. GameData.AllScriptPaths(archive).Where(p => !compiled.ContainsKey(p))];
        foreach (string d in differ.Order(StringComparer.Ordinal)) Console.Error.WriteLine(d);
        foreach (string p in problems.Order(StringComparer.Ordinal)) Console.Error.WriteLine(p);
        foreach (string m in missing.Take(50)) Console.Error.WriteLine($"{m}: no source");
        if (missing.Count > 50) Console.Error.WriteLine($"… and {missing.Count - 50} more scripts without source");

        int scripts = filter.Length > 0 ? folders.Count : GameData.AllScriptPaths(archive).Count;
        string checks = check ? string.Create(CultureInfo.InvariantCulture, $"; {errors} C# errors, {warnings} warnings") : "";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{identical} / {scripts} scripts compile to the game's bytes; {differ.Count} differ, {missing.Count} without source, {problems.Count} other problems{checks}; {clock.Elapsed.TotalSeconds:0.0} s"));
        return new Result(identical, scripts, differ.Count + missing.Count + problems.Count + (int)errors);
    }

    /// <summary>
    ///     The script folders under a root: the top-most folders that hold <c>.cs</c> files themselves
    ///     (a script's folder and anything under it is one script), leaving out <c>Library/</c>,
    ///     <c>obj/</c>, <c>bin/</c> and dot folders (<c>.git</c>, <c>.atel</c>).
    /// </summary>
    internal static List<string> ScriptFolders(string root) {
        List<string> result = [];
        Visit(root, top: true);
        result.Sort(StringComparer.Ordinal);
        return result;

        void Visit(string dir, bool top) {
            if (!top && Directory.EnumerateFiles(dir, "*.cs").Any()) {
                result.Add(dir);
                return;
            }
            foreach (string sub in Directory.EnumerateDirectories(dir)) {
                string name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name is "obj" or "bin" || (top && name == "Library")) continue;
                Visit(sub, top: false);
            }
        }
    }

    private static string Relative(string root, string folder) => Path.GetRelativePath(root, folder).Replace('\\', '/');
}
