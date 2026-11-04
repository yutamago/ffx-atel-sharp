// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Ffx.Atel.Cuts;

/// <summary>
///     The player's switches: on or off per name, and no entry for a name left as loaded (the vanilla block, or what
///     a linked mod made of it). Read on the game thread every frame, set from the menu on the render thread.
/// </summary>
/// <remarks>
///     Saved as JSON, <c>{ "cuts": { "HighroadBattleLine335Tidus": true } }</c>, in the module's state file, which
///     Fahrenheit keeps at <c>state/global/yuta.atel.cuts/Ffx.Atel.Cuts.CutsModule</c>.
/// </remarks>
public sealed class CutStates {
    private readonly ConcurrentDictionary<string, bool> _states = new(StringComparer.Ordinal);
    private int _version;

    /// <summary>Goes up with every change, so the game thread can tell that it has to apply them.</summary>
    public int Version => System.Threading.Volatile.Read(ref _version);

    /// <summary>The switch for <paramref name="name"/>: true on, false off, null as loaded.</summary>
    public bool? Get(string name) => _states.TryGetValue(name, out bool on) ? on : null;

    /// <summary>Sets the switch for <paramref name="name"/>; null leaves it as loaded again.</summary>
    public void Set(string name, bool? on) {
        if (on is bool b) _states[name] = b;
        else _states.TryRemove(name, out _);
        System.Threading.Interlocked.Increment(ref _version);
    }

    /// <summary>The switches that are set, by name.</summary>
    public IReadOnlyDictionary<string, bool> All => _states.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>The JSON the state file holds.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new Dictionary<string, SortedDictionary<string, bool>> { ["cuts"] = new(_states, StringComparer.Ordinal) },
        new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    ///     Reads a state file; an empty one is no switches. Names <paramref name="known"/> does not have are dropped
    ///     and listed in <paramref name="dropped"/> (a name removed from the mod).
    /// </summary>
    /// <exception cref="JsonException">The file is not the JSON <see cref="ToJson"/> writes.</exception>
    public static CutStates FromJson(string json, IReadOnlySet<string> known, List<string> dropped) {
        CutStates states = new();
        if (string.IsNullOrWhiteSpace(json)) return states;
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("cuts", out JsonElement cuts) || cuts.ValueKind != JsonValueKind.Object) {
            throw new JsonException("expected { \"cuts\": { \"Name\": true or false, … } }");
        }
        foreach (JsonProperty p in cuts.EnumerateObject()) {
            if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException($"{p.Name}: expected true or false");
            if (known.Contains(p.Name)) states._states[p.Name] = p.Value.GetBoolean();
            else dropped.Add(p.Name);
        }
        return states;
    }
}
