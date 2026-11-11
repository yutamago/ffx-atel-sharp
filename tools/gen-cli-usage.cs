// SPDX-License-Identifier: MIT
//
// Writes the usage text of each `atel` command for the website's CLI pages (docs/website.md, "Reference"):
// site/src/generated/cli/<command>.txt, cut from what `atel --help` prints, so a page never shows a usage the
// tool no longer has. Build output (.gitignore): the Pages workflow runs this before `astro build`.
//
//   - A command is a line of the help indented by two spaces; the lines indented further are its own.
//     `text export` and `names export` are commands of two words: text-export.txt, names-export.txt.
//   - An option line (`--sdk <dir|nupkg>  (decompile, export) …`) goes to each command it names.
//   - The last line (how atel finds the game) goes to every command.
//   - Each command needs its page, site/src/content/docs/reference/cli/<command>.mdx, and each page its command:
//     a new command fails here until someone writes its page.
//
// usage: dotnet run tools/gen-cli-usage.cs [-- <atel.dll>]   (without it: builds src/Ffx.Atel.Cli and runs that)

#:property PublishAot=false

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, ".."));
if (!Directory.Exists(Path.Combine(root, "site"))) root = Directory.GetCurrentDirectory();
string outDir = Path.Combine(root, "site", "src", "generated", "cli");
string pagesDir = Path.Combine(root, "site", "src", "content", "docs", "reference", "cli");

string help = args.Length > 0
    ? Run("dotnet", [args[0], "--help"])
    : Run("dotnet", ["run", "--project", Path.Combine(root, "src", "Ffx.Atel.Cli"), "-c", "Release", "--no-launch-profile", "--", "--help"]);

List<Entry> commands = [], options = [];
List<string> footer = [];
Entry? current = null;
foreach (string raw in help.Replace("\r", "", StringComparison.Ordinal).Split('\n')) {
    string line = raw.TrimEnd();
    if (line.Length == 0 || line.StartsWith("usage:", StringComparison.Ordinal)) continue;
    if (!line.StartsWith(' ')) {
        footer.Add(line);
        current = null;
        continue;
    }
    if (Regex.IsMatch(line, "^  [^ ]")) {
        string[] words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words[0].StartsWith('-')) {
            current = new Entry(words[0], words[0], [line[2..]]);
            options.Add(current);
        }
        else {
            bool twoWords = words.Length > 1 && Regex.IsMatch(words[1], "^[a-z]+$");
            current = new Entry(twoWords ? $"{words[0]}-{words[1]}" : words[0], twoWords ? $"{words[0]} {words[1]}" : words[0], [line[2..]]);
            commands.Add(current);
        }
        continue;
    }
    if (current is null) throw new InvalidDataException($"atel --help: an indented line before any command: {line}");
    current.Lines.Add(line.Length > 2 ? line[2..] : "");
}
if (commands.Count == 0) throw new InvalidDataException("atel --help: no commands found");

if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
Directory.CreateDirectory(outDir);
foreach (Entry c in commands) {
    StringBuilder text = new();
    foreach (string l in c.Lines) text.Append(l).Append('\n');
    // Options for several commands name them in parentheses: "(decompile, roundtrip) keep duplicated workers …".
    List<Entry> shared = [.. options.Where(o => Regex.Match(string.Join(" ", o.Lines), @"\(([a-z, -]+)\)") is { Success: true } m
        && m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries).Contains(c.Display))];
    if (shared.Count > 0) {
        text.Append("\noptions shared with other commands:\n");
        foreach (Entry o in shared) {
            foreach (string l in o.Lines) text.Append(l).Append('\n');
        }
    }
    if (footer.Count > 0) text.Append('\n').Append(string.Join("\n", footer)).Append('\n');
    File.WriteAllText(Path.Combine(outDir, c.Name + ".txt"), text.ToString());
}

HashSet<string> names = [.. commands.Select(c => c.Name)];
HashSet<string> pages = Directory.Exists(pagesDir)
    ? [.. Directory.GetFiles(pagesDir, "*.mdx").Select(f => Path.GetFileNameWithoutExtension(f))]
    : [];
List<string> problems = [
    .. commands.Where(c => !pages.Contains(c.Name)).Select(c => $"atel {c.Display} has no page: write site/src/content/docs/reference/cli/{c.Name}.mdx"),
    .. pages.Where(p => !names.Contains(p)).Select(p => $"site/src/content/docs/reference/cli/{p}.mdx: atel has no such command"),
];
foreach (string p in problems) Console.Error.WriteLine(p);
Console.WriteLine($"{commands.Count} commands, {options.Count} shared options -> {outDir}");
return problems.Count == 0 ? 0 : 1;

static string Run(string file, string[] argv) {
    ProcessStartInfo psi = new(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (string a in argv) psi.ArgumentList.Add(a);
    // `atel --help` needs no game; keep a set FFX_GAME_DIR out of it all the same.
    psi.Environment.Remove("FFX_GAME_DIR");
    using Process p = Process.Start(psi)!;
    Task<string> stdout = p.StandardOutput.ReadToEndAsync(), stderr = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    // atel writes its usage to stderr; `dotnet run` writes build messages to stdout.
    if (p.ExitCode != 0) throw new InvalidOperationException($"{file} {string.Join(' ', argv)} exited with {p.ExitCode}:\n{stdout.Result}{stderr.Result}");
    return stderr.Result;
}

static string ThisFile([CallerFilePath] string path = "") => path;

/// <param name="Name">The file name: <c>compile-mod</c>, <c>text-export</c>.</param>
/// <param name="Display">As typed: <c>compile-mod</c>, <c>text export</c>.</param>
internal sealed record Entry(string Name, string Display, List<string> Lines);
