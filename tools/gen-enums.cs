// SPDX-License-Identifier: MIT
//
// Writes src/Ffx.Atel.Game/Enums.g.cs: the enums native parameters are typed with (data/native-types.json),
// with the names of the PS2 headers as Fahrenheit transcribed them (src/core/ffx/ids/*.cs; used with
// the Fahrenheit project's permission). Only names and values are taken: each `public const T_X… NAME = value;` line
// of the listed classes, its prefix dropped and the rest in C# case (`CHR_ALL_PLY` -> `AllPly`). Phase 5 of
// docs/plan.md. Also src/Ffx.Atel.Game/Battle.g.cs: the battle ids of btl.h (`BTL_KAMI00_00` ->
// `Battle.Kami00_00`), as int constants (the scripts keep them in int registers).
//
// usage: dotnet run tools/gen-enums.cs -- <fahrenheit checkout>

#:property PublishAot=false

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length != 1) {
    Console.Error.WriteLine("usage: dotnet run tools/gen-enums.cs -- <fahrenheit checkout>");
    return 1;
}
string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, ".."));
string ids = Path.Combine(args[0], "src", "core", "ffx", "ids");
string commit = "";
try {
    string head = File.ReadAllText(Path.Combine(args[0], ".git", "HEAD")).Trim();
    commit = head.StartsWith("ref: ", StringComparison.Ordinal) ? File.ReadAllText(Path.Combine(args[0], ".git", head[5..])).Trim() : head;
    commit = commit[..7];
}
catch (IOException) {
}

// enum, summary, (file, Fahrenheit class, header, prefixes) …
(string Name, string Summary, (string File, string Class, string Header, string[] Prefixes)[] Sources)[] enums = [
    ("Target", "A battle target selector: the negative values name actor groups (<c>T_XTargetId</c>); 0-<c>0x13</c> are the party actors by character (<c>T_XPlySaveId</c>), <c>0x14</c>-<c>0x1b</c> the monster slots (ffx-rust docs/formats/battle.md: <c>MsGetChr</c>); a monster id (<c>0x1000 | n</c>) picks every actor with that id (<c>MsGetChrBit</c>), named after the monster's <c>chr_</c> model (<c>mon.ath</c>).",
        [("target.cs", "TargetId", "btlmot.ath", ["CHR_"]), ("ply_save.cs", "PlySaveId", "ply_save.ath", ["PC_"])]),
    ("ActorProperty", "A battle actor's property, read and written by <c>Btl.GetActorProperty</c> and <c>Btl.SetActorsProperty</c> (<c>MsGetChrStatInfo</c>, <c>MsSetChrStatInfo</c>; <c>T_XChrStatId</c>, ffx-rust docs/formats/battle.md).",
        [("chr_stat.cs", "ChrStatId", "btlhead.src", ["STAT_"])]),
    ("Command", "A battle command (<c>T_XCommandId</c>): the player commands (<c>0x3000</c>-), items (<c>0x2000</c>-), monster commands (<c>0x4000</c>-) and boss commands (<c>0x6000</c>-).",
        [("com_ply.cs", "PlayerCommandId", "command.ath", ["PCOM_"]), ("item.cs", "ItemId", "item.ath", ["ITEM_"]),
         ("com_mon.cs", "MonsterCommandId", "monmagic1.ath", ["MCOM_"]), ("com_boss.cs", "BossCommandId", "monmagic2.ath", ["MCOM2_"])]),
    ("BattleMotion", "A battle actor's standard motion (<c>T_XBtlMotionId</c>); the scripts may add bit <c>0x8000</c> (no blend).",
        [("mot_btl.cs", "BtlMotionId", "btlmot.ath", ["MOT_"])]),
    ("Model", "A character model (<c>Common.SetModel</c>): party members (<c>pc.ath</c>, 1-), monsters (<c>0x1000</c>-), NPCs (<c>0x2000</c>-), summons (<c>0x3000</c>-), weapons (<c>0x4000</c>-), objects (<c>0x5000</c>-), skeletons (<c>0x6000</c>-). One member per value: the <c>chr_</c> name where the header gives one, else the file code (<c>w001</c>).",
        [("PcModelId.cs", "PcModelId", "pc.ath", ["chr_"]), ("MonsterId.cs", "MonsterId", "mon.ath", ["chr_"]), ("NpcId.cs", "NpcId", "npc.ath", ["chr_"]),
         ("SummonId.cs", "SummonId", "sum.ath", ["chr_"]), ("WeaponId.cs", "WeaponId", "wep.ath", ["chr_"]), ("ObjectId.cs", "ObjectId", "obj.ath", ["chr_"]),
         ("SkeletonId.cs", "SkeletonId", "skl.ath", ["chr_"])]),
];

Regex line = new(@"^\s*public const (?:T_X\w+|int)\s+(\w+)\s*=\s*(-?)(0x[0-9A-Fa-f_]+|[0-9_]+)\s*;");

static string Pascal(string s) {
    StringBuilder sb = new();
    foreach (string part in s.Split('_', StringSplitOptions.RemoveEmptyEntries)) {
        if (sb.Length > 0 && char.IsAsciiDigit(sb[^1]) && char.IsAsciiDigit(part[0])) sb.Append('_');
        sb.Append(char.ToUpperInvariant(part[0])).Append(part[1..].ToLowerInvariant());
    }
    return sb.ToString();
}

// Each `public const T_X… NAME = value;` of a Fahrenheit ids file: the name and the value.
List<(string Original, long Value)> Parse(string file) {
    List<(string Original, long Value)> raw = [];
    foreach (string text in File.ReadLines(Path.Combine(ids, file))) {
        Match m = line.Match(text);
        if (!m.Success) continue;
        string digits = m.Groups[3].Value.Replace("_", "", StringComparison.Ordinal);
        long value = digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(digits, CultureInfo.InvariantCulture);
        if (m.Groups[2].Value == "-") value = -value;
        raw.Add((m.Groups[1].Value, value));
    }
    return raw;
}

StringBuilder cs = new();
cs.Append(CultureInfo.InvariantCulture, $$"""
    // SPDX-License-Identifier: MIT
    // <auto-generated> by tools/gen-enums.cs from Fahrenheit's ids (src/core/ffx/ids, {{(commit.Length > 0 ? commit : "unknown commit")}}): do not edit. </auto-generated>
    //
    // The names are the PS2 headers' (the file is in each enum's remarks), as Fahrenheit transcribed them,
    // used with the Fahrenheit project's permission; prefixes dropped and in C# case.

    namespace Ffx;

    """);
int total = 0;
foreach ((string name, string summary, var sources) in enums) {
    List<(string Member, string Original, long Value)> members = [];
    HashSet<string> used = new(StringComparer.Ordinal);
    foreach ((string file, string cls, string header, string[] prefixes) in sources) {
        List<(string Original, long Value)> raw = Parse(file);
        // The model tables give each value a file code and often a chr_ name: one member, the chr_ name first.
        if (name == "Model") {
            raw = [.. raw.GroupBy(r => r.Value).Select(g => g.FirstOrDefault(r => r.Original.StartsWith("chr_", StringComparison.Ordinal)) is { Original: not null } c ? c : g.First())];
        }
        foreach ((string original, long value) in raw) {
            string rest = original;
            foreach (string p in prefixes) {
                if (rest.StartsWith(p, StringComparison.Ordinal)) {
                    rest = rest[p.Length..];
                    break;
                }
            }
            string member = Pascal(rest);
            // `MCOM_4000` keeps its prefix: a member cannot start with a digit; a clash keeps the whole name.
            if (member.Length == 0 || char.IsAsciiDigit(member[0])) member = Pascal(original);
            if (!used.Add(member)) {
                member = Pascal(original);
                if (!used.Add(member)) continue;
            }
            members.Add((member, original, value));
        }
    }
    // The monster actors: slot n is actor 0x14 + n (MsGetChr; ffx-rust docs/formats/battle.md), named here.
    if (name == "Target") {
        for (int n = 0; n < 8; n++) members.Add(($"Monster{n}", $"monster slot {n} (actor 0x{0x14 + n:x2})", 0x14 + n));
        // A monster id picks every actor with that id (MsGetChrBit: selector 0x1xxx, the actor's +0xe; ffx-rust
        // symbols/ffx.sym), named after the monster's chr_ model as Model names it; file codes name nothing,
        // as for workers. A name Target already has gets "Monster" in front.
        foreach (IGrouping<long, (string Original, long Value)> g in Parse("MonsterId.cs").Where(r => r.Value is >= 0x1000 and < 0x2000).GroupBy(r => r.Value)) {
            if (g.FirstOrDefault(r => r.Original.StartsWith("chr_", StringComparison.Ordinal)) is not { Original: not null } c) continue;
            string member = Pascal(c.Original[4..]);
            if (member.Length == 0 || char.IsAsciiDigit(member[0]) || !used.Add(member)) {
                member = "Monster" + member;
                if (!used.Add(member)) continue;
            }
            members.Add((member, $"every actor of monster <c>{c.Original}</c>", g.Key));
        }
    }
    cs.Append(CultureInfo.InvariantCulture, $"\n/// <summary>{summary}</summary>\n");
    cs.Append(CultureInfo.InvariantCulture, $"/// <remarks>From {string.Join(", ", sources.Select(s => $"<c>{s.Header}</c> (Fahrenheit's <c>{s.Class}</c>)"))}.</remarks>\n");
    cs.Append(CultureInfo.InvariantCulture, $"public enum {name}\n{{\n");
    foreach ((string member, string original, long value) in members) {
        string v = value < 0 ? value.ToString(CultureInfo.InvariantCulture) : "0x" + value.ToString("x4", CultureInfo.InvariantCulture);
        string doc = original.StartsWith("monster slot", StringComparison.Ordinal) || original.StartsWith("every actor", StringComparison.Ordinal) ? original : $"<c>{original}</c>";
        cs.Append(CultureInfo.InvariantCulture, $"    /// <summary>{doc}</summary>\n    {member} = {v},\n");
    }
    cs.Append("}\n");
    Console.WriteLine($"{name}: {members.Count}");
    total += members.Count;
}
File.WriteAllText(Path.Combine(root, "src", "Ffx.Atel.Game", "Enums.g.cs"), cs.ToString());
Console.WriteLine($"{total} members");

// The battle ids (btl.h, Fahrenheit's BtlId): the battle field in the high half, the formation in the low
// half; what Btl.LabelExe starts and Btl.GetBattleId returns (MsBattleLabelExe, MsGetBattleScene; ffx-rust
// symbols/ffx.sym). Int constants, not an enum: the scripts keep the id in int registers and compare it there
// (Atel.Values on the natives, data/native-types.json).
StringBuilder bt = new();
bt.Append(CultureInfo.InvariantCulture, $$"""
    // SPDX-License-Identifier: MIT
    // <auto-generated> by tools/gen-enums.cs from Fahrenheit's ids (src/core/ffx/ids/btl.cs, {{(commit.Length > 0 ? commit : "unknown commit")}}): do not edit. </auto-generated>
    //
    // The names are the PS2 header's (btl.h), as Fahrenheit transcribed them, used with the Fahrenheit project's
    // permission; the BTL_ prefix dropped and in C# case.

    namespace Ffx;

    /// <summary>
    ///     The battles, by id: the battle field in the high half, the formation in the low half
    ///     (<c>BTL_KAMI00_00</c> is <c>Battle.Kami00_00</c>). <c>Btl.LabelExe(Battle.Kami00_00, 1)</c> starts
    ///     one (<c>MsBattleLabelExe</c>), <c>Btl.GetBattleId()</c> is the one running (<c>MsGetBattleScene</c>).
    ///     Constants, not an enum: the scripts keep the id in <c>int</c> registers and compare it there.
    /// </summary>
    /// <remarks>From <c>btl.h</c> (Fahrenheit's <c>BtlId</c>).</remarks>
    public static class Battle
    {

    """);
HashSet<string> battleNames = new(StringComparer.Ordinal);
foreach ((string original, long value) in Parse("btl.cs")) {
    string member = Pascal(original.StartsWith("BTL_", StringComparison.Ordinal) ? original[4..] : original);
    if (member.Length == 0 || char.IsAsciiDigit(member[0]) || !battleNames.Add(member)) throw new InvalidOperationException($"btl.cs: no name for {original}");
    bt.Append(CultureInfo.InvariantCulture, $"    /// <summary><c>{original}</c></summary>\n    public const int {member} = 0x{value:x8};\n");
}
bt.Append("}\n");
File.WriteAllText(Path.Combine(root, "src", "Ffx.Atel.Game", "Battle.g.cs"), bt.ToString());
Console.WriteLine($"Battle: {battleNames.Count} ids");
return 0;

static string ThisFile([CallerFilePath] string path = "") => path;
