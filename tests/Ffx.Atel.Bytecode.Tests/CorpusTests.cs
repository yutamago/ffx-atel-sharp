// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Bytecode.Tests;

/// <summary>Tests over every shipped script. Need <c>FFX_GAME_DIR</c>; skipped without it.</summary>
public sealed class CorpusTests(ITestOutputHelper output) {
    internal static VbfArchive OpenOrSkip() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        return GameData.OpenArchive(dir!);
    }

    [Fact]
    public void EveryScriptDisassemblesAndReassemblesExactly() {
        using VbfArchive archive = OpenOrSkip();
        List<string> paths = GameData.ScriptPaths(archive);
        int monsters = 0, events = 0;
        long instructions = 0;
        ConcurrentBag<string> failures = [];
        Parallel.ForEach(paths, path => {
            byte[] original = archive.Read(path);
            try {
                ScriptFile file = ScriptFile.Parse(original);
                string text = Assembly.Disassemble(file.Script);
                ScriptBlock rebuilt = Assembly.Assemble(file.Script, text);
                if (!rebuilt.Bytes.SequenceEqual(file.Script.Bytes)) failures.Add($"{path}: script block differs");
                else if (!file.ReplaceScript(rebuilt).Bytes.SequenceEqual(original)) failures.Add($"{path}: file differs");
                Interlocked.Add(ref instructions, text.Split('\n').Count(l => l.StartsWith("    ", StringComparison.Ordinal)));
                if (file.Kind == ScriptFileKind.Monster) Interlocked.Increment(ref monsters);
                else Interlocked.Increment(ref events);
            }
            catch (Exception ex) when (ex is ScriptFormatException or AssemblyException) {
                failures.Add($"{path}: {ex.Message}");
            }
        });
        Assert.Empty(failures);
        Assert.Equal((361, 397), (monsters, events));
        output.WriteLine($"{monsters} monster and {events} event scripts, {instructions} instructions round-tripped");
    }

    /// <summary>
    ///     Every block taken apart into a <see cref="ScriptImage"/> writes back byte for byte; reports how
    ///     much the layout rules leave to pins and patches.
    /// </summary>
    [Fact]
    public void EveryBlockRebuildsFromItsImage() {
        using VbfArchive archive = OpenOrSkip();
        List<string> paths = GameData.ScriptPaths(archive);
        ConcurrentBag<string> failures = [];
        ConcurrentDictionary<string, int> pins = new();
        long patchBytes = 0, patched = 0, pinned = 0;
        Parallel.ForEach(paths, path => {
            ScriptBlock block = ScriptFile.Parse(archive.Read(path)).Script;
            try {
                ScriptImage image = ScriptImage.FromBlock(block);
                byte[] written = image.Write();
                int length = (int)block.U32(0x10);
                if (!written.AsSpan().SequenceEqual(block.Bytes[..length])) failures.Add($"{path}: differs");
                foreach (string pin in image.Pins.Keys) pins.AddOrUpdate(System.Text.RegularExpressions.Regex.Replace(pin, @"^w\d+\.", "w*."), 1, (_, c) => c + 1);
                if (image.Pins.Count > 0) Interlocked.Increment(ref pinned);
                if (image.Patches.Count > 0) Interlocked.Increment(ref patched);
                Interlocked.Add(ref patchBytes, image.Patches.Values.Sum(v => v.Length));
            }
            catch (ScriptFormatException ex) {
                failures.Add($"{path}: {ex.Message}");
            }
        });
        Assert.Empty(failures);
        output.WriteLine($"{paths.Count} blocks rebuild from their images; {pinned} have pins, {patched} patches ({patchBytes} bytes)");
        foreach ((string pin, int count) in pins.OrderByDescending(p => p.Value)) output.WriteLine($"  pin {pin}: {count}");
    }

    [Fact]
    public void AGrownWorkerMovesToTheEndAndKeepsWorking() {
        using VbfArchive archive = OpenOrSkip();
        string path = GameData.MonsterPrefix + "_m001/m001.bin";
        byte[] original = archive.Read(path);
        ScriptFile file = ScriptFile.Parse(original);
        string text = Assembly.Disassemble(file.Script);

        // 600 bytes of no-ops at the start of worker 1: far more than fits.
        const string Marker = "\nworker 1 type 2\ne0:\n";
        Assert.Contains(Marker, text);
        string edited = text.Replace(Marker, Marker + string.Concat(Enumerable.Repeat("    nop\n", 600)));
        ScriptBlock grown = Assembly.Assemble(file.Script, edited);
        Assert.True(grown.Bytes.Length > file.Script.Bytes.Length);
        Assert.True(grown.Workers[1].Start >= file.Script.Workers[2].End);

        static string WorkerText(string t) {
            int start = t.IndexOf("\nworker 1 ", StringComparison.Ordinal);
            int end = t.IndexOf("\nworker ", start + 1, StringComparison.Ordinal);
            return t[start..(end < 0 ? t.Length : end)];
        }
        Assert.Equal(WorkerText(edited), WorkerText(Assembly.Disassemble(grown)));

        ScriptFile repacked = file.ReplaceScript(grown);
        Assert.True(repacked.Bytes.Length > original.Length);
        // Stable: disassembling and reassembling the grown file changes nothing.
        ScriptBlock again = Assembly.Assemble(repacked.Script, Assembly.Disassemble(repacked.Script));
        Assert.True(again.Bytes.SequenceEqual(repacked.Script.Bytes));
    }

    /// <summary>
    ///     Our disassembler and ffx-rust's <c>ffx script disasm</c> write the same text for every script
    ///     (native annotations aside). Needs <c>FFX_SCRIPT_CLI</c>, the path of ffx-rust's <c>ffx</c> binary.
    /// </summary>
    [Fact]
    public void DisassemblyMatchesFfxRust() {
        using VbfArchive archive = OpenOrSkip();
        string? cli = Environment.GetEnvironmentVariable("FFX_SCRIPT_CLI");
        if (string.IsNullOrEmpty(cli)) Assert.Skip("FFX_SCRIPT_CLI is not set (path of ffx-rust's ffx binary)");
        List<string> paths = GameData.ScriptPaths(archive);
        ConcurrentBag<string> failures = [];
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, path => {
            ScriptFile file = ScriptFile.Parse(archive.Read(path));
            string ours = $"; {path}\n{Assembly.Disassemble(file.Script)}";
            ProcessStartInfo psi = new(cli, ["script", "disasm", path]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using Process p = Process.Start(psi)!;
            string theirs = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            string[] a = ours.Split('\n'), b = theirs.Split('\n');
            if (a.Length != b.Length) {
                failures.Add($"{path}: {a.Length} lines, ffx-rust {b.Length}");
                return;
            }
            for (int i = 0; i < a.Length; i++) {
                if (StripAnnotation(a[i]) != StripAnnotation(b[i])) {
                    failures.Add($"{path}:{i + 1}: `{a[i]}` vs ffx-rust `{b[i]}`");
                    return;
                }
            }
        });
        Assert.Empty(failures.Take(20));
        output.WriteLine($"{paths.Count} scripts disassemble as ffx-rust does");
    }

    /// <summary>ffx-rust annotates native calls with their handlers; we don't (yet).</summary>
    private static string StripAnnotation(string line) {
        string t = line.TrimStart();
        if (!t.StartsWith("func ", StringComparison.Ordinal) && !t.StartsWith("func_facc ", StringComparison.Ordinal)) return line;
        int semicolon = line.IndexOf(';');
        return semicolon < 0 ? line : line[..semicolon].TrimEnd();
    }
}
