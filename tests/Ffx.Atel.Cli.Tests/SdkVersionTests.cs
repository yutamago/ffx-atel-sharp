// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Ffx.Atel.Cli.Tests;

/// <summary>
///     The SDK version atel pins (global.json) and looks up (<c>Ffx.Atel.Sdk.&lt;version&gt;.nupkg</c>): this build's
///     version, one per commit (Directory.Build.targets, docs/sdk.md "Versions").
/// </summary>
public sealed partial class SdkVersionTests {
    [Theory]
    [InlineData("0.1.0-alpha.0.117.g5e8c870545+5e8c870545e71c6c2d5070f978b0382239aaa99e", "0.1.0-alpha.0.117.g5e8c870545")]
    [InlineData("0.1.0-alpha.1+5e8c870545e71c6c2d5070f978b0382239aaa99e", "0.1.0-alpha.1")]
    [InlineData("0.1.0-alpha.1", "0.1.0-alpha.1")]
    [InlineData(null, "0.0.0")]
    [InlineData("", "0.0.0")]
    public void SdkVersionDropsTheBuildMetadata(string? informational, string expected) =>
        Assert.Equal(expected, SourceCommands.SdkVersion(informational));

    [Fact]
    public void ThisBuildHasTheVersionOfItsCommit() {
        string informational = typeof(SourceCommands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        string version = SourceCommands.SdkVersion();
        Assert.StartsWith(version + "+", informational, StringComparison.Ordinal);
        // An explicit -p:Version=<version> (the release workflow) has none of this.
        if (CommitVersion().Match(version) is not { Success: true } m) return;
        string commit = informational[(version.Length + 1)..];
        Assert.Matches("^[0-9a-f]{40}$", commit);
        Assert.Equal(commit[..10], m.Groups["commit"].Value);
        // When the test runs in the checkout it was built from: the count is that commit's.
        if (Git("rev-parse", "HEAD") == commit) Assert.Equal(Git("rev-list", "--count", "HEAD"), m.Groups["count"].Value);
    }

    [Fact]
    public void FindSdkPackageTakesTheGivenFileOrFolder() {
        string dir = Directory.CreateTempSubdirectory("atel-cli-test-").FullName;
        try {
            const string version = "0.1.0-alpha.0.12.gabcdef0123";
            // NuGet's cache names the file in lower case.
            string package = Path.Combine(dir, $"ffx.atel.sdk.{version}.nupkg");
            File.WriteAllBytes(package, []);
            File.WriteAllBytes(Path.Combine(dir, "Ffx.Atel.Sdk.0.1.0-alpha.0.11.g0123456789.nupkg"), []);
            Assert.Equal(package, SourceCommands.FindSdkPackage(dir, version));
            Assert.Equal(package, SourceCommands.FindSdkPackage(package, version));
            // Another commit's package is not this atel's.
            Assert.Null(SourceCommands.FindSdkPackage(dir, "0.1.0-alpha.0.13.gfedcba9876"));
            Assert.Null(SourceCommands.FindSdkPackage(Path.Combine(dir, "missing"), version));
        }
        finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [GeneratedRegex(@"^0\.1\.0-alpha\.0\.(?<count>[0-9]+)\.g(?<commit>[0-9a-f]{10})$")]
    private static partial Regex CommitVersion();

    /// <summary>git's answer in the test's folder, or null without git or outside a clone.</summary>
    private static string? Git(params string[] args) {
        try {
            ProcessStartInfo start = new("git") { WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in args) start.ArgumentList.Add(a);
            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception) {
            return null;
        }
    }
}
