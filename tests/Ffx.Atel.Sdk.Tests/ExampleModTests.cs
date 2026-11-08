// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Ffx.Atel.Sdk.Tests;

/// <summary>
///     The SDK as a modder uses it (docs/sdk.md): packed to a local feed, the vanilla source of m209
///     decompiled by the packed atel, the example mods (examples/LarvaHp, ToughLarva, LarvaInject, LarvaCount,
///     LarvaCountReader) built with
///     <c>dotnet build</c>, and their packages compared with what <c>atel compile-mod</c> writes. Needs the game (FFX_GAME_DIR).
/// </summary>
public sealed class ExampleModTests {
    private const string GameDirVariable = "FFX_GAME_DIR";

    [Fact]
    public void ExampleModBuildsToTheCompileModPackage() {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(GameDirVariable))) Assert.Skip($"{GameDirVariable} is not set");
        string repo = RepoRoot();
        string configuration = typeof(ExampleModTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "Configuration").Value!;
        string root = Directory.CreateTempSubdirectory("atel-sdk-test-").FullName;
        try {
            // Packages only from the local feed, into a cache of the test's own: NuGet never re-reads a version
            // it has cached, and the version is the commit's, uncommitted changes or not (docs/sdk.md, "Versions").
            string feed = Path.Combine(root, "feed");
            Dictionary<string, string?> env = new() { ["NUGET_PACKAGES"] = Path.Combine(root, "packages") };
            File.WriteAllText(Path.Combine(root, "nuget.config"), """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="local" value="feed" />
                  </packageSources>
                </configuration>
                """);
            // The repo's projects build into an artifacts folder of the test's own: their obj/ would otherwise
            // point at the package folder above, deleted at the end while the other test projects still build.
            Run(repo, env, ["pack", Path.Combine(repo, "src", "Ffx.Atel.Sdk"), "-c", configuration, "-o", feed,
                "--artifacts-path", Path.Combine(root, "artifacts"), .. BuildFlags]);
            string nupkg = Assert.Single(Directory.GetFiles(feed, "Ffx.Atel.Sdk.*.nupkg"));
            string version = Path.GetFileNameWithoutExtension(nupkg)["Ffx.Atel.Sdk.".Length..];
            string sdk = Path.Combine(root, "sdk");
            ZipFile.ExtractToDirectory(nupkg, sdk);
            string atel = Path.Combine(sdk, "tools", "atel", "atel.dll");

            // The vanilla source, as the modder gets it: decompiled with the SDK's atel, projects using the SDK.
            string vanilla = Path.Combine(root, "vanilla");
            Run(root, env, [atel, "decompile", "m209", "-o", vanilla, "--sdk", feed]);
            // atel pins the package of its own version: the CLI and the package agree.
            using (JsonDocument global = JsonDocument.Parse(File.ReadAllText(Path.Combine(vanilla, "global.json")))) {
                Assert.Equal(version, global.RootElement.GetProperty("msbuild-sdks").GetProperty("Ffx.Atel.Sdk").GetString());
            }
            Run(vanilla, env, ["build", Path.Combine(vanilla, "Atel.slnx"), .. BuildFlags]);

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
                Run(mod, env, ["build", mod, $"-p:AtelVanillaSource={vanilla}", .. BuildFlags]);

                string built = Path.Combine(mod, "bin", "Debug", "net10.0", "mods", id);
                string expected = Path.Combine(root, "expected", example);
                Run(root, env, [atel, "compile-mod", mod, "--vanilla", vanilla, "-o", expected]);

                string package = Path.Combine("atel", "ffx_ps2", "ffx", "master", "jppc", "battle", "mon", "_m209", "m209.bin.json");
                Assert.Equal(Files(expected), Files(built).Where(f => !f.EndsWith(".manifest.json", StringComparison.Ordinal)));
                Assert.Contains(package, Files(built));
                foreach (string f in Files(expected)) Assert.Equal(File.ReadAllBytes(Path.Combine(expected, f)), File.ReadAllBytes(Path.Combine(built, f)));
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

    /// <summary>One build at a time and no servers left running: the machine runs other heavy jobs.</summary>
    private static readonly string[] BuildFlags = ["-p:UseSharedCompilation=false", "--disable-build-servers", "-nodeReuse:false", "-m:1"];

    private static List<string> Files(string dir) =>
        [.. Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f)).Order(StringComparer.Ordinal)];

    private static string RepoRoot() {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent) {
            if (File.Exists(Path.Combine(d.FullName, "ffx-atel-sharp.slnx"))) return d.FullName;
        }
        throw new InvalidOperationException("not inside the ffx-atel-sharp checkout");
    }

    /// <summary>Runs <c>dotnet</c> (the one running this test) with the arguments; fails the test on a non-zero exit.</summary>
    private static void Run(string workingDirectory, Dictionary<string, string?> env, string[] args) {
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet") {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args) start.ArgumentList.Add(a);
        // The test host's MSBuild settings would point the child builds at the wrong SDK.
        foreach (string name in new[] { "MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly" }) start.Environment.Remove(name);
        foreach ((string key, string? value) in env) start.Environment[key] = value;
        using Process process = Process.Start(start)!;
        StringBuilder output = new();
        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromMinutes(10))) {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"dotnet {string.Join(' ', args)}: timed out\n{output}");
        }
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', args)}: exit code {process.ExitCode}\n{output}");
    }
}
