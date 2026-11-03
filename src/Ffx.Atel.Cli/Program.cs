// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Text;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Cli;

internal static class Program {
    private const string Usage = """
        usage: atel <command> [options]

          list [filter]                    scripts in the game (events, monsters, battle)
          disasm <script> [-o <file>]      disassemble (m001, bjyt0400 or an archive path)
          asm <script> <file.asm> -o <out> assemble onto the original, write the whole file
          survey [-o <file>] [--natives <natives.json>] [--story <file.json>]
                                           measure the corpus (docs/corpus.md)
        """ + "\n" + SourceCommands.Usage + "\n" + CompileAllCommand.Usage + "\n" + ExportCommand.Usage + "\n" + ModCommands.Usage + "\n" + TextCommand.Usage + "\n" + """

        The game is found through FFX_GAME_DIR (the folder with FFX.exe), or --game <dir>.
        """;

    private static int Main(string[] args) {
        try {
            return Run(args);
        }
        catch (Exception ex) when (ex is ScriptFormatException or AssemblyException or IOException or InvalidDataException or UsageException
                                       or ArgumentException or FormatException or Compiler.CompileException) {
            Console.Error.WriteLine($"atel: {ex.Message}");
            return 1;
        }
    }

    internal sealed class UsageException(string message) : Exception(message);

    private static int Run(string[] argv) {
        List<string> args = [.. argv];
        string? game = TakeOption(args, "--game") ?? GameData.GameDir();
        string? output = TakeOption(args, "-o");
        if (args.Count == 0 || args[0] is "-h" or "--help") {
            Console.Error.Write(Usage);
            return args.Count == 0 ? 2 : 0;
        }
        string command = args[0];
        args.RemoveAt(0);
        string gameDir = game ?? throw new UsageException("set FFX_GAME_DIR or pass --game <dir>");
        // Whole-corpus commands take the machine-wide corpus lock: one at a time, they each use several GB.
        bool wholeCorpus = command is "roundtrip" or "survey" or "export" or "compile-all" || (command == "decompile" && args.Contains("all"))
                        || (command == "text" && TextCommand.IsWholeCorpus(args));
        using VbfArchive archive = wholeCorpus ? GameData.OpenArchiveExclusive(gameDir) : GameData.OpenArchive(gameDir);
        switch (command) {
            case "list": {
                string filter = args.Count > 0 ? args[0] : "";
                foreach (string p in GameData.AllScriptPaths(archive).Where(p => p.Contains(filter, StringComparison.Ordinal))) {
                    Console.WriteLine(p);
                }
                return 0;
            }
            case "disasm": {
                string path = Resolve(archive, args);
                ScriptFile file = ScriptFile.Parse(archive.Read(path));
                string text = $"; {path}\n{Assembly.Disassemble(file.Script)}";
                Write(output, text);
                return 0;
            }
            case "asm": {
                if (args.Count < 2 || output is null) throw new UsageException("asm <script> <file.asm> -o <out>");
                string path = Resolve(archive, args);
                ScriptFile file = ScriptFile.Parse(archive.Read(path));
                ScriptBlock rebuilt = Assembly.Assemble(file.Script, File.ReadAllText(args[1]));
                File.WriteAllBytes(output, file.ReplaceScript(rebuilt).ToArray());
                return 0;
            }
            case "decompile": {
                string? comments = TakeOption(args, "--text-comments");
                GameLanguage? language = comments is null ? null : TextCommand.ParseLanguage(comments);
                string? names = TakeOption(args, "--names");
                return SourceCommands.Decompile(archive, args, output, !TakeFlag(args, "--no-library"), ProjectOptions(args), language, names);
            }
            case "text":
                return TextCommand.Run(archive, args, output);
            case "compile": {
                string? library = TakeOption(args, "--library");
                return SourceCommands.Compile(archive, args, output, TakeFlag(args, "--check"), library, Options(args));
            }
            case "roundtrip": {
                bool share = !TakeFlag(args, "--no-library");
                string? names = TakeOption(args, "--names");
                return SourceCommands.RoundTrip(archive, args, output, TakeFlag(args, "--check"), share, names);
            }
            case "compile-all":
                return CompileAllCommand.Run(archive, args, TakeFlag(args, "--check"));
            case "export": {
                bool merge = !TakeFlag(args, "--no-merge"), compile = !TakeFlag(args, "--no-compile");
                bool check = TakeFlag(args, "--check"), push = TakeFlag(args, "--push");
                // The source repo must build after a clone: with the SDK, its package goes into the tree.
                SourceCommands.ProjectOptions projects = ProjectOptions(args) with { RequirePackage = true };
                string? comments = TakeOption(args, "--text-comments");
                GameLanguage? language = TakeFlag(args, "--no-text-comments") ? null : comments is null ? GameLanguage.English : TextCommand.ParseLanguage(comments);
                return ExportCommand.Run(archive, game, args, merge, compile, check, push, projects, language);
            }
            case "compile-mod":
                return ModCommands.CompileMod(archive, args, output, TakeOption(args, "--vanilla"), TakeOption(args, "--id"), TakeFlag(args, "--check"), Options(args));
            case "link":
                return ModCommands.Link(archive, args, output);
            case "survey":
                return SurveyCommand.Run(archive, args, output);
            default:
                throw new UsageException($"unknown command {command}\n{Usage}");
        }
    }

    private static SourceCommands.ProjectOptions ProjectOptions(List<string> args) {
        string? sdk = TakeOption(args, "--sdk");
        bool noSdk = TakeFlag(args, "--no-sdk");
        if (noSdk && sdk is not null) throw new UsageException("--sdk and --no-sdk exclude each other");
        return new(noSdk ? Decompiler.Layout.GameReference.Assembly : Decompiler.Layout.GameReference.Sdk, sdk);
    }

    private static string Resolve(VbfArchive archive, List<string> args) {
        if (args.Count == 0) throw new UsageException("which script?");
        return GameData.Resolve(archive, args[0]) ?? throw new UsageException($"no script {args[0]}");
    }

    private static string? TakeOption(List<string> args, string name) {
        int i = args.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= args.Count) throw new UsageException($"{name} needs a value");
        string value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }

    private static bool TakeFlag(List<string> args, string name) => args.Remove(name);

    /// <summary><c>--debug natives|drop|error</c>, <c>--no-debug</c> (= drop): what <c>Debug.*</c> calls compile to (docs/formats/debug.md).</summary>
    private static Compiler.CompileOptions Options(List<string> args) {
        bool off = TakeFlag(args, "--no-debug");
        string? value = TakeOption(args, "--debug");
        Compiler.DebugMode mode = value is null ? (off ? Compiler.DebugMode.Drop : Compiler.DebugMode.Natives)
            : Compiler.CompileOptions.ParseDebug(value) ?? throw new UsageException($"--debug {value}: natives, drop or error");
        return new Compiler.CompileOptions { Debug = mode };
    }

    private static void Write(string? path, string text) {
        if (path is null) Console.Out.Write(text);
        else File.WriteAllText(path, text);
    }
}
