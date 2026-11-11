// SPDX-License-Identifier: MIT

using System.IO.Compression;

namespace Ffx.Atel.Sdk.Tests;

/// <summary>
///     <c>AtelSharp.Cli</c> (src/Ffx.Atel.Cli, docs/sdk.md "The atel tool"): the .NET tool package installed from the
///     local feed as a modder installs it from nuget.org, into a tool path of the test's own, and run. Needs no game.
/// </summary>
[Collection(SdkFeedCollection.Name)]
public sealed class CliToolTests(SdkFeed sdk) {
    [Fact]
    public void ThePackageIsADotnetToolNamedAtel() {
        using ZipArchive zip = ZipFile.OpenRead(sdk.CliPackage);
        using StreamReader nuspec = new(zip.GetEntry("AtelSharp.Cli.nuspec")!.Open());
        string text = nuspec.ReadToEnd();
        Assert.Contains("<packageType name=\"DotnetTool\" />", text, StringComparison.Ordinal);
        Assert.Contains("<license type=\"expression\">MIT</license>", text, StringComparison.Ordinal);
        Assert.Contains("<readme>README.md</readme>", text, StringComparison.Ordinal);
        using StreamReader settings = new(zip.GetEntry("tools/net10.0/any/DotnetToolSettings.xml")!.Open());
        Assert.Contains("<Command Name=\"atel\" EntryPoint=\"atel.dll\"", settings.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolInstallsAndRuns() {
        string toolPath = Path.Combine(sdk.Root, "tools");
        // The feed's nuget.config in Root clears every other source: the tool comes from the feed or not at all.
        DotNet.Run(sdk.Root, sdk.Env, ["tool", "install", "AtelSharp.Cli", "--version", sdk.Version, "--tool-path", toolPath, "--add-source", sdk.Feed]);
        string atel = Path.Combine(toolPath, OperatingSystem.IsWindows() ? "atel.exe" : "atel");
        Assert.True(File.Exists(atel), $"no {atel}");
        // The launcher finds the runtime through DOTNET_ROOT: the one running the tests, as a modder's PATH would give it.
        Dictionary<string, string?> env = new(sdk.Env);
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host) env["DOTNET_ROOT"] = Path.GetDirectoryName(host);
        string output = DotNet.Exec(atel, sdk.Root, env, ["--help"]);
        Assert.Contains("usage: atel <command>", output, StringComparison.Ordinal);
        Assert.Contains("decompile", output, StringComparison.Ordinal);
    }
}
