// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Bytecode;

/// <summary>Where the scripts are in the player's game.</summary>
public static class GameData {
    /// <summary>The environment variable pointing at the game folder (the one with <c>FFX.exe</c>).</summary>
    public const string GameDirVariable = "FFX_GAME_DIR";

    public const string MonsterPrefix = "ffx_ps2/ffx/master/jppc/battle/mon/";
    public const string BattlePrefix  = "ffx_ps2/ffx/master/jppc/battle/btl/";
    public const string EventPrefix   = "ffx_ps2/ffx/master/jppc/event/";

    /// <summary>The game folder from <c>FFX_GAME_DIR</c>, or null when it is unset.</summary>
    public static string? GameDir() {
        string? dir = Environment.GetEnvironmentVariable(GameDirVariable);
        return string.IsNullOrEmpty(dir) ? null : dir;
    }

    /// <summary>Opens <c>data/FFX_Data.vbf</c> in a game folder.</summary>
    public static VbfArchive OpenArchive(string gameDir) => VbfArchive.Open(Path.Combine(gameDir, "data", "FFX_Data.vbf"));

    /// <summary>The event (<c>.ebp</c>) and monster (<c>mNNN.bin</c>) scripts, sorted: the 758 the round trip covers.</summary>
    public static List<string> ScriptPaths(VbfArchive archive) => archive.Entries
        .Select(e => e.Path)
        .Where(p => (p.StartsWith(EventPrefix, StringComparison.Ordinal) && p.EndsWith(".ebp", StringComparison.Ordinal))
                 || (p.StartsWith(MonsterPrefix, StringComparison.Ordinal) && p.EndsWith(".bin", StringComparison.Ordinal)))
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    ///     Every script: the 758 event and monster scripts and the 863 battle scripts, sorted.
    /// </summary>
    public static List<string> AllScriptPaths(VbfArchive archive) => [.. ScriptPaths(archive).Concat(BattlePaths(archive)).Order(StringComparer.Ordinal)];

    /// <summary>The battle scripts (<c>battle/btl/*/*.bin</c>), sorted.</summary>
    public static List<string> BattlePaths(VbfArchive archive) => archive.Entries
        .Select(e => e.Path)
        .Where(p => p.StartsWith(BattlePrefix, StringComparison.Ordinal) && p.EndsWith(".bin", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    ///     Resolves a short name to an archive path: <c>m001</c> is a monster, <c>bjyt0400</c> an event
    ///     (searched under <c>event/</c>), anything with a <c>/</c> a path.
    /// </summary>
    public static string? Resolve(VbfArchive archive, string name) {
        if (name.Contains('/')) return archive.Find(name)?.Path;
        string lower = name.ToLowerInvariant();
        if (lower.Length == 4 && lower[0] == 'm' && int.TryParse(lower.AsSpan(1), out _)) {
            string path = $"{MonsterPrefix}_{lower}/{lower}.bin";
            if (archive.Find(path) is not null) return path;
        }
        return archive.Entries.Select(e => e.Path).FirstOrDefault(p =>
            (p.StartsWith(EventPrefix, StringComparison.Ordinal) && p.EndsWith($"/{lower}.ebp", StringComparison.Ordinal))
            || (p.StartsWith(BattlePrefix, StringComparison.Ordinal) && p.EndsWith($"/{lower}.bin", StringComparison.Ordinal)));
    }
}
