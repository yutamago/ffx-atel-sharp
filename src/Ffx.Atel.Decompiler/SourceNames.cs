// SPDX-License-Identifier: MIT

using System.Text;
using Ffx.Atel.Bytecode;

namespace Ffx.Atel.Decompiler;

/// <summary>Where a script's source goes: its namespace, script class and file.</summary>
/// <param name="Namespace"><c>Events.Bj_BaajTemple.Bjyt0400</c>, <c>Monsters.M001</c>, <c>Battles.Cdsp_SalvageShip.Cdsp00_00</c>: the map code with
///     its English suffix where data/areas.json has one.</param>
/// <param name="ClassName">The script class: the file name with its first letter upper case.</param>
/// <param name="Attribute">The attribute that names the file: <c>Event</c>, <c>Monster</c> or <c>Battle</c>.</param>
public sealed record SourceNames(ScriptCategory Category, string Name, string Namespace, string ClassName, string Attribute) {
    /// <summary>The file, relative to a source root: the namespace as folders (<c>Events/Bj_BaajTemple/Bjyt0400/Bjyt0400.cs</c>).</summary>
    public string RelativePath => Path.Combine([.. Namespace.Split('.'), ClassName + ".cs"]);

    /// <summary>The names for a script's archive path.</summary>
    /// <exception cref="ArgumentException">Not an event, monster or battle script.</exception>
    public static SourceNames ForPath(string archivePath) {
        (ScriptCategory category, string name) = GameData.Identify(archivePath) ?? throw new ArgumentException($"not a script path: {archivePath}", nameof(archivePath));
        string cls = Identifier(name);
        return category switch {
            // event/obj/<group>/<map>/<name>.ebp: the two-letter group is the middle namespace.
            ScriptCategory.Event => new(category, name, $"Events.{EventFolder(archivePath[GameData.EventPrefix.Length..].Split('/')[1])}.{cls}", cls, "Event"),
            ScriptCategory.Monster => new(category, name, $"Monsters.{cls}", cls, "Monster"),
            // battle/btl/<name>/<name>.bin: the name starts with the map area (kino00_00, zzzz03_12).
            _ => new(category, name, $"Battles.{BattleFolder(BattleArea(cls))}.{cls}", cls, "Battle"),
        };
    }

    /// <summary>An event folder with its English suffix (<c>ka</c> → <c>Ka_ThunderPlains</c>, data/areas.json), else the bare code (<c>Te</c>).</summary>
    private static string EventFolder(string group) =>
        Layout.AreaNames.EventArea(group) is string english ? $"{Identifier(group)}_{english}" : Identifier(group);

    /// <summary>A battle area with its English suffix (<c>Kami</c> → <c>Kami_ThunderPlains</c>), else the bare code (<c>Zzzz</c>).</summary>
    private static string BattleFolder(string area) =>
        Layout.AreaNames.BattleArea(area) is string english ? $"{area}_{english}" : area;

    /// <summary>The letters a battle script's name starts with, its map area: <c>Kino00_00</c> → <c>Kino</c>.</summary>
    private static string BattleArea(string cls) {
        int n = 0;
        while (n < cls.Length && char.IsAsciiLetter(cls[n])) n++;
        return n > 0 ? cls[..n] : "Other";
    }

    /// <summary><c>bjyt0400</c> → <c>Bjyt0400</c>, <c>200thunder_kami0400</c> → <c>_200thunder_kami0400</c>.</summary>
    public static string Identifier(string name) {
        StringBuilder sb = new();
        foreach (char c in name) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        if (sb.Length == 0 || char.IsAsciiDigit(sb[0])) sb.Insert(0, '_');
        else sb[0] = char.ToUpperInvariant(sb[0]);
        return sb.ToString();
    }
}
