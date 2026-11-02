// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Text;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Decompiler.Layout;

namespace Ffx.Atel.Cli;

/// <summary>
///     <c>atel text export</c>: the messages each script's <c>Text[n]</c> reads, per language, as JSON beside
///     the decompiled source, for the editor extension (<c>docs/editor.md</c>).
/// </summary>
internal static class TextCommand {
    public const string Usage = """
          text export [<script>...] [-o <source dir>] [--lang en,de,…]
                                           the messages of every event and battle script (or those
                                           named), decoded, as <source dir>/.atel/text/<lang>/<script>.json
                                           for the editor extension (docs/editor.md); <source dir> is
                                           the -o of atel decompile (default: the current folder if it
                                           has Atel.slnx); every language without --lang (en, ja, fr,
                                           es, de, it, ko, zh); game text: stays on your machine
        """;

    /// <summary>Where the files go under a source root: <c>.atel/text</c>, which the root's <c>.gitignore</c> ignores.</summary>
    public static readonly string Folder = Path.Combine(ProjectLayout.ToolFolder, "text");

    private static readonly JsonSerializerOptions s_json = new() {
        WriteIndented = true,
        // The text as it is (Japanese, quotes), not \u escapes: the file is read by people too.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Whether <c>text export</c> reads every script (none named): then it takes the corpus lock.</summary>
    public static bool IsWholeCorpus(List<string> args) {
        List<string> scripts = [.. args.Skip(1)]; // after "export"; -o is taken already
        int lang = scripts.IndexOf("--lang");
        if (lang >= 0) scripts.RemoveRange(lang, Math.Min(2, scripts.Count - lang));
        return scripts.Count == 0;
    }

    public static int Run(VbfArchive archive, List<string> args, string? output) {
        if (args.Count == 0 || args[0] != "export") throw new Program.UsageException("text export [<script>...] [-o <source dir>] [--lang en,de,…]");
        args.RemoveAt(0);
        string? langOption = TakeOption(args, "--lang");
        IReadOnlyList<GameLanguage> languages = langOption is null ? GameLanguages.All : ParseLanguages(langOption);
        string root = output ?? (File.Exists(ProjectLayout.Solution) ? "." : throw new Program.UsageException("text export needs -o <source dir> (the folder atel decompile wrote)"));
        List<string> paths = args.Count == 0
            ? [.. GameData.AllScriptPaths(archive).Where(p => GameData.Identify(p)?.Category is ScriptCategory.Event or ScriptCategory.Battle)]
            : [.. args.Select(a => GameData.Resolve(archive, a) ?? throw new Program.UsageException($"no script {a}"))];
        ScriptMessages messages = new(archive);
        int files = 0, texts = 0;
        foreach (GameLanguage language in languages) {
            string dir = Path.Combine(root, Folder, language.IsoCode());
            Directory.CreateDirectory(dir);
            foreach (string path in paths) {
                (ScriptCategory category, string name) = GameData.Identify(path)!.Value;
                IReadOnlyList<string?>? list = messages.Display(path, language);
                if (list is null) continue;
                File.WriteAllText(Path.Combine(dir, name + ".json"), Json(name, category, language, list));
                files++;
                texts += list.Count(t => t is not null);
            }
        }
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{files} files, {texts} messages in {languages.Count} languages: {Path.Combine(root, Folder)}"));
        return 0;
    }

    /// <summary>The file's JSON: <c>{ "script", "category", "language", "messages": [ … ] }</c>, a message's index its position.</summary>
    public static string Json(string name, ScriptCategory category, GameLanguage language, IReadOnlyList<string?> messages) =>
        JsonSerializer.Serialize(new TextFile(name, category.ToString().ToLowerInvariant(), language.IsoCode(), messages), s_json) + "\n";

    private sealed record TextFile(
        [property: System.Text.Json.Serialization.JsonPropertyName("script")] string Script,
        [property: System.Text.Json.Serialization.JsonPropertyName("category")] string Category,
        [property: System.Text.Json.Serialization.JsonPropertyName("language")] string Language,
        [property: System.Text.Json.Serialization.JsonPropertyName("messages")] IReadOnlyList<string?> Messages);

    public static GameLanguage ParseLanguage(string code) =>
        GameLanguages.Parse(code) ?? throw new Program.UsageException($"unknown language {code} (en, ja, fr, es, de, it, ko, zh)");

    private static List<GameLanguage> ParseLanguages(string list) =>
        [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseLanguage).Distinct()];

    private static string? TakeOption(List<string> args, string name) {
        int i = args.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= args.Count) throw new Program.UsageException($"{name} needs a value");
        string value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }
}
