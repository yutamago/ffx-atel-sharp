// SPDX-License-Identifier: MIT

using System.Text;
using Ffx.Atel.Bytecode;

namespace Ffx.Atel.Decompiler.Layout;

/// <summary>How a decompiled tree's projects get <c>Ffx.Atel.Game</c> and the analyzers.</summary>
public enum GameReference {
    /// <summary>
    ///     The projects use the SDK package (<c>&lt;Project Sdk="Ffx.Atel.Sdk"&gt;</c>, its version in
    ///     <c>global.json</c>): reference assembly, analyzers and settings (docs/sdk.md).
    /// </summary>
    Sdk,

    /// <summary>
    ///     Plain <c>Microsoft.NET.Sdk</c> projects referencing <c>.atel/Ffx.Atel.Game.dll</c>, or with
    ///     <c>-p:AtelGameProject=…</c> a checkout of ffx-atel-sharp; no analyzers. Needs no package.
    /// </summary>
    Assembly,
}

/// <summary>
///     The projects of a decompiled source tree (docs/plan.md, "Phase 6: source layout"): which scripts
///     one project holds, the <c>.csproj</c> files, <c>Directory.Build.props</c>, and a solution, so the
///     tree opens in Rider, Visual Studio and VS Code and builds with <c>dotnet build</c>.
/// </summary>
/// <remarks>
///     A project is a folder: events and battles by map area (<c>Events/Bj/Events.Bj.csproj</c>), monsters
///     as one project, blitzball a project per script (see <see cref="Assign"/>). Its <c>.cs</c> files are
///     found by the SDK's default glob. Besides <c>Atel.slnx</c> with every project, <c>Events.slnx</c>,
///     <c>Battles.slnx</c> and <c>Monsters.slnx</c> hold one category each: the whole corpus at once
///     needs about 22 GB to compile.
/// </remarks>
public static class ProjectLayout {
    /// <summary>The folder the root files point at for the reference assembly, relative to the root.</summary>
    public const string ToolFolder = ".atel";

    /// <summary>The reference assembly's file name.</summary>
    public const string GameAssembly = "Ffx.Atel.Game.dll";

    /// <summary>The SDK package's id (<see cref="GameReference.Sdk"/>).</summary>
    public const string SdkPackage = "Ffx.Atel.Sdk";

    /// <summary>The folder under <see cref="ToolFolder"/> that is a local NuGet source holding the SDK package.</summary>
    public const string PackageFolder = "packages";

    /// <summary>The solution file at the root.</summary>
    public const string Solution = "Atel.slnx";

    /// <summary>An area with more source than this (characters) gets a project per script.</summary>
    public const long AreaLimit = 64_000_000;

    /// <summary>
    ///     The project folder of each script (by namespace), relative to the root: a project per map area
    ///     (<c>Events/Bj</c>, <c>Battles/Kino</c>), all monsters in one (<c>Monsters</c>), and a project per
    ///     script in an area of more than <see cref="AreaLimit"/> characters of source (blitzball,
    ///     <c>Events/Bl/Bltz0000</c>).
    /// </summary>
    public static Dictionary<string, string> Assign(IEnumerable<(SourceNames Names, long Size)> scripts) {
        List<(SourceNames Names, long Size)> all = [.. scripts];
        Dictionary<string, long> areaSizes = all.GroupBy(s => AreaFolder(s.Names), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(s => s.Size), StringComparer.Ordinal);
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach ((SourceNames names, _) in all) {
            string area = AreaFolder(names);
            result[names.Namespace] = names.Category != ScriptCategory.Monster && areaSizes[area] > AreaLimit ? SourceLayout.Folder(names) : area;
        }
        return result;
    }

    /// <summary>The map area's folder: <c>Events/Bj</c>, <c>Battles/Kino</c>; <c>Monsters</c> for every monster.</summary>
    public static string AreaFolder(SourceNames names) =>
        names.Category == ScriptCategory.Monster ? "Monsters" : string.Join('/', names.Namespace.Split('.')[..2]);

    /// <summary>A project's file, relative to the root: <c>Events/Bj/Events.Bj.csproj</c>.</summary>
    public static string ProjectFile(string folder) => $"{folder}/{folder.Replace('/', '.')}.csproj";

    /// <summary>
    ///     The root's files for the given project folders, with the reference assembly from
    ///     <c>.atel/</c> (<see cref="GameReference.Assembly"/>).
    /// </summary>
    public static List<SourceFile> Files(IEnumerable<string> projectFolders, IReadOnlyCollection<string>? libraryFolders = null) =>
        Files(projectFolders, libraryFolders, GameReference.Assembly, null, localFeed: false);

    /// <summary>
    ///     The root's files for the given project folders: <c>Directory.Build.props</c>, the solution,
    ///     <c>.gitignore</c> and every project; with <see cref="GameReference.Sdk"/> also <c>global.json</c>
    ///     (the SDK's version, <paramref name="sdkVersion"/>) and, with <paramref name="localFeed"/>, a
    ///     <c>nuget.config</c> naming <c>.atel/packages/</c> as a package source. <paramref name="libraryFolders"/>
    ///     are projects every other project references (the shared worker classes).
    /// </summary>
    public static List<SourceFile> Files(IEnumerable<string> projectFolders, IReadOnlyCollection<string>? libraryFolders, GameReference reference,
                                         string? sdkVersion, bool localFeed) {
        libraryFolders ??= [];
        List<string> projects = [.. projectFolders.Concat(libraryFolders).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        bool sdk = reference == GameReference.Sdk;
        if (sdk && string.IsNullOrEmpty(sdkVersion)) throw new ArgumentException("the SDK's version is needed", nameof(sdkVersion));
        List<SourceFile> files = [
            new("Directory.Build.props", sdk ? SdkBuildProps : BuildProps),
            new(".gitignore", "bin/\nobj/\n" + ToolFolder + "/\n"),
        ];
        if (sdk) {
            files.Add(new("global.json", GlobalJson(sdkVersion!)));
            if (localFeed) files.Add(new("nuget.config", NuGetConfig));
        }
        foreach (string p in projects) {
            List<string> references = libraryFolders.Contains(p) ? [] : [.. libraryFolders.Select(l => Relative(p, ProjectFile(l)))];
            files.Add(new(ProjectFile(p), Project(p, references, sdk ? SdkPackage : "Microsoft.NET.Sdk")));
        }
        files.Add(new(Solution, SolutionFile(projects)));
        foreach (IGrouping<string, string> category in projects.Where(p => !libraryFolders.Contains(p)).GroupBy(p => p.Split('/')[0], StringComparer.Ordinal)) {
            files.Add(new(category.Key + ".slnx", SolutionFile([.. category, .. libraryFolders])));
        }
        return files;
    }

    private static string SolutionFile(IEnumerable<string> projects) {
        StringBuilder sln = new("<Solution>\n");
        foreach (string p in projects.Order(StringComparer.Ordinal)) sln.Append("  <Project Path=\"").Append(ProjectFile(p)).Append("\" />\n");
        return sln.Append("</Solution>\n").ToString();
    }

    private static string Relative(string fromFolder, string toFile) =>
        string.Concat(Enumerable.Repeat("../", fromFolder.Split('/').Length)) + toFile;

    private static string Project(string folder, IReadOnlyList<string> references, string sdk) {
        StringBuilder sb = new($"<Project Sdk=\"{sdk}\">\n");
        sb.Append("    <!-- Atel source, decompiled by atel: ").Append(folder.Replace('/', '.')).Append(". -->\n");
        if (references.Count > 0) {
            sb.Append("    <ItemGroup>\n");
            foreach (string r in references) sb.Append("        <ProjectReference Include=\"").Append(r).Append("\" />\n");
            sb.Append("    </ItemGroup>\n");
        }
        sb.Append("</Project>\n");
        return sb.ToString();
    }

    /// <summary>
    ///     The settings every project shares. The reference assembly comes from <c>$(AtelGameAssembly)</c>
    ///     (by default the copy <c>atel decompile</c> puts in <c>.atel/</c>), or, with
    ///     <c>$(AtelGameProject)</c> set, from that project (a checkout of ffx-atel-sharp)
    ///     (<see cref="GameReference.Assembly"/>).
    /// </summary>
    public const string BuildProps = """
        <!-- Written by atel decompile. -->
        <Project>
            <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <LangVersion>latest</LangVersion>
                <Nullable>enable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
                <OutputType>Library</OutputType>
                <GenerateDocumentationFile>false</GenerateDocumentationFile>
                <!-- CS0162: the original kept unreachable code (if (false) { … }, code after return).
                     CS1998: a requested entry is async Task whether or not it awaits. -->
                <NoWarn>$(NoWarn);CS0162;CS1998</NoWarn>
                <!-- The reference assembly: a path to Ffx.Atel.Game.dll, or a project to build it from. -->
                <AtelGameAssembly Condition="'$(AtelGameAssembly)' == ''">$(MSBuildThisFileDirectory).atel/Ffx.Atel.Game.dll</AtelGameAssembly>
            </PropertyGroup>
            <ItemGroup Condition="'$(AtelGameProject)' != ''">
                <ProjectReference Include="$(AtelGameProject)" />
            </ItemGroup>
            <ItemGroup Condition="'$(AtelGameProject)' == ''">
                <Reference Include="Ffx.Atel.Game" HintPath="$(AtelGameAssembly)" />
            </ItemGroup>
        </Project>

        """;

    /// <summary>
    ///     The settings of the projects with <see cref="GameReference.Sdk"/>: the SDK has the rest
    ///     (net10.0, no implicit usings, CS0162 and CS1998 off, the reference assembly, the analyzers).
    /// </summary>
    public const string SdkBuildProps = """
        <!-- Written by atel decompile. The projects use Ffx.Atel.Sdk, its version in global.json. -->
        <Project>
            <PropertyGroup>
                <!-- Vanilla source: dotnet build checks the C# and writes no mod package. -->
                <AtelProjectKind>Vanilla</AtelProjectKind>
            </PropertyGroup>
        </Project>

        """;

    private static string GlobalJson(string version) => $$"""
        {
          "msbuild-sdks": {
            "{{SdkPackage}}": "{{version}}"
          }
        }

        """;

    /// <summary>A package source for <c>.atel/packages/</c>, where <c>atel decompile</c> put the SDK package.</summary>
    public const string NuGetConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <!-- Written by atel decompile: Ffx.Atel.Sdk from .atel/packages/. -->
        <configuration>
          <packageSources>
            <add key="atel" value=".atel/packages" />
          </packageSources>
        </configuration>

        """;
}
