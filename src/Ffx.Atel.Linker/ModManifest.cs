// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Ffx.Atel.Linker;

/// <summary>
///     An Atel mod's <c>mod.toml</c> (docs/plan.md, "Mods"): its source language and how it relates to
///     other mods. Read with a small parser for the subset these keys need: <c>key = "text"</c>,
///     <c>key = ["a", "b"]</c>, comments, and tables (whose keys are ignored).
/// </summary>
public sealed class ModManifest {
    /// <summary>The language its new messages are written in (<c>en</c>, …).</summary>
    public string SourceLanguage { get; init; } = "en";

    /// <summary>Mods it must be linked after, when they are enabled.</summary>
    public List<string> After { get; init; } = [];

    /// <summary>Mods that must be enabled for it to be linked.</summary>
    public List<string> Requires { get; init; } = [];

    /// <summary>Mods it cannot be linked with.</summary>
    public List<string> Incompatible { get; init; } = [];

    public static ModManifest Empty { get; } = new();

    /// <exception cref="FormatException">A line the parser does not understand.</exception>
    public static ModManifest Parse(string text) {
        Dictionary<string, object> values = new(StringComparer.Ordinal);
        bool inTable = false;
        string[] lines = text.Split('\n');
        for (int n = 0; n < lines.Length; n++) {
            string line = StripComment(lines[n]).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[')) {
                inTable = true;
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) throw new FormatException($"mod.toml line {n + 1}: expected key = value");
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            object parsed = value.StartsWith('[') ? List(value, n + 1) : Text(value, n + 1);
            if (!inTable) values[key] = parsed;
        }
        return new ModManifest {
            SourceLanguage = values.GetValueOrDefault("source_language") as string ?? "en",
            After = values.GetValueOrDefault("after") as List<string> ?? [],
            Requires = values.GetValueOrDefault("requires") as List<string> ?? [],
            Incompatible = values.GetValueOrDefault("incompatible") as List<string> ?? [],
        };
    }

    private static string StripComment(string line) {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++) {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) quoted = !quoted;
            else if (line[i] == '#' && !quoted) return line[..i];
        }
        return line;
    }

    private static List<string> List(string value, int line) {
        if (!value.EndsWith(']')) throw new FormatException($"mod.toml line {line}: a list ends on its line");
        string inner = value[1..^1].Trim();
        if (inner.Length == 0) return [];
        return [.. inner.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Select(s => Text(s, line) as string ?? throw new FormatException($"mod.toml line {line}: a list holds quoted strings"))];
    }

    private static object Text(string value, int line) {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        if (value is "true" or "false" || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return value;
        throw new FormatException($"mod.toml line {line}: expected a quoted string");
    }
}
