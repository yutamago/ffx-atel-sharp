// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Ffx.Atel.Loader;

/// <summary>A line a <c>Debug.*</c> native printed: when, which worker (null for the loader's own notes), and the text.</summary>
/// <param name="At">Time since the loader started (a monotonic clock): what the line fades by.</param>
/// <param name="Clock">The wall-clock time it was printed: what the overlay shows.</param>
public sealed record DebugLine(TimeSpan At, DateTime Clock, int? Worker, string Text) {
    /// <summary>The longest text the overlay shows; the rest is cut and marked with an ellipsis.</summary>
    public const int MaxText = 160;

    /// <summary>The prefix: <c>12:04:31 w1</c>, or the time alone for a line of the loader's own.</summary>
    public string Prefix => Worker is int w
        ? string.Create(CultureInfo.InvariantCulture, $"{Clock:HH:mm:ss} w{w}")
        : Clock.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The text on one line: line breaks and tabs as spaces, at most <see cref="MaxText"/> characters.</summary>
    public string Body {
        get {
            string flat = Text.ReplaceLineEndings(" ").Replace('\t', ' ');
            return flat.Length <= MaxText ? flat : string.Concat(flat.AsSpan(0, MaxText - 1), "…");
        }
    }

    /// <summary>The whole line, as the overlay shows it.</summary>
    public override string ToString() => $"{Prefix}  {Body}";
}

/// <summary>A line to draw and how opaque it is (1 shown, towards 0 fading).</summary>
public readonly record struct VisibleLine(DebugLine Line, float Alpha);

/// <summary>
///     The overlay's settings: whether it shows, how many lines, and for how long. Kept as JSON in the loader's
///     Fahrenheit global state file, <c>state/global/yuta.atel/Ffx.Atel.Loader.AtelLoaderModule</c>:
///     <c>{ "debugOverlay": { "enabled": true, "lines": 6, "seconds": 8 } }</c>.
/// </summary>
public sealed record DebugOverlaySettings {
    public const int MinLines = 1, MaxLines = DebugLines.Capacity;
    public const double MinSeconds = 1, MaxSeconds = 600;

    /// <summary>The line counts Shift+F6 steps through.</summary>
    public static IReadOnlyList<int> LineSteps { get; } = [3, 6, 10, 16];

    /// <summary>Whether the overlay is drawn (F6). Lines are kept either way.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The newest lines shown, at most.</summary>
    public int Lines { get; init; } = 6;

    /// <summary>How long a line stays, its fade included.</summary>
    public double Seconds { get; init; } = 8;

    /// <summary>The next line count after <see cref="Lines"/> (the first after the last).</summary>
    public int NextLines() {
        foreach (int n in LineSteps) {
            if (n > Lines) return n;
        }
        return LineSteps[0];
    }

    /// <summary>The JSON the state file holds.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new Dictionary<string, object> {
            ["debugOverlay"] = new Dictionary<string, object> { ["enabled"] = Enabled, ["lines"] = Lines, ["seconds"] = Seconds },
        },
        new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    ///     Reads the state file. An empty file, or one without <c>debugOverlay</c>, is the defaults; a value out of
    ///     range is clamped; a missing one keeps its default.
    /// </summary>
    /// <exception cref="JsonException">The file is not JSON, or a value has the wrong type.</exception>
    public static DebugOverlaySettings FromJson(string json) {
        DebugOverlaySettings s = new();
        if (string.IsNullOrWhiteSpace(json)) return s;
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("expected a JSON object");
        if (!doc.RootElement.TryGetProperty("debugOverlay", out JsonElement o)) return s;
        if (o.ValueKind != JsonValueKind.Object) throw new JsonException("debugOverlay: expected an object");
        if (o.TryGetProperty("enabled", out JsonElement e)) {
            if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException("debugOverlay.enabled: expected true or false");
            s = s with { Enabled = e.GetBoolean() };
        }
        if (o.TryGetProperty("lines", out JsonElement l)) {
            if (l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out int n)) throw new JsonException("debugOverlay.lines: expected a whole number");
            s = s with { Lines = Math.Clamp(n, MinLines, MaxLines) };
        }
        if (o.TryGetProperty("seconds", out JsonElement t)) {
            if (t.ValueKind != JsonValueKind.Number) throw new JsonException("debugOverlay.seconds: expected a number");
            s = s with { Seconds = Math.Clamp(t.GetDouble(), MinSeconds, MaxSeconds) };
        }
        return s;
    }
}

/// <summary>
///     The last <see cref="Capacity"/> <c>Debug.*</c> lines, newest last. Added on the game thread (the natives),
///     read on the render thread (the overlay), so every access takes the lock.
/// </summary>
public sealed class DebugLines {
    /// <summary>The lines kept: the most the settings can ask to show.</summary>
    public const int Capacity = 32;

    /// <summary>How much of a line's time is the fade at its end.</summary>
    public const double FadeShare = 0.25;

    private readonly DebugLine[] _ring = new DebugLine[Capacity];
    private readonly object _lock = new();
    private int _next, _count;

    /// <summary>The lines kept now.</summary>
    public int Count {
        get {
            lock (_lock) return _count;
        }
    }

    /// <summary>Adds a line; the oldest goes when the buffer is full.</summary>
    public void Add(DebugLine line) {
        lock (_lock) {
            _ring[_next] = line;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Forgets every line.</summary>
    public void Clear() {
        lock (_lock) {
            Array.Clear(_ring);
            _next = _count = 0;
        }
    }

    /// <summary>
    ///     How opaque a line of age <paramref name="age"/> is, when lines stay <paramref name="seconds"/>: 1 for the
    ///     first <c>1 - </c><see cref="FadeShare"/> of that time, then down to 0 in a straight line, 0 after.
    /// </summary>
    public static float Alpha(TimeSpan age, double seconds) {
        double t = age.TotalSeconds;
        if (t < 0) return 1f;
        if (t >= seconds) return 0f;
        double fade = seconds * FadeShare;
        double start = seconds - fade;
        return t <= start ? 1f : (float)((seconds - t) / fade);
    }

    /// <summary>
    ///     The lines to draw at <paramref name="now"/>, oldest first: the newest <see cref="DebugOverlaySettings.Lines"/>,
    ///     without those that have faded out.
    /// </summary>
    public List<VisibleLine> Visible(TimeSpan now, DebugOverlaySettings settings) {
        List<VisibleLine> shown = [];
        lock (_lock) {
            int n = Math.Min(_count, Math.Clamp(settings.Lines, DebugOverlaySettings.MinLines, DebugOverlaySettings.MaxLines));
            for (int i = n; i >= 1; i--) {
                DebugLine line = _ring[(_next - i + Capacity) % Capacity];
                float a = Alpha(now - line.At, settings.Seconds);
                if (a > 0f) shown.Add(new VisibleLine(line, a));
            }
        }
        return shown;
    }
}
