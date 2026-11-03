// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Text;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Decompiler;

namespace Ffx.Atel.Cli;

/// <summary>
///     <c>atel dead</c>: the catalogue of the dead <c>if (false)</c> blocks (docs/research/dead-code.md):
///     every block with its place, size, signature, group and the natives and messages it uses, the
///     groups counted per area, and the code of each block with its messages' text. Game code and text:
///     the files stay on the player's machine.
/// </summary>
internal static partial class DeadCommand {
    public const string Usage = """
          dead [filter] -o <dir> [--lang en]
                                           catalogue the dead if (false) blocks (docs/research/dead-code.md):
                                           <dir>/dead-blocks.tsv (one row per block), dead-summary.md (the
                                           groups per area), dead-code.txt (each block's C# and the text of
                                           its messages); game code and text: stays on your machine
        """;

    /// <summary>A block, where it is, and what the catalogue makes of it.</summary>
    /// <param name="Area">The area folder (<c>Events.Mc_Macalania</c>).</param>
    /// <param name="DeadOnly">Its messages that no live code of the script names as a constant.</param>
    internal sealed record Row(string Script, string Area, DeadBlock Block, string Group, IReadOnlyList<string> Natives, IReadOnlyList<int> Messages, IReadOnlyList<int> DeadOnly);

    public static int Run(VbfArchive archive, List<string> args, string? output) {
        if (output is null) throw new Program.UsageException("dead [filter] -o <dir>");
        string? lang = TakeOption(args, "--lang");
        GameLanguage language = lang is null ? GameLanguage.English : TextCommand.ParseLanguage(lang);
        string filter = args.Count > 0 ? args[0] : "";
        List<string> paths = [.. GameData.AllScriptPaths(archive).Where(p => p.Contains(filter, StringComparison.Ordinal))];
        ConcurrentBag<Row> rows = [];
        ConcurrentBag<string> failures = [];
        Parallel.ForEach(paths, path => {
            try {
                DecompiledScript d = ScriptDecompiler.Decompile(path, ScriptFile.Parse(archive.Read(path)), deadBlocks: true);
                if (d.DeadBlocks.Count == 0) return;
                HashSet<int> live = LiveMessages(d.Source);
                string area = string.Join('.', d.Names.Namespace.Split('.').Take(2));
                foreach (DeadBlock b in d.DeadBlocks) {
                    List<string> natives = [.. NativeCall().Matches(b.Code).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal)];
                    List<int> messages = [.. TextRef().Matches(b.Code).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order()];
                    rows.Add(new Row(d.Names.Name, area, b, Classify(d.Names.Name, d.Names.Category, b, natives), natives, messages, [.. messages.Where(m => !live.Contains(m))]));
                }
            }
            catch (Exception ex) when (ex is ScriptFormatException or InvalidOperationException or FormatException) {
                failures.Add($"{path}: {ex.Message}");
            }
        });
        foreach (string f in failures.Order(StringComparer.Ordinal)) Console.Error.WriteLine(f);

        List<Row> sorted = [.. rows.OrderBy(r => r.Area, StringComparer.Ordinal).ThenBy(r => r.Script, StringComparer.Ordinal).ThenBy(r => r.Block.Offset)];
        Dictionary<string, int> copies = sorted.GroupBy(r => r.Block.Signature).ToDictionary(g => g.Key, g => g.Count());
        Directory.CreateDirectory(output);
        WriteTable(Path.Combine(output, "dead-blocks.tsv"), sorted, copies);
        WriteCode(Path.Combine(output, "dead-code.txt"), sorted, new ScriptMessages(archive), language, paths);
        string summary = Summary(sorted, copies);
        File.WriteAllText(Path.Combine(output, "dead-summary.md"), summary);
        Console.Out.Write(summary);
        return failures.IsEmpty ? 0 : 1;
    }

    /// <summary>
    ///     The messages live code names as a constant (<c>Text[n]</c>): every line of the source outside the
    ///     braces of an <c>if (false)</c> or <c>if (Cut.…)</c>. A message chosen at run time is not seen.
    /// </summary>
    internal static HashSet<int> LiveMessages(string source) {
        HashSet<int> live = [];
        string[] lines = source.Split('\n');
        int deadDepth = 0, depth = 0;
        bool opening = false;
        foreach (string raw in lines) {
            string line = raw.Trim();
            if (line == "{") {
                depth++;
                if (opening) {
                    deadDepth = depth;
                    opening = false;
                }
                continue;
            }
            if (line == "}") {
                if (depth == deadDepth) deadDepth = 0;
                depth--;
                continue;
            }
            if (deadDepth == 0 && line is "if (false)" or "else if (false)" || deadDepth == 0 && CutCondition().IsMatch(line)) {
                opening = true;
                continue;
            }
            if (deadDepth == 0) foreach (Match m in TextRef().Matches(line)) live.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        return live;
    }

    // ----- Groups --------------------------------------------------------------------------------

    /// <summary>Whether a script is a blitzball game: the <c>bltz</c> scripts and their variants, and the Luca match.</summary>
    internal static bool IsBlitzball(string script) => script.Contains("bltz", StringComparison.Ordinal) || script == "lchb0806";

    /// <summary>The group of a block, by the script and the natives it calls (docs/research/dead-code.md, "Groups"): the first rule that fits.</summary>
    internal static string Classify(string script, ScriptCategory category, DeadBlock b, IReadOnlyList<string> natives) {
        string code = b.Code;
        bool Has(string n) => natives.Contains(n, StringComparer.Ordinal);
        bool Prefix(string p) => natives.Any(n => n.StartsWith(p, StringComparison.Ordinal));
        if (b.Instructions == 0) return "empty";
        if (IsBlitzball(script)) {
            if (Prefix("Common.Sprite")) return "blitzball-hud";
            if (Prefix("Common.Msg") || Has("Common.MacroDicText")) return "blitzball-message";
            if (Has("Chr.SetAutoMotion")) return "blitzball-motion";
            return natives.Count == 0 ? "blitzball-data" : "blitzball-other";
        }
        if (script == "mcfr0100") return "celestial-weapon";
        if (category != ScriptCategory.Event) {
            if (code.Contains("BattleDialogLinesSeen", StringComparison.Ordinal)) return "battle-line";
            if (Prefix("Cam.")) return "battle-camera";
            return "battle-actor";
        }
        if (Has("Common.MapChange")) return "map-change";
        if (Prefix("Common.Msg")) return "dialogue";
        if (Has("Calc.FacingWithin")) return "npc-reaction";
        if (b.WorkerName.StartsWith("Takarabox", StringComparison.Ordinal)) return "treasure-chest";
        if (script.StartsWith("test", StringComparison.Ordinal)) return "test-map";
        if (code.Contains("Async(", StringComparison.Ordinal) || Has("Common.TriggerSetActive")) return "scene-start";
        return "other";
    }

    private static string Summary(List<Row> rows, Dictionary<string, int> copies) {
        StringBuilder sb = new();
        List<Row> top = [.. rows.Where(r => r.Block.Depth == 0)];
        sb.Append(CultureInfo.InvariantCulture, $"# Dead blocks\n\n{rows.Count} blocks ({rows.Count(r => !r.Block.HasElse)} `if (false)`, {rows.Count(r => r.Block.HasElse)} `if (false) … else`) in {rows.Select(r => r.Script).Distinct().Count()} scripts; ");
        sb.Append(CultureInfo.InvariantCulture, $"{rows.Count - top.Count} of them inside another dead block; {top.Sum(r => (long)r.Block.Instructions)} dead instructions; {copies.Count} distinct signatures; ");
        sb.Append(CultureInfo.InvariantCulture, $"{rows.Count(r => r.Block.LiveTwins > 0)} with a live twin in their script; {rows.Count(r => r.DeadOnly.Count > 0)} name messages no live code names; {rows.Count(r => r.Block.Name is not null)} named.\n\n");
        sb.Append("Instructions count blocks in live code only (an inner block's are inside its outer block's).\n\n");
        sb.Append("## Groups\n\n| Group | Blocks | With live twin | Instructions | Scripts | Signatures |\n|---|---:|---:|---:|---:|---:|\n");
        foreach (IGrouping<string, Row> g in rows.GroupBy(r => r.Group).OrderByDescending(g => g.Count())) {
            sb.Append(CultureInfo.InvariantCulture, $"| {g.Key} | {g.Count()} | {g.Count(r => r.Block.LiveTwins > 0)} | {g.Where(r => r.Block.Depth == 0).Sum(r => (long)r.Block.Instructions)} | {g.Select(r => r.Script).Distinct().Count()} | {g.Select(r => r.Block.Signature).Distinct().Count()} |\n");
        }
        sb.Append("\n## Areas\n\n| Area | Scripts | Blocks | Instructions | Groups |\n|---|---:|---:|---:|---|\n");
        foreach (IGrouping<string, Row> g in rows.GroupBy(r => r.Area).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)) {
            string groups = string.Join(", ", g.GroupBy(r => r.Group).OrderByDescending(x => x.Count()).Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Key} {x.Count()}")));
            sb.Append(CultureInfo.InvariantCulture, $"| {g.Key} | {g.Select(r => r.Script).Distinct().Count()} | {g.Count()} | {g.Where(r => r.Block.Depth == 0).Sum(r => (long)r.Block.Instructions)} | {groups} |\n");
        }
        sb.Append("\n## Scripts\n\n| Script | Blocks | With live twin | Dead-only messages |\n|---|---:|---:|---|\n");
        foreach (IGrouping<string, Row> g in rows.GroupBy(r => r.Script).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)) {
            string only = string.Join(" ", g.SelectMany(r => r.DeadOnly).Distinct().Order());
            sb.Append(CultureInfo.InvariantCulture, $"| {g.Key} | {g.Count()} | {g.Count(r => r.Block.LiveTwins > 0)} | {only} |\n");
        }
        sb.Append("\n## Natives in dead code\n\n| Native | Blocks |\n|---|---:|\n");
        foreach (IGrouping<string, string> g in rows.SelectMany(r => r.Natives).GroupBy(n => n).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(40)) {
            sb.Append(CultureInfo.InvariantCulture, $"| {g.Key} | {g.Count()} |\n");
        }
        sb.Append("\n## Most copied signatures\n\n| Signature | Copies | Group | Instructions | First |\n|---|---:|---|---:|---|\n");
        foreach (IGrouping<string, Row> g in rows.GroupBy(r => r.Block.Signature).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(20)) {
            Row r = g.First();
            sb.Append(CultureInfo.InvariantCulture, $"| {g.Key} | {g.Count()} | {r.Group} | {r.Block.Instructions} | {r.Script} {Where(r.Block)} |\n");
        }
        return sb.ToString();
    }

    internal static string Where(DeadBlock b) =>
        string.Create(CultureInfo.InvariantCulture, $"w{b.Worker:d2} ({b.WorkerName}) e{string.Join("/", b.Entries)} @{b.Offset:x4}");

    private static void WriteTable(string path, List<Row> rows, Dictionary<string, int> copies) {
        StringBuilder sb = new("area\tscript\tworker\tname\tentries\toffset\tkind\tinstructions\tdepth\tsignature\tcopies\tlive_twins\tgroup\tcut\tnatives\tmessages\tdead_only_messages\n");
        foreach (Row r in rows) {
            DeadBlock b = r.Block;
            sb.Append(CultureInfo.InvariantCulture, $"{r.Area}\t{r.Script}\t{b.Worker}\t{b.WorkerName}\t{string.Join(' ', b.Entries)}\t{b.Offset:x4}\t{(b.HasElse ? "dead-else" : "dead")}\t");
            sb.Append(CultureInfo.InvariantCulture, $"{b.Instructions}\t{b.Depth}\t{b.Signature}\t{copies[b.Signature]}\t{b.LiveTwins}\t{r.Group}\t{b.Name}\t{string.Join(' ', r.Natives)}\t{string.Join(' ', r.Messages)}\t{string.Join(' ', r.DeadOnly)}\n");
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteCode(string path, List<Row> rows, ScriptMessages messages, GameLanguage language, List<string> paths) {
        Dictionary<string, string> pathOf = paths.Where(p => GameData.Identify(p) is not null).GroupBy(p => GameData.Identify(p)!.Value.Name).ToDictionary(g => g.Key, g => g.First());
        using StreamWriter w = new(path);
        foreach (IGrouping<string, Row> script in rows.GroupBy(r => r.Script)) {
            IReadOnlyList<string?>? table = null;
            if (pathOf.TryGetValue(script.Key, out string? p)) {
                try {
                    table = messages.Display(p, language);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException) {
                }
            }
            foreach (Row r in script) {
                DeadBlock b = r.Block;
                w.Write(string.Create(CultureInfo.InvariantCulture,
                    $"==== {r.Area} {r.Script} {Where(b)} {(b.HasElse ? "dead-else" : "dead")} {b.Instructions} instr depth {b.Depth} twins {b.LiveTwins} sig {b.Signature} group {r.Group}{(b.Name is null ? "" : " cut " + b.Name)}\n"));
                w.Write(b.Code);
                if (b.ElseCode is not null) {
                    w.Write("---- else (live)\n");
                    w.Write(b.ElseCode.Length > 1500 ? b.ElseCode[..1500] + "…\n" : b.ElseCode);
                }
                foreach (int m in r.Messages) {
                    string? text = table is not null && m < table.Count ? table[m] : null;
                    if (text is not null) w.Write(string.Create(CultureInfo.InvariantCulture, $"  // Text[{m}]{(r.DeadOnly.Contains(m) ? " (dead only)" : "")}: {text.ReplaceLineEndings(" / ")}\n"));
                }
            }
        }
    }

    [GeneratedRegex(@"\b([A-Z][A-Za-z0-9_]*\.[A-Z][A-Za-z0-9_]*)\(")]
    private static partial Regex NativeCall();

    [GeneratedRegex(@"\bText\[(\d+)\]")]
    private static partial Regex TextRef();

    [GeneratedRegex(@"^(else )?if \((Ffx\.)?Cut\.[A-Za-z_][A-Za-z0-9_]*\)$")]
    private static partial Regex CutCondition();

    private static string? TakeOption(List<string> args, string name) {
        int i = args.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= args.Count) throw new Program.UsageException($"{name} needs a value");
        string value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }
}
