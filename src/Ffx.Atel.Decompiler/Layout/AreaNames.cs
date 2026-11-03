// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace Ffx.Atel.Decompiler.Layout;

/// <summary>
///     The hand-written area table, <c>data/areas.json</c> (docs/plan.md, Decisions 2026-11-02: folders are the
///     map code plus an English suffix, <c>Events/Ka_ThunderPlains/</c>): the English suffix of each event folder
///     (<c>event/obj/&lt;xx&gt;/</c>) and battle area, and notes on scripts the PC build does not load.
/// </summary>
public static class AreaNames {
    private sealed record Table(Dictionary<string, string> Events, Dictionary<string, string> Battles, Dictionary<string, string> Scripts);

    private static readonly Lazy<Table> s_table = new(() => {
        using Stream stream = typeof(AreaNames).Assembly.GetManifestResourceStream("areas.json") ?? throw new InvalidOperationException("areas.json is not embedded");
        using JsonDocument doc = JsonDocument.Parse(stream);
        static Dictionary<string, string> Read(JsonElement e) => e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        return new Table(Read(doc.RootElement.GetProperty("events")), Read(doc.RootElement.GetProperty("battles")), Read(doc.RootElement.GetProperty("scripts")));
    });

    /// <summary>The English suffix of an event folder (<c>ka</c> → <c>ThunderPlains</c>); null when the area keeps its bare code.</summary>
    public static string? EventArea(string group) => s_table.Value.Events.GetValueOrDefault(group.ToLowerInvariant());

    /// <summary>The English suffix of a battle area (<c>kami</c> → <c>ThunderPlains</c>); null when it keeps its bare code.</summary>
    public static string? BattleArea(string code) => s_table.Value.Battles.GetValueOrDefault(code.ToLowerInvariant());

    /// <summary>A note on a script the PC build does not load, or loads only behind a debug switch; null for the others.</summary>
    public static string? ScriptNote(string name) => s_table.Value.Scripts.GetValueOrDefault(name);

    /// <summary>Every event folder suffix, by folder code.</summary>
    public static IReadOnlyDictionary<string, string> EventAreas => s_table.Value.Events;

    /// <summary>Every battle area suffix, by code.</summary>
    public static IReadOnlyDictionary<string, string> BattleAreas => s_table.Value.Battles;
}
