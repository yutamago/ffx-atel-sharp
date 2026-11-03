// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Layout;
using Ffx.Atel.Linker;

namespace Ffx.Atel.Cli.Tests;

/// <summary>
///     The link-time switch for named dead blocks on the player's files (docs/research/dead-code.md, "Switching a
///     block on"). Needs <c>FFX_GAME_DIR</c>; skipped without it.
/// </summary>
public sealed class CutCorpusTests(ITestOutputHelper output) {
    /// <summary>
    ///     For each name in <c>data/cut-names.toml</c>: the package <c>atel compile-mod</c> writes for
    ///     <c>[assembly: EnableCut(nameof(Cut.Name))]</c>, linked onto the vanilla file, changes each block's
    ///     <c>jmp L</c> (<c>b0 lo hi</c>) into <c>nop_76 L</c> (<c>f6 lo hi</c>) at the jmp the decompiler names, and
    ///     nothing else in the file; a second mod enabling the same name gives the same file.
    /// </summary>
    [Fact]
    public void EveryNamedBlockSwitchesOnInPlace() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        using VbfArchive archive = GameData.OpenArchiveExclusive(dir!);    // one corpus run on the machine at a time
        CutNames names = CutNames.Default;
        foreach (string name in names.Names.Values.Distinct().Order(StringComparer.Ordinal)) {
            Dictionary<string, ScriptPackage> packages = new(StringComparer.Ordinal);
            ModCommands.AddCut(archive, packages, name);
            Assert.Equal(names.Scripts[name].Length, packages.Count);
            foreach ((string path, ScriptPackage package) in packages) {
                byte[] vanilla = archive.Read(archive.Find(path)!);
                ScriptFile file = ScriptFile.Parse(vanilla);
                CutSwitch cut = Assert.Single(package.Cuts!);
                Assert.Equal(ScriptPackage.CutsFormat, package.Format);

                // The blocks the decompiler names, by their jmp's offset.
                DecompiledScript d = ScriptDecompiler.Decompile(path, file, deadBlocks: true);
                List<DeadBlock> named = [.. d.DeadBlocks.Where(b => b.Name == name)];
                Assert.Equal(named.Count, cut.Blocks);

                // Through the package's JSON, as the loader reads it.
                ScriptPackage loaded = ScriptPackage.FromJson(package.ToJson());
                LinkResult once = ScriptLinker.Link(vanilla, [new ModScript("a", loaded)]);
                Assert.True(once.Changed, string.Join("\n", once.Messages));
                Assert.Equal(vanilla.Length, once.File.Length);
                HashSet<int> jumps = [.. named.Select(b => file.SectionStart + file.Script.CodeStart + (int)b.Offset)];
                for (int i = 0; i < vanilla.Length; i++) {
                    if (jumps.Contains(i)) {
                        Assert.Equal(DeadJumps.JmpByte, vanilla[i]);
                        Assert.Equal(DeadJumps.NopByte, once.File[i]);
                        Assert.Equal(vanilla[(i + 1)..(i + 3)], once.File[(i + 1)..(i + 3)]);    // the same operand, L
                    }
                    else if (vanilla[i] != once.File[i]) {
                        Assert.Fail($"{name}: {package.Script} changed at 0x{i:x} outside its blocks' jmps");
                    }
                }
                // The linked script still parses, and has no dead block of that name left.
                ScriptFile linked = ScriptFile.Parse(once.File);
                Assert.Empty(DeadJumps.Find(linked.Script, cut.Signatures.ToHashSet(StringComparer.Ordinal)));

                LinkResult twice = ScriptLinker.Link(vanilla, [new ModScript("a", loaded), new ModScript("b", loaded)]);
                Assert.Equal(["a", "b"], twice.Applied);
                Assert.Equal(once.File, twice.File);
                output.WriteLine($"{name}: {package.Script}, {cut.Blocks} block(s) at {string.Join(", ", named.Select(b => $"w{b.Worker:d2} @{b.Offset:x4}"))}; {jumps.Count * 3} bytes rewritten, {jumps.Count} changed");
            }
        }
    }

    /// <summary>The <c>[scripts]</c> of <c>data/cut-names.toml</c> list every script that has a named block, by the linker's finder.</summary>
    [Fact]
    public void TheNamedBlocksAreOnlyInTheirListedScripts() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        using VbfArchive archive = GameData.OpenArchiveExclusive(dir!);
        CutNames names = CutNames.Default;
        HashSet<string> signatures = [.. names.Names.Keys];
        Dictionary<string, SortedSet<string>> found = new(StringComparer.Ordinal);
        foreach (string path in GameData.AllScriptPaths(archive)) {
            ScriptFile file = ScriptFile.Parse(archive.Read(archive.Find(path)!));
            foreach (DeadJump j in DeadJumps.Find(file.Script, signatures)) {
                string name = names.Names[j.Signature];
                if (!found.TryGetValue(name, out SortedSet<string>? scripts)) found[name] = scripts = new(StringComparer.Ordinal);
                scripts.Add(GameData.Identify(path)!.Value.Name);
            }
        }
        foreach ((string name, string[] scripts) in names.Scripts) {
            Assert.True(found.ContainsKey(name), $"no block of {name}");
            Assert.Equal(scripts.Order(StringComparer.Ordinal), found[name]);
        }
    }
}
