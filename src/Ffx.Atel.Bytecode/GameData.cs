// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Bytecode;

/// <summary>The three kinds of script: events (<c>.ebp</c>), monster AI and battle scripts.</summary>
public enum ScriptCategory {
    Event,
    Monster,
    Battle,
}

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

    /// <summary>
    ///     Opens the archive for a whole-corpus job, holding a machine-wide lock until the archive is
    ///     disposed, so such jobs (the corpus tests of several checkouts, say) run one at a time: each
    ///     uses every core and several GB, and together they exhaust the machine's memory.
    /// </summary>
    /// <remarks>
    ///     The lock is <c>ffx-atel-corpus.lock</c> in the temp folder, opened without sharing; it is
    ///     released when the process ends, however it ends. Set <c>ATEL_CORPUS_LOCK=0</c> to skip it.
    /// </remarks>
    public static VbfArchive OpenArchiveExclusive(string gameDir) {
        IDisposable? held = Environment.GetEnvironmentVariable("ATEL_CORPUS_LOCK") == "0" ? null : AcquireCorpusLock();
        try {
            VbfArchive archive = OpenArchive(gameDir);
            archive.Owned = held;
            return archive;
        }
        catch {
            held?.Dispose();
            throw;
        }
    }

    private static FileStream AcquireCorpusLock() {
        string path = Path.Combine(Path.GetTempPath(), "ffx-atel-corpus.lock");
        while (true) {
            try {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) {
                Thread.Sleep(2000);
            }
        }
    }

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

    /// <summary>What a script path holds and its name: the file name without extension (<c>bjyt0400</c>, <c>m001</c>, <c>cdsp00_00</c>).</summary>
    public static (ScriptCategory Category, string Name)? Identify(string path) {
        ScriptCategory? category = path.StartsWith(EventPrefix, StringComparison.Ordinal) ? ScriptCategory.Event
            : path.StartsWith(MonsterPrefix, StringComparison.Ordinal) ? ScriptCategory.Monster
            : path.StartsWith(BattlePrefix, StringComparison.Ordinal) ? ScriptCategory.Battle
            : null;
        if (category is null) return null;
        string file = path[(path.LastIndexOf('/') + 1)..];
        int dot = file.IndexOf('.');
        return (category.Value, dot < 0 ? file : file[..dot]);
    }

    /// <summary>The archive path of a script by category and name (file names are unique within each).</summary>
    public static string? Find(VbfArchive archive, ScriptCategory category, string name) {
        string lower = name.ToLowerInvariant();
        if (category == ScriptCategory.Monster) {
            string path = $"{MonsterPrefix}_{lower}/{lower}.bin";
            return archive.Find(path) is not null ? path : null;
        }
        (string prefix, string suffix) = category == ScriptCategory.Event ? (EventPrefix, $"/{lower}.ebp") : (BattlePrefix, $"/{lower}.bin");
        return archive.Entries.Select(e => e.Path).FirstOrDefault(p => p.StartsWith(prefix, StringComparison.Ordinal) && p.EndsWith(suffix, StringComparison.Ordinal));
    }

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
