// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Linker;

namespace Ffx.Atel.Cuts.Tests;

/// <summary>
///     The in-game switch on the player's scripts (docs/research/dead-code.md, "Switching a block on in game"). Needs
///     <c>FFX_GAME_DIR</c>; skipped without it.
/// </summary>
public sealed class CutCorpusTests(ITestOutputHelper output) {
    /// <summary>
    ///     For each name and each of its scripts: switching it on in the vanilla block gives exactly the block the linker
    ///     writes for <c>EnableCut</c>, and off gives the vanilla block again; the linked block reads as on as loaded, and
    ///     switching it off there gives vanilla too. Through the tracker, the same as it runs in game.
    /// </summary>
    [Fact]
    public void OnIsTheLinkersBytesAndOffIsVanilla() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        using VbfArchive archive = GameData.OpenArchiveExclusive(dir!);    // one corpus run on the machine at a time
        CutCatalog catalog = CutCatalog.Embedded();
        foreach (CutInfo cut in catalog.Cuts) {
            foreach (string script in cut.Scripts) {
                string path = GameData.Resolve(archive, script) ?? throw new InvalidOperationException($"no script {script}");
                byte[] vanillaFile = archive.Read(archive.Find(path)!);
                ScriptFile file = ScriptFile.Parse(vanillaFile);
                byte[] vanilla = file.Script.ToArray();
                Assert.Equal(script, ScriptCuts.NameOf(vanilla));

                Stopwatch watch = Stopwatch.StartNew();
                ScriptCuts cuts = ScriptCuts.Scan(vanilla, script, catalog);
                watch.Stop();
                IReadOnlyList<CutSite> sites = cuts.Sites[cut.Name];
                Assert.All(sites, s => Assert.False(s.LoadedOn));

                // What the linker writes for [assembly: EnableCut(nameof(Cut.Name))] (atel compile-mod's package).
                ScriptPackage package = new() {
                    File = path, Script = script, Format = ScriptPackage.CutsFormat,
                    Cuts = [new CutSwitch(cut.Name, cut.Signatures, sites.Count)],
                };
                LinkResult linked = ScriptLinker.Link(vanillaFile, [new ModScript("a", ScriptPackage.FromJson(package.ToJson()))]);
                Assert.True(linked.Changed, string.Join("\n", linked.Messages));
                byte[] linkedBlock = ScriptFile.Parse(linked.File).Script.ToArray();

                byte[] image = (byte[])vanilla.Clone();
                Assert.Equal(new ToggleResult(sites.Count, null), cuts.Toggle(image, cut.Name, on: true));
                Assert.Equal(linkedBlock, image);
                Assert.Equal(sites.Count, cuts.Toggle(image, cut.Name, on: false).Changed);
                Assert.Equal(vanilla, image);

                // The file a linked mod switched it on in: found as on, and off is vanilla.
                ScriptCuts fromLinked = ScriptCuts.Scan(linkedBlock, script, catalog);
                Assert.Equal(sites.Select(s => s.At), fromLinked.Sites[cut.Name].Select(s => s.At));
                Assert.All(fromLinked.Sites[cut.Name], s => Assert.True(s.LoadedOn));
                image = (byte[])linkedBlock.Clone();
                Assert.Equal(sites.Count, fromLinked.Toggle(image, cut.Name, on: false).Changed);
                Assert.Equal(vanilla, image);

                // Through the tracker, as in game: loaded, switched on, read again, switched off.
                CutStates states = new();
                Memory memory = new(vanilla);
                List<string> errors = [];
                CutTracker tracker = new(catalog, states, _ => { }, errors.Add);
                states.Set(cut.Name, true);
                tracker.Tick(memory);
                Assert.Equal(linkedBlock, memory.Block);
                memory.Block = (byte[])vanilla.Clone();
                tracker.Tick(memory);
                Assert.Equal(linkedBlock, memory.Block);
                states.Set(cut.Name, false);
                tracker.Tick(memory);
                Assert.Equal(vanilla, memory.Block);
                Assert.Empty(errors);
                Assert.Equal(1, tracker.Scans);

                output.WriteLine($"{cut.Name}: {script}, {sites.Count} block(s) at {string.Join(", ", sites.Select(s => $"w{s.Worker:d2} +0x{s.At:x}"))}; scan {watch.Elapsed.TotalMilliseconds:F0} ms");
            }
        }
    }

    /// <summary>One block, run by ctrl work 0.</summary>
    private sealed class Memory(byte[] block) : IScriptMemory {
        public byte[] Block { get; set; } = (byte[])block.Clone();
        public int CtrlWorks => 1;
        public bool InUse(int ctrl) => true;
        public void Blocks(int ctrl, List<nint> into) => into.Add(0x10000);
        public Span<byte> Bytes(nint at, int length) => Block.AsSpan(0, length);
    }
}
