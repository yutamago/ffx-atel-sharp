// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Ffx.Atel.Sdk.Tests;

/// <summary>
///     The SDK as a modder uses it (docs/sdk.md): packed to a local feed, the vanilla source of m209
///     decompiled by the packed atel, the example mod (examples/LarvaHp) built with <c>dotnet build</c>,
///     and its package compared with what <c>atel compile-mod</c> writes. Needs the game (FFX_GAME_DIR).
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
            // it has cached, and every pack here has the same version.
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
            Run(repo, env, ["pack", Path.Combine(repo, "src", "Ffx.Atel.Sdk"), "-c", configuration, "-o", feed, .. BuildFlags]);
            string nupkg = Assert.Single(Directory.GetFiles(feed, "Ffx.Atel.Sdk.*.nupkg"));
            string sdk = Path.Combine(root, "sdk");
            ZipFile.ExtractToDirectory(nupkg, sdk);
            string atel = Path.Combine(sdk, "tools", "atel", "atel.dll");

            // The vanilla source, as the modder gets it: decompiled with the SDK's atel, projects using the SDK.
            string vanilla = Path.Combine(root, "vanilla");
            Run(root, env, [atel, "decompile", "m209", "-o", vanilla, "--sdk", feed]);
            Assert.Contains("Ffx.Atel.Sdk", File.ReadAllText(Path.Combine(vanilla, "global.json")), StringComparison.Ordinal);
            Run(vanilla, env, ["build", Path.Combine(vanilla, "Atel.slnx"), .. BuildFlags]);

            // The example mod, copied out of the repo with the examples' (empty) build files.
            string mod = Path.Combine(root, "mod", "LarvaHp");
            Directory.CreateDirectory(mod);
            foreach (string f in Directory.GetFiles(Path.Combine(repo, "examples"))) File.Copy(f, Path.Combine(root, "mod", Path.GetFileName(f)));
            foreach (string f in Directory.GetFiles(Path.Combine(repo, "examples", "LarvaHp"))) File.Copy(f, Path.Combine(mod, Path.GetFileName(f)));
            Run(mod, env, ["build", mod, $"-p:AtelVanillaSource={vanilla}", .. BuildFlags]);

            string built = Path.Combine(mod, "bin", "Debug", "net10.0", "mods", "yuta.larvahp");
            string expected = Path.Combine(root, "expected");
            Run(root, env, [atel, "compile-mod", mod, "--vanilla", vanilla, "-o", expected]);

            string package = Path.Combine("atel", "ffx_ps2", "ffx", "master", "jppc", "battle", "mon", "_m209", "m209.bin.json");
            Assert.Equal(Files(expected), Files(built).Where(f => !f.EndsWith(".manifest.json", StringComparison.Ordinal)));
            Assert.Contains(package, Files(built));
            foreach (string f in Files(expected)) Assert.Equal(File.ReadAllBytes(Path.Combine(expected, f)), File.ReadAllBytes(Path.Combine(built, f)));
            Assert.Contains("\"postfix\"", File.ReadAllText(Path.Combine(built, package)), StringComparison.Ordinal);

            // A file-only Fahrenheit mod that depends on the loader; no DLL (it would hold vanilla code).
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(built, "yuta.larvahp.manifest.json")));
            Assert.Equal("yuta.larvahp", manifest.RootElement.GetProperty("Id").GetString());
            Assert.Equal(["yuta.atel"], manifest.RootElement.GetProperty("Dependencies").EnumerateArray().Select(e => e.GetString()));
            Assert.Empty(Directory.GetFiles(Path.Combine(mod, "bin"), "*.dll", SearchOption.AllDirectories));
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
