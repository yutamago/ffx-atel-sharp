// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Bytecode.Tests;

/// <summary>The Phase 0 facts the decompiler and compiler build on, checked over every script.</summary>
public sealed class SurveyTests(ITestOutputHelper output) {
    private static readonly Lazy<Survey> s_survey = new(() => {
        using VbfArchive archive = GameData.OpenArchiveExclusive(GameData.GameDir()!);
        return Survey.Load(archive, NativeSignatures.Load(NativeSignatures.Find() ?? throw new InvalidOperationException("data/natives.json not found")));
    });

    private static Survey LoadOrSkip() {
        CorpusTests.OpenOrSkip().Dispose();
        return s_survey.Value;
    }

    /// <summary>
    ///     With the argument counts of data/natives.json, every worker's stack simulates cleanly, and the stack is
    ///     empty at every label, every jump and every end: no expression spans a branch.
    /// </summary>
    [Fact]
    public void TheStackIsEmptyAtEveryLabelJumpAndEnd() {
        Survey survey = LoadOrSkip();
        List<string> failures = [];
        long jumps = 0;
        foreach (SurveyScript s in survey.Scripts) {
            foreach (WorkerFlow f in s.Flows) {
                if (f.Issues.Count > 0) failures.Add($"{s.Name} worker {f.WorkerIndex}: {f.Issues[0]}");
                for (int i = 0; i < f.Code.Count; i++) {
                    byte op = f.Code[i].Code;
                    bool atLabel = f.LabelsHere[i].Count > 0;
                    int after = f.Depth[i] - (StackEffects.Jumps(op) ? StackEffects.Of(op).Pops : 0);
                    if (StackEffects.Jumps(op)) jumps++;
                    if ((atLabel && f.Depth[i] != 0) || (StackEffects.Jumps(op) && after != 0)) failures.Add($"{s.Name} worker {f.WorkerIndex} at {f.Code[i].Offset}: depth {f.Depth[i]}");
                }
            }
        }
        Assert.Empty(failures.Take(20));
        output.WriteLine($"{survey.Scripts.Count} scripts, {jumps} jumps, all with an empty stack");
    }

    [Fact]
    public void TheSurveyRuns() {
        Survey survey = LoadOrSkip();
        string report = survey.Run();
        Assert.Contains("## Stack", report, StringComparison.Ordinal);
        Assert.NotEmpty(survey.StoryWrites);
        output.WriteLine($"{report.Length} characters of report, {survey.StoryWrites.Count} story values");
    }
}
