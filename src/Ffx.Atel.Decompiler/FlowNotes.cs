// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Decompiler.Layout;
using Ffx.Atel.Decompiler.Naming;

namespace Ffx.Atel.Decompiler;

/// <summary>
///     Doc comments that help a reader follow a script, read off its code (docs/source-repo.md, "Notes"):
///     <list type="bullet">
///         <item>on each entry method, the methods that start it: <c>/// &lt;summary&gt;Requested by W57.Main, Tidus.E20Async.&lt;/summary&gt;</c>
///         (requests of a constant worker and entry), or that call a function worker (<c>Called by W13.Init.</c>);</item>
///         <item>on the script class, the story index: every <c>switch (StoryProgress)</c> in a worker's
///         <c>Init</c> or <c>Main</c> that requests entries, with the entries each case requests.</item>
///     </list>
///     Comments only: the source compiles to the same bytes. Requests whose worker or entry is computed at
///     run time are not listed.
/// </summary>
internal static class FlowNotes {
    /// <summary>The most methods a note lists; the rest are counted.</summary>
    public const int MaxListed = 24;

    /// <summary>The most entries a case of the story index lists; the rest are counted.</summary>
    public const int MaxPerCase = 6;

    private const string Indent = "    ";

    /// <summary>The workers with their notes: on their methods, and the same for their class line.</summary>
    public static WorkerSource[] Annotate(WorkerDecompiler[] workers, string[] names, WorkerSource[] sources) {
        // (target worker, entry) -> the requesting methods, and whether by call.
        Dictionary<(int Worker, int Entry), List<string>> requested = [], called = [];
        for (int w = 0; w < workers.Length; w++) {
            foreach ((int m, int t, int e, bool call) in workers[w].Calls()) {
                if (t < 0 || t >= workers.Length) continue;
                Dictionary<(int, int), List<string>> map = call ? called : requested;
                string by = $"{names[w]}.{workers[w].MethodName(m)}";
                if (!map.TryGetValue((t, e), out List<string>? list)) map[(t, e)] = list = [];
                if (!list.Contains(by)) list.Add(by);
            }
        }
        WorkerSource[] result = new WorkerSource[sources.Length];
        for (int t = 0; t < workers.Length; t++) {
            WorkerDecompiler worker = workers[t];
            List<(int Offset, string Text)> notes = [];
            StringBuilder classNotes = new();
            for (int m = 0; m < worker.MethodCount; m++) {
                List<int> entries = worker.EntriesOf(m);
                List<string> lines = [];
                foreach (int e in entries) {
                    string? by = By(requested.GetValueOrDefault((t, e)), called.GetValueOrDefault((t, e)));
                    if (by is null) continue;
                    lines.Add(entries.Count == 1 ? Capital(by) : string.Create(CultureInfo.InvariantCulture, $"Entry {e}: {by}"));
                    classNotes.Append(e == entries[0] ? $"{worker.MethodName(m)}: {by}" : string.Create(CultureInfo.InvariantCulture, $"Entry {e} (in {worker.MethodName(m)}): {by}")).Append('\n');
                }
                if (lines.Count == 0) continue;
                string text = lines.Count == 1
                    ? $"{Indent}/// <summary>{lines[0]}</summary>\n"
                    : $"{Indent}/// <summary>\n" + string.Concat(lines.Select(l => $"{Indent}/// {l}<br/>\n")) + $"{Indent}/// </summary>\n";
                notes.Add((worker.MethodOffsets[m], text));
            }
            string[] classLines = classNotes.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string cls = classLines.Length switch {
                0 => "",
                1 => $"/// <summary>{classLines[0]}</summary>\n",
                _ => "/// <summary>\n" + string.Concat(classLines.Select(l => $"/// {l}<br/>\n")) + "/// </summary>\n",
            };
            result[t] = sources[t] with { Notes = notes, ClassNotes = cls };
        }
        return result;
    }

    /// <summary>"requested by A, B." and/or "called by C."; null when neither.</summary>
    private static string? By(List<string>? requested, List<string>? called) {
        List<string> parts = [];
        if (requested is { Count: > 0 }) parts.Add("requested by " + List(requested));
        if (called is { Count: > 0 }) parts.Add("called by " + List(called));
        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    private static string List(List<string> names) =>
        names.Count <= MaxListed ? string.Join(", ", names) : string.Join(", ", names.Take(MaxListed)) + string.Create(CultureInfo.InvariantCulture, $" and {names.Count - MaxListed} more");

    private static string Capital(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    ///     The story index for the script class, or "" when no worker's <c>Init</c> or <c>Main</c> has a
    ///     <c>switch (StoryProgress)</c> that requests entries.
    /// </summary>
    public static string StoryIndex(WorkerDecompiler[] workers, string[] names) {
        StringBuilder sb = new();
        for (int w = 0; w < workers.Length; w++) {
            foreach ((int m, SwitchStmt sw) in workers[w].StorySwitches()) {
                if (!sw.Sections.Any(x => WorkerDecompiler.RequestsIn(x.Body).Count > 0)) continue;
                sb.Append(CultureInfo.InvariantCulture, $"/// {names[w]}.{workers[w].MethodName(m)}:\n");
                foreach (SwitchSection section in sw.Sections) {
                    List<string> targets = [];
                    foreach ((int t, int e) in WorkerDecompiler.RequestsIn(section.Body)) {
                        string target = t >= 0 && t < workers.Length ? Target(workers[t], names[t], e) : string.Create(CultureInfo.InvariantCulture, $"worker {t} entry {e}");
                        if (!targets.Contains(target)) targets.Add(target);
                    }
                    string list = targets.Count == 0 ? "-"
                        : targets.Count <= MaxPerCase ? string.Join(", ", targets)
                        : string.Join(", ", targets.Take(MaxPerCase)) + string.Create(CultureInfo.InvariantCulture, $" and {targets.Count - MaxPerCase} more");
                    sb.Append(CultureInfo.InvariantCulture, $"///   {Case(section.Label)}: {list}\n");
                }
            }
        }
        if (sb.Length == 0) return "";
        return "/// <summary>\n" +
               "/// The story index: each switch (StoryProgress) in a worker's Init or Main that requests entries,\n" +
               "/// with the entries each case requests (read off the code by atel).\n" +
               "/// <code>\n" + sb + "/// </code>\n" +
               "/// </summary>\n";
    }

    /// <summary>A requested entry by the method that starts with it (<c>Tidus.E20Async</c>), else <c>Tidus entry 21</c>.</summary>
    private static string Target(WorkerDecompiler worker, string name, int e) {
        for (int m = 0; m < worker.MethodCount; m++) {
            if (worker.EntriesOf(m) is var entries && entries.Contains(e)) {
                return entries[0] == e ? $"{name}.{worker.MethodName(m)}" : string.Create(CultureInfo.InvariantCulture, $"{name} entry {e} (in {worker.MethodName(m)})");
            }
        }
        return string.Create(CultureInfo.InvariantCulture, $"{name} entry {e}");
    }

    /// <summary>A case label in words (no <c>&lt;</c> in a doc comment): <c>Story.S00210_Bsvr0000 to Story.S00220_Bsil0100</c>.</summary>
    private static string Case(CaseLabel? c) {
        static string V(int v) => GameNames.Story.Member(v) ?? v.ToString(CultureInfo.InvariantCulture);
        return c switch {
            null => "otherwise",
            { Kind: CaseKind.Equal } => V(c.Value),
            { Kind: CaseKind.Less } => $"before {V(c.Value)}",
            { Kind: CaseKind.LessOrEqual } => $"up to {V(c.Value)}",
            { Kind: CaseKind.Greater } => $"after {V(c.Value)}",
            { Kind: CaseKind.GreaterOrEqual } => $"from {V(c.Value)}",
            _ => $"{V(c.Value)} to {V(c.High)}",
        };
    }
}
