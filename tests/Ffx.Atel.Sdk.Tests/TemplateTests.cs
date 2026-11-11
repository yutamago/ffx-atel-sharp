// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.RegularExpressions;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Layout;

namespace Ffx.Atel.Sdk.Tests;

/// <summary>
///     <c>dotnet new atel-mod</c> (src/Ffx.Atel.Templates, docs/sdk.md): the template's namespace steps against the
///     decompiler's names, the template installed from the local feed and used as a modder would, and (with the game)
///     a mod made by it built against the decompiled m209 into a package, following the README the template writes.
/// </summary>
[Collection(SdkFeedCollection.Name)]
public sealed class TemplateTests(SdkFeed sdk) {
    private static readonly string TemplateJson =
        Path.Combine(SdkFeed.RepoRoot(), "src", "Ffx.Atel.Templates", "content", "atel-mod", ".template.config", "template.json");

    /// <summary>The steps of a regex symbol, applied as the template engine's RegexMacro does: in order, each on the last's result.</summary>
    private static Func<string, string> Symbol(string symbol) {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(TemplateJson));
        JsonElement parameters = doc.RootElement.GetProperty("symbols").GetProperty(symbol).GetProperty("parameters");
        Assert.Equal("scriptName", parameters.GetProperty("source").GetString());
        List<(string Regex, string Replacement)> steps = [.. parameters.GetProperty("steps").EnumerateArray()
            .Select(s => (s.GetProperty("regex").GetString()!, s.GetProperty("replacement").GetString()!))];
        return name => steps.Aggregate(name.ToLowerInvariant(), (value, step) => Regex.Replace(value, step.Regex, step.Replacement));
    }

    /// <summary>The template's namespace, class and folder of a script, against the decompiler's (<see cref="SourceNames.ForPath" />).</summary>
    private static List<string> Mismatches(IEnumerable<string> archivePaths) {
        Func<string, string> ns = Symbol("scriptNamespace"), cls = Symbol("scriptClass"), folder = Symbol("scriptFolder");
        List<string> wrong = [];
        foreach (string path in archivePaths) {
            SourceNames names = SourceNames.ForPath(path);
            (string, string, string) got = (ns(names.Name), cls(names.Name), folder(names.Name));
            (string, string, string) want = (names.Namespace, names.ClassName, names.Namespace.Replace('.', '/'));
            if (got != want) wrong.Add($"{names.Name}: {got} instead of {want}");
        }
        return wrong;
    }

    [Fact]
    public void TheTemplateNamesEveryAreaAsTheDecompilerDoes() {
        // MSBuild on Linux and macOS turns a backslash into a slash when the pack writes the file.
        Assert.DoesNotContain('\\', File.ReadAllText(TemplateJson));
        List<string> paths = [
            GameData.MonsterPrefix + "_m001/m001.bin", GameData.MonsterPrefix + "_m209/m209.bin",
            // Areas without an English name, other builds' copies, debug variants, the odd battle names.
            GameData.EventPrefix + "obj/te/te0100/te0100.ebp", GameData.EventPrefix + "obj/bl/bltz0000/cn_bltz0000.ebp",
            GameData.EventPrefix + "obj/bl/bltz0000/psvcn_bltz0000.ebp", GameData.EventPrefix + "obj/ka/kami0400/200thunder_kami0400.ebp",
            GameData.BattlePrefix + "xyzw01_02/xyzw01_02.bin", GameData.BattlePrefix + "zzzz00_136/zzzz00_136.bin",
            GameData.BattlePrefix + "system_01/system_01.bin",
        ];
        foreach (string group in AreaNames.EventAreas.Keys) paths.Add($"{GameData.EventPrefix}obj/{group}/{group}xy0100/{group}xy0100.ebp");
        foreach (string area in AreaNames.BattleAreas.Keys) paths.Add($"{GameData.BattlePrefix}{area}03_12/{area}03_12.bin");
        Assert.Empty(Mismatches(paths));
    }

    [Fact]
    public void TheTemplateNamesEveryScriptAsTheDecompilerDoes() {
        string? gameDir = GameData.GameDir();
        if (gameDir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        using VbfArchive archive = GameData.OpenArchive(gameDir!);
        List<string> paths = GameData.AllScriptPaths(archive);
        Assert.Equal(1621, paths.Count);
        Assert.Empty(Mismatches(paths));
    }

    /// <summary>A custom hive: the install stays in the test's folder, not the user's templates.</summary>
    private string Install() {
        string hive = Path.Combine(sdk.Root, "hive");
        if (!Directory.Exists(hive)) DotNet.Run(sdk.Root, sdk.Env, ["new", "install", sdk.TemplatesPackage, "--debug:custom-hive", hive]);
        return hive;
    }

    [Fact]
    public void TheTemplateWritesAModProject() {
        string hive = Install();
        string dir = Path.Combine(sdk.Root, "new", "Thunder");
        DotNet.Run(sdk.Root, sdk.Env, ["new", "atel-mod", "-n", "Thunder Mod", "--script", "kami0000", "--authors", "Someone", "-o", dir, "--debug:custom-hive", hive]);

        Assert.Equal([".gitignore", "Kami0000.cs", "LICENSE", "README.md", "Thunder Mod.csproj", "global.json", "mod.toml", "nuget.config"], DotNet.Files(dir));
        string cs = File.ReadAllText(Path.Combine(dir, "Kami0000.cs"));
        // MIT by default (--license): the licence file with the year and the authors, an SPDX header, the project's expression.
        Assert.StartsWith("// SPDX-License-Identifier: MIT\n\nusing Ffx;", cs.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        string license = File.ReadAllText(Path.Combine(dir, "LICENSE"));
        Assert.StartsWith("MIT License", license, StringComparison.Ordinal);
        Assert.Contains($"Copyright (c) {DateTime.Now.Year} Someone", license, StringComparison.Ordinal);
        Assert.Contains("namespace Events.Ka_ThunderPlains.Kami0000;", cs, StringComparison.Ordinal);
        Assert.Contains("using static Events.Ka_ThunderPlains.Kami0000.Kami0000;", cs, StringComparison.Ordinal);
        Assert.Contains("public static partial class Kami0000", cs, StringComparison.Ordinal);
        Assert.Contains("public static int Thunder_ModCounter;", cs, StringComparison.Ordinal);
        string project = File.ReadAllText(Path.Combine(dir, "Thunder Mod.csproj"));
        Assert.Contains("<AtelModId>someone.thundermod</AtelModId>", project, StringComparison.Ordinal);
        Assert.Contains("<AtelModName>Thunder Mod</AtelModName>", project, StringComparison.Ordinal);
        Assert.Contains("<Authors>Someone</Authors>", project, StringComparison.Ordinal);
        Assert.Contains(">../atel-vanilla</AtelVanillaSource>", project, StringComparison.Ordinal);
        Assert.Contains("<PackageLicenseExpression>MIT</PackageLicenseExpression>", project, StringComparison.Ordinal);
        using (JsonDocument global = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "global.json")))) {
            Assert.Equal(sdk.Version, global.RootElement.GetProperty("msbuild-sdks").GetProperty("AtelSharp.Sdk").GetString());
        }
        Assert.Contains("value=\"../atel-feed\"", File.ReadAllText(Path.Combine(dir, "nuget.config")), StringComparison.Ordinal);
        string readme = File.ReadAllText(Path.Combine(dir, "README.md"));
        Assert.Contains($"dotnet tool install -g AtelSharp.Cli --version {sdk.Version}", readme, StringComparison.Ordinal);
        Assert.Contains("atel decompile kami0000 -o ../atel-vanilla", readme, StringComparison.Ordinal);
        Assert.Contains($"atelsharp.sdk/{sdk.Version}/tools/atel/atel.dll", readme, StringComparison.Ordinal);
        // No token of the template is left.
        foreach (string f in Directory.GetFiles(dir)) Assert.DoesNotMatch(LeftoverTokens, File.ReadAllText(f));
    }

    /// <summary>The template's placeholders and conditions, none of which may reach a mod.</summary>
    private const string LeftoverTokens =
        "Atel(Script|ModIdentifier)|atel-(script-name|mod-|sdk-version|vanilla-dir|feed-dir|license-)|ModTemplateName|@AtelSdkVersion@|#if|#endif|mitLicense";

    [Fact]
    public void TheLicenceIsAnOption() {
        string hive = Install();
        // Without --authors, the copyright holder is "the authors of this mod", not the default author "you".
        string mit = Path.Combine(sdk.Root, "new", "DefaultAuthors");
        DotNet.Run(sdk.Root, sdk.Env, ["new", "atel-mod", "-n", "DefaultAuthors", "-o", mit, "--debug:custom-hive", hive]);
        Assert.Contains($"Copyright (c) {DateTime.Now.Year} the authors of this mod", File.ReadAllText(Path.Combine(mit, "LICENSE")), StringComparison.Ordinal);

        string none = Path.Combine(sdk.Root, "new", "Unlicensed");
        DotNet.Run(sdk.Root, sdk.Env, ["new", "atel-mod", "-n", "Unlicensed", "--license", "none", "-o", none, "--debug:custom-hive", hive]);
        Assert.Equal([".gitignore", "M209.cs", "README.md", "Unlicensed.csproj", "global.json", "mod.toml", "nuget.config"], DotNet.Files(none));
        Assert.StartsWith("using Ffx;", File.ReadAllText(Path.Combine(none, "M209.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("License", File.ReadAllText(Path.Combine(none, "Unlicensed.csproj")), StringComparison.Ordinal);
        Assert.DoesNotContain("## Licence", File.ReadAllText(Path.Combine(none, "README.md")), StringComparison.Ordinal);
        foreach (string f in Directory.GetFiles(none)) Assert.DoesNotMatch(LeftoverTokens, File.ReadAllText(f));
    }

    [Fact]
    public void AModFromTheTemplateBuildsToAPackage() {
        if (GameData.GameDir() is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        string hive = Install();
        string work = Directory.CreateDirectory(Path.Combine(sdk.Root, "template-mod")).FullName;
        string mod = Path.Combine(work, "LarvaMod");
        DotNet.Run(work, sdk.Env, ["new", "atel-mod", "-n", "LarvaMod", "--script", "m209", "--feed", sdk.Feed, "--debug:custom-hive", hive]);

        // The README's steps: restore (the SDK package into NuGet's folder), decompile with its atel beside the mod, build.
        DotNet.Run(mod, sdk.Env, ["restore", .. DotNet.BuildFlags]);
        string atel = Path.Combine(sdk.Env["NUGET_PACKAGES"]!, "atelsharp.sdk", sdk.Version, "tools", "atel", "atel.dll");
        DotNet.Run(mod, sdk.Env, [atel, "decompile", "m209", "-o", "../atel-vanilla"]);
        Assert.True(File.Exists(Path.Combine(work, "atel-vanilla", "Monsters", "M209", "M209.cs")));
        DotNet.Run(mod, sdk.Env, ["build", .. DotNet.BuildFlags]);

        string built = Path.Combine(mod, "bin", "Debug", "net10.0", "mods", "you.larvamod");
        string package = Path.Combine("atel", "ffx_ps2", "ffx", "master", "jppc", "battle", "mon", "_m209", "m209.bin.json");
        Assert.Equal([package, "mod.toml", "you.larvamod.manifest.json"], DotNet.Files(built));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(built, package)));
        Assert.Equal("m209", json.RootElement.GetProperty("script").GetString());
        Assert.Equal("LarvaModCounter", Assert.Single(json.RootElement.GetProperty("variables").EnumerateArray()).GetProperty("name").GetString());
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(built, "you.larvamod.manifest.json")));
        Assert.Equal("you.larvamod", manifest.RootElement.GetProperty("Id").GetString());
        Assert.Empty(Directory.GetFiles(Path.Combine(mod, "bin"), "*.dll", SearchOption.AllDirectories));
    }
}
