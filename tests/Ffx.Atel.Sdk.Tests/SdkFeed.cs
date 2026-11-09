// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Ffx.Atel.Sdk.Tests;

/// <summary>
///     The SDK and the templates as a modder gets them: packed once into a local feed, in a folder of the tests' own,
///     with a NuGet package cache of its own (NuGet never re-reads a version it has cached, and the version is the
///     commit's, uncommitted changes or not: docs/sdk.md, "Versions"). Shared by the test classes of the "SDK feed"
///     collection; packed when a test first asks for it, so the tests that skip pack nothing.
/// </summary>
public sealed class SdkFeed : IDisposable {
    private readonly Lazy<(string Feed, string Version)> _packed;

    public SdkFeed() {
        Root = Directory.CreateTempSubdirectory("atel-sdk-test-").FullName;
        Env = new() { ["NUGET_PACKAGES"] = Path.Combine(Root, "packages") };
        // Packages only from the local feed; a project folder's own nuget.config adds to this one.
        File.WriteAllText(Path.Combine(Root, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="feed" />
              </packageSources>
            </configuration>
            """);
        _packed = new(Pack);
    }

    /// <summary>The tests' folder: the feed, the package cache, and whatever the tests write.</summary>
    public string Root { get; }

    /// <summary>The environment of every <c>dotnet</c> the tests run: the package cache.</summary>
    public Dictionary<string, string?> Env { get; }

    /// <summary>The folder with <c>Ffx.Atel.Sdk.&lt;version&gt;.nupkg</c> and <c>Ffx.Atel.Templates.&lt;version&gt;.nupkg</c>.</summary>
    public string Feed => _packed.Value.Feed;

    /// <summary>The packages' version, the commit's.</summary>
    public string Version => _packed.Value.Version;

    public string SdkPackage => Path.Combine(Feed, $"Ffx.Atel.Sdk.{Version}.nupkg");
    public string TemplatesPackage => Path.Combine(Feed, $"Ffx.Atel.Templates.{Version}.nupkg");

    private (string, string) Pack() {
        string repo = RepoRoot();
        string configuration = typeof(SdkFeed).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "Configuration").Value!;
        string feed = Path.Combine(Root, "feed");
        // The repo's projects build into an artifacts folder of the tests' own: their obj/ would otherwise point at the
        // package folder above, deleted at the end while the other test projects still build.
        foreach (string project in new[] { "Ffx.Atel.Sdk", "Ffx.Atel.Templates" }) {
            DotNet.Run(repo, Env, ["pack", Path.Combine(repo, "src", project), "-c", configuration, "-o", feed,
                "--artifacts-path", Path.Combine(Root, "artifacts"), .. DotNet.BuildFlags]);
        }
        string nupkg = Assert.Single(Directory.GetFiles(feed, "Ffx.Atel.Sdk.*.nupkg"));
        string version = Path.GetFileNameWithoutExtension(nupkg)["Ffx.Atel.Sdk.".Length..];
        Assert.True(File.Exists(Path.Combine(feed, $"Ffx.Atel.Templates.{version}.nupkg")), "the templates have the SDK's version");
        return (feed, version);
    }

    public static string RepoRoot() {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent) {
            if (File.Exists(Path.Combine(d.FullName, "ffx-atel-sharp.slnx"))) return d.FullName;
        }
        throw new InvalidOperationException("not inside the ffx-atel-sharp checkout");
    }

    public void Dispose() {
        try {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) {
            // A build server may still hold a file; the temp folder is the OS's to clean then.
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SdkFeedCollection : ICollectionFixture<SdkFeed> {
    public const string Name = "SDK feed";
}

/// <summary>Runs <c>dotnet</c> (the one running the tests) as a modder would.</summary>
public static class DotNet {
    /// <summary>One build at a time and no servers left running: the machine runs other heavy jobs.</summary>
    public static readonly string[] BuildFlags = ["-p:UseSharedCompilation=false", "--disable-build-servers", "-nodeReuse:false", "-m:1"];

    /// <summary>Runs <c>dotnet</c> with the arguments; fails the test on a non-zero exit. Returns the output.</summary>
    public static string Run(string workingDirectory, Dictionary<string, string?> env, string[] args) {
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
        return output.ToString();
    }

    public static List<string> Files(string dir) =>
        [.. Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f)).Order(StringComparer.Ordinal)];
}
