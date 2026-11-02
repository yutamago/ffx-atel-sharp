// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Ffx.Atel.Decompiler.Naming;

/// <summary>
///     Entry method names from the overlay (<see cref="NameOverlay.Entries"/>): <c>"18.2" = "Strike"</c>
///     names the method whose first entry is W18's entry 2 <c>Strike()</c>, or <c>StrikeAsync()</c> when
///     other workers request it, and the requests call it so. Only an entry that starts a method of its own
///     can be named: not <c>Init</c> and <c>Main</c> (entries 0 and 1, the base class's), not a function
///     worker's <c>Run</c>, not an entry that shares its code with a lower one.
/// </summary>
internal static class EntryNaming {
    /// <exception cref="FormatException">The overlay names an entry that has no method of its own, or gives a worker's methods the same name.</exception>
    public static void Apply(WorkerDecompiler[] workers, string[] workerNames, NameOverlay overlay) {
        foreach (IGrouping<int, KeyValuePair<(int Worker, int Entry), string>> g in overlay.Entries.GroupBy(kv => kv.Key.Worker)) {
            int w = g.Key;
            if (w >= workers.Length) throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"{overlay.Source}: entry {w}.*: the script has {workers.Length} workers"));
            HashSet<int> heads = [.. workers[w].MethodEntries()];
            HashSet<string> used = new(StringComparer.Ordinal);
            foreach (((_, int e), string name) in g) {
                if (!heads.Contains(e)) {
                    throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                        $"{overlay.Source}: entry {w}.{e}: {workerNames[w]} has no method starting with entry {e} (Init, Main, Run and entries sharing a method keep their names)"));
                }
                if (name == workerNames[w]) throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"{overlay.Source}: entry {w}.{e}: a method cannot be named as its class"));
                if (!used.Add(name)) throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"{overlay.Source}: two methods of {workerNames[w]} are named {name}"));
                workers[w].EntryNames[e] = name;
            }
        }
    }
}
