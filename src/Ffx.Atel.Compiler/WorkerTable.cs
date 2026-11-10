// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

/// <summary>
///     The workers code can name, by name: each worker of the script, and each library class exactly one of them
///     derives from (directly or through other library classes), as another name of that worker (docs/plan.md,
///     Phase 6, "Bodies that name a sibling"; docs/language.md, "Library classes"). A shared body calling a sibling
///     writes <c>MoveTalkDefaults.Run()</c>; in a script whose one worker deriving from <c>MoveTalkDefaults</c> is
///     <c>W87</c>, that is <c>W87.Run()</c>. A library class's name that is also a worker's names the worker.
/// </summary>
internal sealed class WorkerTable : Dictionary<string, int> {
    public WorkerTable() : base(StringComparer.Ordinal) {
    }

    /// <summary>The script's workers that derive from each library class: from 0 (none) up.</summary>
    public Dictionary<string, List<string>> Deriving { get; } = new(StringComparer.Ordinal);

    /// <summary>For a library class's name that names no worker here (none or several derive from it), why; else null.</summary>
    public string? WhyNot(string name) =>
        ContainsKey(name) || !Deriving.TryGetValue(name, out List<string>? users) ? null
        : users.Count == 0 ? $"{name} is a library class no worker of this script derives from, so it names no worker here"
        : $"{name} is a library class {users.Count} workers of this script derive from ({string.Join(", ", users)}): name the worker";

    /// <summary>
    ///     The table of <paramref name="workers"/> (name → number) with the library aliases: each worker's
    ///     <paramref name="parts"/> give its base class, a library class's its base in turn.
    /// </summary>
    public static WorkerTable Of(IReadOnlyDictionary<string, int> workers, Func<string, IEnumerable<ClassDeclarationSyntax>> parts, ScriptLibrary library) {
        WorkerTable table = new();
        foreach ((string name, int w) in workers) table[name] = w;
        foreach (string cls in library.Classes.Select(g => g.Key)) table.Deriving[cls] = [];
        foreach ((string worker, int w) in workers) {
            HashSet<string> seen = new(StringComparer.Ordinal);
            for (List<ClassDeclarationSyntax> level = [.. parts(worker)]; level.Count > 0;) {
                BaseTypeSyntax? b = level.Select(p => p.BaseList?.Types.FirstOrDefault()).LastOrDefault(x => x is not null);
                string cls = b is null ? "" : ScriptCompiler.SimpleNameOf(b.Type);
                if (b is null || !library.Classes.Contains(cls) || !seen.Add(cls)) break;
                table.Deriving[cls].Add(worker);
                level = [.. library.Classes[cls]];
            }
        }
        foreach ((string cls, List<string> users) in table.Deriving) {
            if (users.Count != 1 || workers.ContainsKey(cls)) continue;
            table[cls] = workers[users[0]];
            table.Aliases[cls] = users[0];
        }
        return table;
    }

    /// <summary>The library classes that name a worker here, and that worker's own name.</summary>
    public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);

    /// <summary>The worker a name stands for: itself, or the one worker deriving from the library class it names.</summary>
    public string Real(string name) => Aliases.GetValueOrDefault(name) ?? name;
}
