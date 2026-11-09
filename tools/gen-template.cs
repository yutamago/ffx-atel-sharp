// SPDX-License-Identifier: MIT
//
// Writes the namespace steps of the `dotnet new atel-mod` template (src/Ffx.Atel.Templates/content/atel-mod/
// .template.config/template.json, symbol scriptNamespace): regex replacements the template engine applies in order
// (symbols scriptClass and scriptFolder: the same, giving the class or the folder)
// to the lower-case script name, giving the namespace `atel decompile` gives that script (SourceNames.ForPath):
// `m209` -> `Monsters.M209`, `kami00_00` -> `Battles.Kami_ThunderPlains.Kami00_00`, `kami0000` ->
// `Events.Ka_ThunderPlains.Kami0000`. The areas' English names come from data/areas.json (AreaNames); an area
// without one keeps its bare code. Each step's result starts with an upper-case letter, so no later step (all of
// them anchored on a lower-case one) matches it again. tests/Ffx.Atel.Sdk.Tests checks the steps against the
// decompiler for every area, and with the game for every script. docs/sdk.md, "dotnet new atel-mod".
//
// usage: dotnet run tools/gen-template.cs

#:project ../src/Ffx.Atel.Decompiler
#:property PublishAot=false

using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ffx.Atel.Decompiler.Layout;

string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, ".."));
string file = Path.Combine(root, "src", "Ffx.Atel.Templates", "content", "atel-mod", ".template.config", "template.json");

// The steps run in order on one value. First the name becomes `Root|area|name` (`Events|ka|kami0000`; a monster has no
// area: `Monsters||m209`), then the area gets its English name (`Events|ka_ThunderPlains|kami0000`), every part its
// upper-case first letter (a name that starts with a digit an underscore, `_200thunder_kami0400`), and last the
// parts are joined: with dots for the namespace, with slashes for the folder, or only the last part for the class.
// No backslashes ([|], not \\|): MSBuild turns them into slashes when the pack writes the file (Ffx.Atel.Templates.csproj).
JsonArray Steps(params (string Regex, string Replacement)[] join) {
    JsonArray steps = [];
    void Step(string regex, string replacement) => steps.Add(new JsonObject { ["regex"] = regex, ["replacement"] = replacement });
    // SourceNames.Identifier: anything but a letter or digit is '_'.
    Step("[^a-z0-9]", "_");
    // Monsters: battle/mon/_m209/m209.bin.
    Step("^m([0-9]+)$", "Monsters||m$1");
    // Battles: battle/btl/kami00_00/kami00_00.bin (also zzzz00_136, system_01); the area is the letters the name starts with.
    Step("^([a-z]+)([0-9]*_[0-9]+)$", "Battles|$1|$1$2");
    // Events: event/obj/ka/kami0000/kami0000.ebp, the group the name's first two letters; for the copies of other
    // builds and the debug variants (cn_bltz0000, psv_hiku2100, 200thunder_kami0400), those of the name after the prefix.
    Step("^([a-z0-9]+_)([a-z][a-z0-9])([a-z0-9]*)$", "Events|$2|$1$2$3");
    Step("^([a-z][a-z0-9])([a-z0-9_]*)$", "Events|$1|$1$2");
    foreach ((string area, string english) in AreaNames.BattleAreas.OrderBy(a => a.Key, StringComparer.Ordinal))
        Step($"^Battles[|]{area}[|]", $"Battles|{area}_{english}|");
    foreach ((string group, string english) in AreaNames.EventAreas.OrderBy(a => a.Key, StringComparer.Ordinal))
        Step($"^Events[|]{group}[|]", $"Events|{group}_{english}|");
    for (char c = 'a'; c <= 'z'; c++) Step($"[|]{c}", $"|{char.ToUpperInvariant(c)}");
    Step("[|]([0-9][^|]*)$", "|_$1");
    foreach ((string regex, string replacement) in join) Step(regex, replacement);
    return steps;
}

JsonNode template = JsonNode.Parse(File.ReadAllText(file))!;
JsonNode symbols = template["symbols"]!;
JsonArray ns = Steps(("[|][|]", "."), ("[|]", "."));
symbols["scriptNamespace"]!["parameters"]!["steps"] = ns;
symbols["scriptClass"]!["parameters"]!["steps"] = Steps(("^.*[|]", ""));
symbols["scriptFolder"]!["parameters"]!["steps"] = Steps(("[|][|]", "/"), ("[|]", "/"));
File.WriteAllText(file, template.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
Console.WriteLine($"{ns.Count} steps -> {Path.GetRelativePath(root, file)}");
return 0;

static string ThisFile([CallerFilePath] string path = "") => path;
