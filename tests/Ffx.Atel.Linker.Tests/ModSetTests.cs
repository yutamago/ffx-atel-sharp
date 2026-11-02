// SPDX-License-Identifier: MIT

namespace Ffx.Atel.Linker.Tests;

public sealed class ModSetTests : IDisposable {
    private readonly string _dir = Directory.CreateTempSubdirectory("atel-linker-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ReadsTheManifest() {
        ModManifest m = ModManifest.Parse("""
            # An Atel mod
            source_language = "de"   # German
            after = ["yuta.base", "other.mod"]
            requires = []
            [something]
            after = ["ignored"]
            """);
        Assert.Equal("de", m.SourceLanguage);
        Assert.Equal(["yuta.base", "other.mod"], m.After);
        Assert.Empty(m.Requires);
        Assert.Throws<FormatException>(() => ModManifest.Parse("after = [unquoted]"));
    }

    private AtelMod Mod(string id, string toml) {
        string dir = Path.Combine(_dir, id);
        Directory.CreateDirectory(Path.Combine(dir, AtelMod.PackageDir));
        File.WriteAllText(Path.Combine(dir, AtelMod.ManifestFile), toml);
        return AtelMod.Load(id, dir);
    }

    [Fact]
    public void LeavesOutModsWhoseRequirementsAreMissingAndIncompatibleOnes() {
        List<LinkMessage> messages = [];
        List<AtelMod> kept = ModSet.Resolve([
            Mod("a", ""),
            Mod("b", "requires = [\"missing\"]"),
            Mod("c", "incompatible = [\"a\"]"),
            Mod("d", "requires = [\"a\"]\nafter = [\"e\"]"),
            Mod("e", ""),
        ], messages);
        Assert.Equal(["a", "d", "e"], kept.Select(m => m.Id));
        Assert.Contains(messages, m => m.Mod == "b" && m.Severity == LinkSeverity.Error);
        Assert.Contains(messages, m => m.Mod == "c" && m.Severity == LinkSeverity.Conflict);
        Assert.Contains(messages, m => m.Mod == "d" && m.Severity == LinkSeverity.Warning);
    }

    [Fact]
    public void LinksEveryFileAModHasAPackageFor() {
        byte[] vanilla = LinkerTests.VanillaFile();
        ScriptPackage p = LinkerTests.Package(vanilla, new EntryPatch {
            Kind = PatchKind.Postfix, Worker = 1, Name = "After", Target = "Init", Code = LinkerTests.Frag("pushimm 9\npopi0"),
        });
        string dir = Path.Combine(_dir, "mod");
        string file = Path.Combine(dir, AtelMod.PackageDir, "ffx_ps2", "test", "m998.bin.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, p.ToJson());
        AtelMod mod = AtelMod.Load("mod", dir);
        Assert.Empty(mod.Errors);
        Assert.True(AtelMod.IsAtelMod(dir));

        List<LinkedFile> linked = ModSet.Link([mod], path => path == "ffx_ps2/test/m998.bin" ? vanilla : null);
        LinkedFile f = Assert.Single(linked);
        Assert.Null(f.Error);
        Assert.True(f.Result!.Changed);
        Assert.NotEqual(vanilla, f.Result.File);
    }
}
