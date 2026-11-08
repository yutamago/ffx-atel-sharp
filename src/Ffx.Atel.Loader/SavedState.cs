// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using Ffx.Atel.Linker;

namespace Ffx.Atel.Loader;

/// <summary>
///     Which save a state file belongs to: the save's CRC (file <c>+0x1a</c>) and play time (block <c>+0xbc</c>, in the
///     file at <c>+0xfc</c>). A state file whose fingerprint is not the loaded save's was left behind by a save made
///     without the loader, and is ignored.
/// </summary>
public sealed record SaveFingerprint(int Crc, uint PlayTime) {
    /// <summary>The CRC's offset in a save file's header (<c>u16</c>).</summary>
    public const int CrcOffset = 0x1a;

    /// <summary>The play time's offset in the save block (<c>g_SaveData</c>), <c>u32</c>.</summary>
    public const int PlayTimeOffset = 0xbc;

    /// <summary>The save block's offset in a save file.</summary>
    public const int BlockInFile = 0x40;

    /// <summary>The fingerprint of a save file in memory (after <c>SaveDataWriteCrc</c>, or as read from disk).</summary>
    public static SaveFingerprint OfFile(ReadOnlySpan<byte> file) =>
        new(BinaryPrimitives.ReadUInt16LittleEndian(file[CrcOffset..]), BinaryPrimitives.ReadUInt32LittleEndian(file[(BlockInFile + PlayTimeOffset)..]));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"CRC 0x{Crc:x4}, play time {PlayTime}");
}

/// <summary>One saved field's value in a state file: its type and length when it was saved, and its bytes.</summary>
public sealed record StoredValue(int Type, int Length, byte[] Bytes);

/// <summary>
///     The loader's local state for one save (Fahrenheit's <c>state/…/&lt;save&gt;/yuta.atel/…/state</c>): every saved
///     field's value by mod and key, and the save it belongs to (docs/research/mod-saves.md).
/// </summary>
public sealed class StateFile {
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;

    /// <summary>The save this state was written with.</summary>
    public SaveFingerprint? Save { get; set; }

    /// <summary>Values by mod id, then by key (<c>LarvaCount.Progress.LarvasMet</c>).</summary>
    public Dictionary<string, Dictionary<string, StoredValue>> Vars { get; set; } = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    /// <exception cref="JsonException">Not a state file.</exception>
    public static StateFile FromJson(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<StateFile>(json, Options) ?? throw new JsonException("an empty state file");
}

/// <summary>
///     The mods' saved fields while the game runs: the save's mod area (<see cref="SaveArea"/>, in <c>g_SaveData</c>) is
///     their home, which scripts read and write as plain storage 0 variables; this class fills it on a new game and on a
///     load, and takes it apart by key when the game saves. The game-free part of the loader's save hooks.
/// </summary>
/// <remarks>
///     Values are kept by key, never by offset: the offsets are this run's (<see cref="SavedLayout"/>). On a load every
///     field of the linked mods takes its stored value when the state has one of the same type (an array that grew keeps
///     its first elements, the rest take the initial value), else its initial value; the values of mods that are not
///     linked now (disabled, left out) are kept as they were and written again on the next save, so they come back when
///     the mod does.
/// </remarks>
public sealed class SavedState(SavedLayout layout) {
    private Dictionary<string, Dictionary<string, StoredValue>> _kept = new(StringComparer.Ordinal);

    public SavedLayout Layout { get; } = layout;

    /// <summary>The mods whose values are kept without being linked now.</summary>
    public IReadOnlyCollection<string> KeptMods => _kept.Keys;

    /// <summary>A new game: the area is cleared (the game clears it too) and every field takes its initial value.</summary>
    public void NewGame(Span<byte> area) {
        _kept = new(StringComparer.Ordinal);
        Fill(area, null, []);
    }

    /// <summary>
    ///     A loaded save: rebuilds the area from <paramref name="state"/> (null when the save has no state file, a vanilla
    ///     save). A state of another save than <paramref name="loaded"/> is ignored, with a warning.
    /// </summary>
    /// <returns>What happened, for the log: warnings first.</returns>
    public List<(bool Warning, string Text)> Load(Span<byte> area, StateFile? state, SaveFingerprint? loaded) {
        List<(bool, string)> report = [];
        if (state is not null && state.Format > StateFile.CurrentFormat) {
            report.Add((true, $"the saved fields' state has format {state.Format}, newer than this loader's ({StateFile.CurrentFormat}); ignored, the fields start from their initial values"));
            state = null;
        }
        else if (state is not null && (loaded is null || state.Save != loaded)) {
            report.Add((true, $"the saved fields' state belongs to another save ({state.Save?.ToString() ?? "none"}; this one: {loaded?.ToString() ?? "unknown"}), "
                + "probably one made without the loader; ignored, the fields start from their initial values"));
            state = null;
        }
        _kept = new(StringComparer.Ordinal);
        if (state is not null) {
            foreach ((string mod, Dictionary<string, StoredValue> values) in state.Vars) {
                if (!Layout.Mods.Contains(mod)) _kept[mod] = new(values, StringComparer.Ordinal);
            }
        }
        Fill(area, state, report);
        return report;
    }

    /// <summary>The state to write for a save: every linked field's bytes from the area, and the kept values.</summary>
    public StateFile Capture(ReadOnlySpan<byte> area, SaveFingerprint? save) {
        StateFile state = new() { Save = save };
        foreach ((string mod, Dictionary<string, StoredValue> values) in _kept) state.Vars[mod] = new(values, StringComparer.Ordinal);
        foreach (PlacedField f in Layout.Fields) {
            if (!state.Vars.TryGetValue(f.Mod, out Dictionary<string, StoredValue>? values)) state.Vars[f.Mod] = values = new(StringComparer.Ordinal);
            values[f.Field.Key] = new StoredValue(f.Field.Type, f.Field.Length, area.Slice(f.AreaOffset, f.Field.Size).ToArray());
        }
        return state;
    }

    private void Fill(Span<byte> area, StateFile? state, List<(bool, string)> report) {
        area[..SaveArea.Length].Clear();
        int stored = 0, initial = 0;
        foreach (PlacedField f in Layout.Fields) {
            Span<byte> to = area.Slice(f.AreaOffset, f.Field.Size);
            f.Field.InitialBytes().CopyTo(to);
            StoredValue? v = state is not null && state.Vars.TryGetValue(f.Mod, out var values) ? values.GetValueOrDefault(f.Field.Key) : null;
            if (v is null) {
                initial++;
                continue;
            }
            if (v.Type != f.Field.Type) {
                report.Add((true, $"{f.FullKey} was saved as type {v.Type} and is type {f.Field.Type} now; it starts from its initial value"));
                initial++;
                continue;
            }
            int elements = Math.Min(Math.Min(v.Length, f.Field.Length), v.Bytes.Length / f.Field.ElementSize);
            v.Bytes.AsSpan(0, elements * f.Field.ElementSize).CopyTo(to);
            if (v.Length != f.Field.Length) {
                report.Add((false, $"{f.FullKey} was saved with {v.Length} element(s) and has {f.Field.Length} now; {elements} kept"
                    + (elements < f.Field.Length ? ", the others start from their initial values" : "")));
            }
            stored++;
        }
        report.Add((false, $"{Layout.Fields.Count} saved field(s): {stored} from the save's state, {initial} from their initial values"
            + (_kept.Count > 0 ? $"; kept for mods not linked now: {string.Join(", ", _kept.Keys.Order(StringComparer.Ordinal))}" : "")));
    }

    /// <summary>Reads a state file; null (with the reason) when it is empty or broken.</summary>
    public static StateFile? Read(Stream stream, out string? problem) {
        problem = null;
        try {
            byte[] bytes = new byte[stream.Length];
            stream.Position = 0;
            stream.ReadExactly(bytes);
            if (bytes.Length == 0) return null;
            return StateFile.FromJson(bytes);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    ///     Writes a state file over <paramref name="stream"/>: Fahrenheit opens it without truncating, so its length is set
    ///     first. The stream is not flushed or closed (Fahrenheit does that).
    /// </summary>
    public static void Write(Stream stream, StateFile state) {
        byte[] bytes = state.ToJson();
        stream.SetLength(0);
        stream.Position = 0;
        stream.Write(bytes);
    }
}
