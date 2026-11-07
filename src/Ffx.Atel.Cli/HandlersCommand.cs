// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Cli;

/// <summary><c>atel handlers</c>: each monster and battle script's handler table (<see cref="HandlerTable"/>), as text.</summary>
internal static class HandlersCommand {
    public static int Run(VbfArchive archive, List<string> args, string? output) {
        string filter = args.Count > 0 ? args[0] : "";
        StringBuilder text = new();
        foreach (string path in GameData.AllScriptPaths(archive).Where(p => p.Contains(filter, StringComparison.Ordinal))) {
            if (GameData.Identify(path) is not { } id || id.Category == ScriptCategory.Event) continue;
            ScriptFile file = ScriptFile.Parse(archive.Read(path));
            HandlerTable? table = HandlerTable.Of(file);
            if (table is null) continue;
            text.Append(CultureInfo.InvariantCulture, $"{id.Name} works={table.WorkCount} workers={file.Script.Workers.Count}\n");
            foreach (Handler h in table.Handlers) {
                string entries = string.Join(' ', h.Entries.Select(e => e?.ToString(CultureInfo.InvariantCulture) ?? "-"));
                text.Append(CultureInfo.InvariantCulture, $"  0x{h.Signal:x2} W{h.Worker:d2} {h.Flag} [{entries}]\n");
            }
        }
        if (output is null) Console.Write(text.ToString());
        else File.WriteAllText(output, text.ToString());
        return 0;
    }
}
