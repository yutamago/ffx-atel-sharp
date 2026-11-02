// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Cli;

/// <summary><c>atel survey</c>: Phase 0's measurements over every script (docs/corpus.md).</summary>
internal static class SurveyCommand {
    public static int Run(VbfArchive archive, List<string> args, string? output) {
        string? nativesPath = Take(args, "--natives") ?? NativeSignatures.Find()
            ?? throw new Program.UsageException("data/natives.json not found; pass --natives <file>");
        string? storyPath = Take(args, "--story");
        Survey survey = Survey.Load(archive, NativeSignatures.Load(nativesPath));
        string report = survey.Run();
        if (output is null) Console.Out.Write(report);
        else File.WriteAllText(output, report);
        if (storyPath is not null) File.WriteAllText(storyPath, survey.StoryJson());
        return 0;
    }

    private static string? Take(List<string> args, string name) {
        int i = args.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= args.Count) throw new Program.UsageException($"{name} needs a value");
        string value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }
}
