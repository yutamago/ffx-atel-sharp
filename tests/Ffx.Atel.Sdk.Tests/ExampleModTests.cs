// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text.Json;

namespace Ffx.Atel.Sdk.Tests;

/// <summary>
///     The SDK as a modder uses it (docs/sdk.md): packed to a local feed (SdkFeed), the vanilla source of m209
///     decompiled by the packed atel, the example mods (examples/LarvaHp, ToughLarva, LarvaInject, LarvaCount,
///     LarvaCountReader) built with
///     <c>dotnet build</c>, and their packages compared with what <c>atel compile-mod</c> writes. Needs the game (FFX_GAME_DIR).
/// </summary>
[Collection(SdkFeedCollection.Name)]
public sealed class ExampleModTests(SdkFeed sdk) {
    private const string GameDirVariable = "FFX_GAME_DIR";

    [Fact]
    public void ExampleModBuildsToTheCompileModPackage() {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(GameDirVariable))) Assert.Skip($"{GameDirVariable} is not set");
        string repo = SdkFeed.RepoRoot();
        string root = Directory.CreateDirectory(Path.Combine(sdk.Root, "examples")).FullName;
        Dictionary<string, string?> env = sdk.Env;
        try {
            string feed = sdk.Feed;
            string version = sdk.Version;
            string nupkg = sdk.SdkPackage;
            string extracted = Path.Combine(root, "sdk");
            ZipFile.ExtractToDirectory(nupkg, extracted);
            string atel = Path.Combine(extracted, "tools", "atel", "atel.dll");

            // The vanilla source, as the modder gets it: decompiled with the SDK's atel, projects using the SDK.
            string vanilla = Path.Combine(root, "vanilla");
            DotNet.Run(root, env, [atel, "decompile", "m209", "-o", vanilla, "--sdk", feed]);
            // atel pins the package of its own version: the CLI and the package agree.
            using (JsonDocument global = JsonDocument.Parse(File.ReadAllText(Path.Combine(vanilla, "global.json")))) {
                Assert.Equal(version, global.RootElement.GetProperty("msbuild-sdks").GetProperty("Ffx.Atel.Sdk").GetString());
            }
            DotNet.Run(vanilla, env, ["build", Path.Combine(vanilla, "Atel.slnx"), .. DotNet.BuildFlags]);

            // The SDK's version, as a modder pins it (docs/sdk.md); the examples' (empty) build files.
            Directory.CreateDirectory(Path.Combine(root, "mod"));
            foreach (string f in Directory.GetFiles(Path.Combine(repo, "examples"))) File.Copy(f, Path.Combine(root, "mod", Path.GetFileName(f)));
            File.WriteAllText(Path.Combine(root, "mod", "global.json"), $$"""
                { "msbuild-sdks": { "Ffx.Atel.Sdk": "{{version}}" } }
                """);
            // Each example mod, copied out of the repo, built, and compared with atel compile-mod's package.
            // In this order: LarvaCountReader reads LarvaCount's saved field.
            foreach ((string example, string id, string shows) in new[] {
                         ("LarvaHp", "yuta.larvahp", "\"postfix\""), ("ToughLarva", "yuta.toughlarva", "\"monsterStats\""), ("LarvaInject", "yuta.larvainject", "\"inject\""),
                         ("LarvaCount", "yuta.larvacount", "\"saved\": \"LarvaCount.Progress.LarvasMet\""),
                         ("LarvaCountReader", "yuta.larvacountreader", "\"saved\": \"yuta.larvacount/LarvaCount.Progress.LarvasMet\""),
                     }) {
                string mod = Path.Combine(root, "mod", example);
                Directory.CreateDirectory(mod);
                foreach (string f in Directory.GetFiles(Path.Combine(repo, "examples", example))) File.Copy(f, Path.Combine(mod, Path.GetFileName(f)));
                DotNet.Run(mod, env, ["build", mod, $"-p:AtelVanillaSource={vanilla}", .. DotNet.BuildFlags]);

                string built = Path.Combine(mod, "bin", "Debug", "net10.0", "mods", id);
                string expected = Path.Combine(root, "expected", example);
                DotNet.Run(root, env, [atel, "compile-mod", mod, "--vanilla", vanilla, "-o", expected]);

                string package = Path.Combine("atel", "ffx_ps2", "ffx", "master", "jppc", "battle", "mon", "_m209", "m209.bin.json");
                Assert.Equal(DotNet.Files(expected), DotNet.Files(built).Where(f => !f.EndsWith(".manifest.json", StringComparison.Ordinal)));
                Assert.Contains(package, DotNet.Files(built));
                foreach (string f in DotNet.Files(expected)) Assert.Equal(File.ReadAllBytes(Path.Combine(expected, f)), File.ReadAllBytes(Path.Combine(built, f)));
                Assert.Contains(shows, File.ReadAllText(Path.Combine(built, package)), StringComparison.Ordinal);

                // A file-only Fahrenheit mod that depends on the loader; no DLL (it would hold vanilla code).
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(built, $"{id}.manifest.json")));
                Assert.Equal(id, manifest.RootElement.GetProperty("Id").GetString());
                string[] dependencies = example == "LarvaCountReader" ? ["yuta.atel", "yuta.larvacount"] : ["yuta.atel"];
                Assert.Equal(dependencies, manifest.RootElement.GetProperty("Dependencies").EnumerateArray().Select(e => e.GetString()));
                Assert.Empty(Directory.GetFiles(Path.Combine(mod, "bin"), "*.dll", SearchOption.AllDirectories));
            }
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }
}
