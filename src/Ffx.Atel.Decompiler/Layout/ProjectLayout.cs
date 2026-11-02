// SPDX-License-Identifier: MIT

using System.Text;
using Ffx.Atel.Bytecode;

namespace Ffx.Atel.Decompiler.Layout;

/// <summary>
///     The projects of a decompiled source tree (docs/plan.md, "Phase 6: source layout"): which scripts
///     one project holds, the <c>.csproj</c> files, <c>Directory.Build.props</c> with the reference to
///     <c>Ffx.Atel.Game</c>, and a solution, so the tree opens in Rider, Visual Studio and VS Code and
///     builds with <c>dotnet build</c>.
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
    ///     The root's files for the given project folders: <c>Directory.Build.props</c>, the solution,
    ///     <c>.gitignore</c> and every project. <paramref name="libraryFolders"/> are projects every other
    ///     project references (the shared worker classes).
    /// </summary>
    public static List<SourceFile> Files(IEnumerable<string> projectFolders, IReadOnlyCollection<string>? libraryFolders = null) {
        libraryFolders ??= [];
        List<string> projects = [.. projectFolders.Concat(libraryFolders).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        List<SourceFile> files = [
            new("Directory.Build.props", BuildProps),
            new(".gitignore", "bin/\nobj/\n" + ToolFolder + "/\n"),
        ];
        foreach (string p in projects) {
            List<string> references = libraryFolders.Contains(p) ? [] : [.. libraryFolders.Select(l => Relative(p, ProjectFile(l)))];
            files.Add(new(ProjectFile(p), Project(p, references)));
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

    private static string Project(string folder, IReadOnlyList<string> references) {
        StringBuilder sb = new("<Project Sdk=\"Microsoft.NET.Sdk\">\n");
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
    ///     <c>$(AtelGameProject)</c> set, from that project (a checkout of ffx-atel-sharp). Phase 8
    ///     replaces both with the SDK package.
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
                <!-- CS0162: the original kept unreachable code (if (false) { … }, code after return). -->
                <NoWarn>$(NoWarn);CS0162</NoWarn>
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
}
