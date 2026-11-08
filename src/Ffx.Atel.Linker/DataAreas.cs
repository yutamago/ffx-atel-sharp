// SPDX-License-Identifier: MIT

using System.Globalization;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Linker;

/// <summary>
///     Where a package's data edits (<see cref="ScriptPackage.Data"/>) write in a script file, and the hashes of those
///     areas' vanilla bytes (docs/formats/mod-package.md, "Data edits"). The data areas of the block never move when
///     linking (they hold block offsets), and the monster file's other sections move only as a whole, so an edit is
///     a write of its bytes in place.
/// </summary>
public static class DataAreas {
    /// <summary>A monster file's stat record, its copy and its rewards (ffx-decomp, docs/formats/monster.md).</summary>
    public const int StatsSection = 2, StatsCopySection = 6, RewardsSection = 4;

    /// <summary>The stat record is <c>0x80</c> bytes; the first <c>0x14</c> are string references, which differ between its two copies.</summary>
    public const int StatsLength = 0x80, StatsFirst = 0x14;

    /// <summary>The rewards are <c>0x118</c> bytes.</summary>
    public const int RewardsLength = 0x118;

    /// <summary>
    ///     An area of a script file: <see cref="At"/> its start in the file, <see cref="Length"/> the bytes the hash covers,
    ///     <see cref="First"/> the first an edit may write (the stat record's string references are not edited).
    /// </summary>
    public readonly record struct Area(int At, int Length, int First);

    /// <summary>
    ///     The area <paramref name="area"/> (of worker <paramref name="worker"/>, for private data) in a script file; null
    ///     when the file does not have it. <paramref name="path"/> is the file's archive path: only a monster file
    ///     (<c>battle/mon/</c>) has monster stats and rewards.
    /// </summary>
    public static Area? Find(ScriptFile file, ScriptImage image, string path, DataEditArea area, int? worker) {
        ScriptBlock b = file.Script;
        int Block(uint at) => file.SectionStart + (int)at;
        switch (area) {
            case DataEditArea.Shared:
                return b.Workers.Count > 0 && image.Shared is { Length: > 0 } s && b.U32(b.Workers[0].Header + 0x30) is uint sh and not 0 ? new Area(Block(sh), s.Length, 0) : null;
            case DataEditArea.Other:
                return image.Other is { Length: > 0 } o && b.U32(0x2c) is uint ot and not 0 ? new Area(Block(ot), o.Length, 0) : null;
            case DataEditArea.Event:
                return image.Event is { Length: > 0 } e && b.U32(0x20) is uint ev and not 0 ? new Area(Block(ev), e.Length, 0) : null;
            case DataEditArea.Area:
                return image.Area is { Length: > 0 } a && b.U32(0x28) is uint ar and not 0 ? new Area(Block(ar), a.Length, 0) : null;
            case DataEditArea.Private: {
                if (worker is not int w || w < 0 || w >= b.Workers.Count) return null;
                uint at = b.U32(b.Workers[w].Header + 0x2c);
                uint length = image.Workers[w].PrivateLength;
                return at != 0 && length > 0 ? new Area(Block(at), (int)length, 0) : null;
            }
            case DataEditArea.MonsterStats or DataEditArea.MonsterRewards: {
                if (!path.StartsWith(GameData.MonsterPrefix, StringComparison.Ordinal)) return null;
                (int section, int length, int first) = area == DataEditArea.MonsterStats ? (StatsSection, StatsLength, StatsFirst) : (RewardsSection, RewardsLength, 0);
                return file.Section(section) is (int start, int room) && room >= length ? new Area(start, length, first) : null;
            }
            default:
                return null;
        }
    }

    /// <summary>The stat record's copy (section 6), where the linker writes the same bytes as into section 2.</summary>
    internal static Area? StatsCopy(ScriptFile file) =>
        file.Section(StatsCopySection) is (int start, int room) && room >= StatsLength ? new Area(start, StatsLength, StatsFirst) : null;

    /// <summary>What an area is called in messages.</summary>
    public static string Describe(DataEditArea area, int? worker) => area switch {
        DataEditArea.Shared => "the shared data",
        DataEditArea.Other => "the other area",
        DataEditArea.Event => "the event data",
        DataEditArea.Area => "the area data",
        DataEditArea.Private => string.Create(CultureInfo.InvariantCulture, $"worker {worker}'s private data"),
        DataEditArea.MonsterStats => "the monster's stats",
        DataEditArea.MonsterRewards => "the monster's rewards",
        _ => area.ToString(),
    };

    /// <summary>
    ///     Fills in <see cref="VanillaRefs.Data"/> for a package's data edits from the vanilla file (<c>atel compile-mod</c>,
    ///     with the player's file) and sets the format the package needs.
    /// </summary>
    /// <exception cref="ArgumentException">An edit is outside its area, or the file has no such area.</exception>
    public static void Attach(ScriptPackage p, byte[] vanillaFile) {
        p.Format = Math.Max(p.Format, p.NeededFormat());
        if (p.Data is not { Count: > 0 }) return;
        ScriptFile file = ScriptFile.Parse(vanillaFile);
        ScriptImage image = ScriptImage.FromBlock(file.Script);
        List<string> problems = [];
        List<DataRef> refs = [];
        foreach (var key in p.Data.Select(e => (e.Area, e.Worker)).Distinct()) {
            if (Find(file, image, p.File, key.Area, key.Worker) is not Area a) {
                problems.Add($"{p.Script} has no {Describe(key.Area, key.Worker)}");
                continue;
            }
            refs.Add(new DataRef(key.Area, key.Worker, VanillaHash.Data(file.Bytes.Slice(a.At, a.Length))));
        }
        problems.AddRange(Check(p, file, image));
        if (problems.Count > 0) throw new ArgumentException(string.Join("; ", problems.Distinct(StringComparer.Ordinal)));
        p.Vanilla.Data = refs;
    }

    /// <summary>What keeps a package's data edits from applying to this file (beyond their hashes); empty when they apply.</summary>
    internal static List<string> Check(ScriptPackage p, ScriptFile file, ScriptImage image) {
        List<string> problems = [];
        foreach (DataEdit e in p.Data ?? []) {
            if (Find(file, image, p.File, e.Area, e.Worker) is not Area a) {
                problems.Add($"{e.Field}: the script has no {Describe(e.Area, e.Worker)}");
                continue;
            }
            if (e.Bytes.Length == 0 || e.Offset < a.First || (long)e.Offset + e.Bytes.Length > a.Length) {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{e.Field}: {e.Bytes.Length} byte(s) at +0x{e.Offset:x} are outside {Describe(e.Area, e.Worker)} (0x{a.First:x}-0x{a.Length:x})"));
            }
            if (e.Area == DataEditArea.MonsterStats) {
                if (StatsCopy(file) is not Area c) problems.Add($"{e.Field}: the monster file has no copy of its stats (section {StatsCopySection})");
                else if (!file.Bytes.Slice(a.At + StatsFirst, StatsLength - StatsFirst).SequenceEqual(file.Bytes.Slice(c.At + StatsFirst, StatsLength - StatsFirst))) {
                    problems.Add($"{e.Field}: the monster's stats in sections {StatsSection} and {StatsCopySection} differ");
                }
            }
        }
        return problems;
    }
}
