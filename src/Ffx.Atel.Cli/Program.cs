// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Cli;

internal static class Program {
    private const string Usage = """
        usage: atel <command> [options]

          list [filter]                    scripts in the game (events, monsters, battle)
          disasm <script> [-o <file>]      disassemble (m001, bjyt0400 or an archive path)
          asm <script> <file.asm> -o <out> assemble onto the original, write the whole file

        The game is found through FFX_GAME_DIR (the folder with FFX.exe), or --game <dir>.
        """;

    private static int Main(string[] args) {
        try {
            return Run(args);
        }
        catch (Exception ex) when (ex is ScriptFormatException or AssemblyException or IOException or InvalidDataException or UsageException) {
            Console.Error.WriteLine($"atel: {ex.Message}");
            return 1;
        }
    }

    private sealed class UsageException(string message) : Exception(message);

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
        using VbfArchive archive = GameData.OpenArchive(game ?? throw new UsageException("set FFX_GAME_DIR or pass --game <dir>"));
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
            default:
                throw new UsageException($"unknown command {command}\n{Usage}");
        }
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

    private static void Write(string? path, string text) {
        if (path is null) Console.Out.Write(text);
        else File.WriteAllText(path, text);
    }
}
