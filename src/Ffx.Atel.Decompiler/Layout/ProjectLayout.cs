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
///     as one project, blitzball a project per script, and the variants (<see cref="IsVariant"/>: copies for other
///     builds, debug and test scripts) apart from the scripts the game loads (see <see cref="Assign"/>). Its
///     <c>.cs</c> files are found by the SDK's default glob, less the folders of projects inside it. Besides
///     <c>Atel.slnx</c> with every project but the single-script ones (blitzball, in <c>Blitzball.slnx</c>) and the
///     variants (in <c>Variants.slnx</c>), <c>Events.slnx</c>, <c>Battles.slnx</c> and <c>Monsters.slnx</c> hold one
///     category each, and <c>Start.slnx</c> a small set to begin with: the whole corpus at once needs about 22 GB to
///     compile. <c>.vscode/</c> recommends C# Dev Kit and opens <c>Start.slnx</c>.
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
    ///     <c>Events/Bl/Bltz0000</c>) and for a variant (<see cref="IsVariant"/>) in an area with other scripts
    ///     (<c>Events/Ka_ThunderPlains/_200thunder_kami0400</c>), so that the area's project holds only what the game
    ///     loads. An area of variants only (<c>Events/Te</c>) stays one project. The scripts keep their folders.
    /// </summary>
    public static Dictionary<string, string> Assign(IEnumerable<(SourceNames Names, long Size)> scripts) {
        List<(SourceNames Names, long Size)> all = [.. scripts];
        Dictionary<string, long> areaSizes = all.GroupBy(s => AreaFolder(s.Names), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(s => s.Size), StringComparer.Ordinal);
        HashSet<string> mixed = [.. all.GroupBy(s => AreaFolder(s.Names), StringComparer.Ordinal).Where(g => g.Any(s => IsVariant(s.Names)) && !g.All(s => IsVariant(s.Names))).Select(g => g.Key)];
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach ((SourceNames names, _) in all) {
            string area = AreaFolder(names);
            bool own = names.Category != ScriptCategory.Monster && (areaSizes[area] > AreaLimit || mixed.Contains(area) && IsVariant(names));
            result[names.Namespace] = own ? SourceLayout.Folder(names) : area;
        }
        return result;
    }

    /// <summary>
    ///     A script that is not part of the game as the PC build plays it: one the PC build does not load or loads
    ///     only behind a debug switch (the copies for other builds, <c>cn_</c>, <c>psv_</c>, <c>psvcn_</c>, and
    ///     <c>testpub0</c>, <c>200thunder_kami0400</c>, <c>dbg_nagi0000</c>, <c>full_nagi0700</c>: those with a note in
    ///     <c>data/areas.json</c>), and the developers' test maps and battles (<c>test*</c>, <c>camtest0</c>,
    ///     <c>soundtest</c>, <c>sample*</c>). Their projects are in <see cref="VariantSolution"/> only.
    /// </summary>
    public static bool IsVariant(SourceNames names) =>
        AreaNames.ScriptNote(names.Name) is not null
        || names.Name.Contains("test", StringComparison.OrdinalIgnoreCase)
        || names.Name.StartsWith("sample", StringComparison.OrdinalIgnoreCase);

    /// <summary>The project folders (<see cref="Assign"/>) that hold variants (<see cref="IsVariant"/>), which hold nothing else.</summary>
    public static HashSet<string> VariantFolders(IEnumerable<SourceNames> scripts, IReadOnlyDictionary<string, string> projects) {
        List<SourceNames> all = [.. scripts];
        HashSet<string> loaded = [.. all.Where(n => !IsVariant(n)).Select(n => projects[n.Namespace])];
        return [.. all.Where(IsVariant).Select(n => projects[n.Namespace]).Where(f => !loaded.Contains(f))];
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
    public static List<SourceFile> Files(IEnumerable<string> projectFolders, IReadOnlyCollection<string>? libraryFolders = null, IReadOnlySet<string>? variantFolders = null) =>
        Files(projectFolders, libraryFolders, GameReference.Assembly, null, localFeed: false, variantFolders);

    /// <summary>
    ///     The root's files for the given project folders: <c>Directory.Build.props</c>, the solution,
    ///     <c>.gitignore</c> and every project; with <see cref="GameReference.Sdk"/> also <c>global.json</c>
    ///     (the SDK's version, <paramref name="sdkVersion"/>) and, with <paramref name="localFeed"/>, a
    ///     <c>nuget.config</c> naming <c>.atel/packages/</c> as a package source. <paramref name="libraryFolders"/>
    ///     are projects every other project references (the shared worker classes); <paramref name="variantFolders"/>
    ///     the projects of variants (<see cref="VariantFolders"/>), which only <see cref="VariantSolution"/> holds.
    /// </summary>
    public static List<SourceFile> Files(IEnumerable<string> projectFolders, IReadOnlyCollection<string>? libraryFolders, GameReference reference,
                                         string? sdkVersion, bool localFeed, IReadOnlySet<string>? variantFolders = null) {
        libraryFolders ??= [];
        variantFolders ??= new HashSet<string>();
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
            // A project inside this one's folder (a variant in its area) keeps its files to itself.
            List<string> inner = [.. projects.Where(q => q.StartsWith(p + "/", StringComparison.Ordinal)).Select(q => q[(p.Length + 1)..])];
            files.Add(new(ProjectFile(p), Project(p, references, sdk ? SdkPackage : "Microsoft.NET.Sdk", inner)));
        }
        // The variants (other builds' copies, debug and test scripts) stay out of the default solutions:
        // Variants.slnx holds them. So do the other projects of a single script (blitzball, about 2 GB each to
        // compile): Blitzball.slnx holds them.
        List<string> variants = [.. projects.Where(variantFolders.Contains)];
        List<string> big = [.. projects.Where(IsScriptProject).Except(variants)];
        List<string> normal = [.. projects.Except(big).Except(variants)];
        files.Add(new(Solution, SolutionFile(normal)));
        foreach (IGrouping<string, string> category in normal.Where(p => !libraryFolders.Contains(p)).GroupBy(p => p.Split('/')[0], StringComparer.Ordinal)) {
            files.Add(new(category.Key + ".slnx", SolutionFile([.. category, .. libraryFolders])));
        }
        if (big.Count > 0) files.Add(new(BigSolution, SolutionFile([.. big, .. libraryFolders])));
        if (variants.Count > 0) files.Add(new(VariantSolution, SolutionFile([.. variants, .. libraryFolders])));
        // A small solution to start with: the Thunder Plains (the tutorial's Larva lives there) and the monsters.
        List<string> start = [.. normal.Where(p => libraryFolders.Contains(p) || p == "Monsters" || StartAreas.Any(a => p.StartsWith(a, StringComparison.Ordinal)))];
        if (start.Count > libraryFolders.Count) files.Add(new(StartSolution, SolutionFile(start)));
        files.Add(new(".vscode/extensions.json", VsCodeExtensions));
        files.Add(new(".vscode/settings.json", VsCodeSettings(start.Count > libraryFolders.Count ? StartSolution : Solution)));
        return files;
    }

    /// <summary>The solution with the projects of single scripts (<see cref="AreaLimit"/>): blitzball.</summary>
    public const string BigSolution = "Blitzball.slnx";

    /// <summary>The solution with the variants' projects (<see cref="IsVariant"/>), blitzball's among them.</summary>
    public const string VariantSolution = "Variants.slnx";

    /// <summary>The small solution to open first.</summary>
    public const string StartSolution = "Start.slnx";

    /// <summary>The areas <see cref="StartSolution"/> holds besides the monsters.</summary>
    private static readonly string[] StartAreas = ["Events/Ka_", "Battles/Kami_"];

    /// <summary>A project of one script (<c>Events/Bl_Blitzball/Bltz0000</c>) rather than of an area.</summary>
    private static bool IsScriptProject(string folder) => folder.Split('/').Length >= 3;

    private const string VsCodeExtensions = """
        {
          "recommendations": [
            "ms-dotnettools.csdevkit"
          ]
        }

        """;

    private static string VsCodeSettings(string solution) => $$"""
        {
          "dotnet.defaultSolution": "{{solution}}",
          "files.exclude": {
            "**/bin": true,
            "**/obj": true
          },
          "search.exclude": {
            ".atel": true
          }
        }

        """;

    private static string SolutionFile(IEnumerable<string> projects) {
        StringBuilder sln = new("<Solution>\n");
        foreach (string p in projects.Order(StringComparer.Ordinal)) sln.Append("  <Project Path=\"").Append(ProjectFile(p)).Append("\" />\n");
        return sln.Append("</Solution>\n").ToString();
    }

    private static string Relative(string fromFolder, string toFile) =>
        string.Concat(Enumerable.Repeat("../", fromFolder.Split('/').Length)) + toFile;

    private static string Project(string folder, IReadOnlyList<string> references, string sdk, IReadOnlyList<string> inner) {
        StringBuilder sb = new($"<Project Sdk=\"{sdk}\">\n");
        sb.Append("    <!-- Atel source, decompiled by atel: ").Append(folder.Replace('/', '.')).Append(". -->\n");
        if (inner.Count > 0) {
            sb.Append("    <PropertyGroup>\n        <!-- Scripts with projects of their own (Variants.slnx, Blitzball.slnx). -->\n");
            sb.Append("        <DefaultItemExcludes>$(DefaultItemExcludes)");
            foreach (string i in inner.Order(StringComparer.Ordinal)) sb.Append(';').Append(i).Append("/**");
            sb.Append("</DefaultItemExcludes>\n    </PropertyGroup>\n");
        }
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
