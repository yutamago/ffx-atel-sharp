// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;

namespace Ffx.Atel.Loader.Tests;

/// <summary>
///     The on-screen <c>Debug.*</c> lines (<see cref="DebugLines"/>, <see cref="DebugLine"/>,
///     <see cref="DebugOverlaySettings"/>): the buffer, the fade and the text, without the game.
/// </summary>
public sealed class DebugOverlayTests {
    private static readonly DateTime Noon = new(2026, 11, 9, 12, 4, 31);

    private static DebugLine Line(double at, string text, int? worker = 1) =>
        new(TimeSpan.FromSeconds(at), Noon.AddSeconds(at), worker, text);

    private static readonly DebugOverlaySettings Six = new() { Lines = 6, Seconds = 8 };

    // The buffer.

    [Fact]
    public void KeepsTheNewestLinesOldestFirst() {
        DebugLines lines = new();
        for (int i = 0; i < 10; i++) lines.Add(Line(0, $"line {i}"));
        Assert.Equal(["line 4", "line 5", "line 6", "line 7", "line 8", "line 9"],
            lines.Visible(TimeSpan.Zero, Six).Select(v => v.Line.Text));
    }

    [Fact]
    public void FewerLinesThanTheCountShowsThemAll() {
        DebugLines lines = new();
        lines.Add(Line(0, "a"));
        lines.Add(Line(0, "b"));
        Assert.Equal(["a", "b"], lines.Visible(TimeSpan.Zero, Six).Select(v => v.Line.Text));
    }

    [Fact]
    public void WrapsAroundAtCapacity() {
        DebugLines lines = new();
        for (int i = 0; i < DebugLines.Capacity + 5; i++) lines.Add(Line(0, $"{i}"));
        Assert.Equal(DebugLines.Capacity, lines.Count);
        DebugOverlaySettings all = Six with { Lines = DebugOverlaySettings.MaxLines };
        List<VisibleLine> shown = lines.Visible(TimeSpan.Zero, all);
        Assert.Equal(DebugLines.Capacity, shown.Count);
        Assert.Equal("5", shown[0].Line.Text);
        Assert.Equal($"{DebugLines.Capacity + 4}", shown[^1].Line.Text);
    }

    [Fact]
    public void ClearForgetsEveryLine() {
        DebugLines lines = new();
        lines.Add(Line(0, "a"));
        lines.Clear();
        Assert.Equal(0, lines.Count);
        Assert.Empty(lines.Visible(TimeSpan.Zero, Six));
    }

    [Fact]
    public void ALineCountOutOfRangeIsClamped() {
        DebugLines lines = new();
        for (int i = 0; i < 5; i++) lines.Add(Line(0, $"{i}"));
        Assert.Single(lines.Visible(TimeSpan.Zero, Six with { Lines = 0 }));
        Assert.Equal(5, lines.Visible(TimeSpan.Zero, Six with { Lines = 1000 }).Count);
    }

    [Fact]
    public void AddsFromManyThreads() {
        DebugLines lines = new();
        Parallel.For(0, 10_000, i => lines.Add(Line(0, $"{i}")));
        Assert.Equal(DebugLines.Capacity, lines.Count);
        Assert.Equal(DebugLines.Capacity, lines.Visible(TimeSpan.Zero, Six with { Lines = DebugOverlaySettings.MaxLines }).Count);
    }

    // The fade.

    [Theory]
    [InlineData(-1.0, 1.0)]  // a clock that went back: shown
    [InlineData(0.0, 1.0)]
    [InlineData(6.0, 1.0)]   // 8 s, the last quarter (2 s) fades
    [InlineData(7.0, 0.5)]
    [InlineData(7.5, 0.25)]
    [InlineData(8.0, 0.0)]
    [InlineData(60.0, 0.0)]
    public void FadesOverTheLastQuarter(double age, double alpha) =>
        Assert.Equal(alpha, DebugLines.Alpha(TimeSpan.FromSeconds(age), 8), 5);

    [Fact]
    public void FadedLinesAreNotShownAndTheRestCarryTheirAlpha() {
        DebugLines lines = new();
        lines.Add(Line(0, "old"));
        lines.Add(Line(3, "mid"));
        lines.Add(Line(9, "new"));
        List<VisibleLine> shown = lines.Visible(TimeSpan.FromSeconds(10), Six);
        Assert.Equal(["mid", "new"], shown.Select(v => v.Line.Text));
        Assert.Equal(0.5f, shown[0].Alpha, 5);  // 7 s old
        Assert.Equal(1f, shown[1].Alpha);
    }

    [Fact]
    public void AFadedNewLineDoesNotBringBackOlderOnes() {
        DebugLines lines = new();
        for (int i = 0; i < 6; i++) lines.Add(Line(0, $"early {i}"));
        lines.Add(Line(20, "late"));
        Assert.Equal(["late"], lines.Visible(TimeSpan.FromSeconds(21), Six).Select(v => v.Line.Text));
    }

    // The text.

    [Fact]
    public void ALineShowsItsTimeAndWorker() {
        Assert.Equal("12:04:34 w1  Larvas met: 3", Line(3, "Larvas met: 3").ToString());
        Assert.Equal("12:04:31 w12  hp 5", Line(0, "hp 5", 12).ToString());
    }

    [Fact]
    public void ALineOfTheLoaderHasNoWorker() =>
        Assert.Equal("12:04:31  Atel debug overlay on", Line(0, "Atel debug overlay on", null).ToString());

    [Fact]
    public void TheTimeIsTwentyFourHourWhateverTheCulture() {
        CultureInfo was = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            DebugLine evening = new(TimeSpan.Zero, new DateTime(2026, 11, 9, 21, 5, 0), 3, "x");
            Assert.Equal("21:05:00 w3", evening.Prefix);
        }
        finally {
            CultureInfo.CurrentCulture = was;
        }
    }

    [Fact]
    public void ALongTextIsCut() {
        string body = Line(0, new string('x', 500)).Body;
        Assert.Equal(DebugLine.MaxText, body.Length);
        Assert.EndsWith("…", body);
        Assert.Equal(new string('x', DebugLine.MaxText), Line(0, new string('x', DebugLine.MaxText)).Body);
    }

    [Fact]
    public void LineBreaksAndTabsBecomeSpaces() =>
        Assert.Equal("a b c d", Line(0, "a\r\nb\nc\td").Body);

    // The settings.

    [Fact]
    public void AnEmptyStateFileIsTheDefaults() {
        Assert.Equal(new DebugOverlaySettings(), DebugOverlaySettings.FromJson(""));
        Assert.Equal(new DebugOverlaySettings(), DebugOverlaySettings.FromJson("{}"));
        DebugOverlaySettings d = new();
        Assert.True(d.Enabled);
        Assert.Equal(6, d.Lines);
        Assert.Equal(8, d.Seconds);
    }

    [Fact]
    public void SettingsRoundTrip() {
        DebugOverlaySettings s = new() { Enabled = false, Lines = 10, Seconds = 12.5 };
        Assert.Equal(s, DebugOverlaySettings.FromJson(s.ToJson()));
    }

    [Fact]
    public void TheStateFileIsTheDocumentedJson() {
        using JsonDocument doc = JsonDocument.Parse(new DebugOverlaySettings().ToJson());
        JsonElement o = doc.RootElement.GetProperty("debugOverlay");
        Assert.True(o.GetProperty("enabled").GetBoolean());
        Assert.Equal(6, o.GetProperty("lines").GetInt32());
        Assert.Equal(8, o.GetProperty("seconds").GetDouble());
    }

    [Fact]
    public void MissingValuesKeepTheirDefaultsAndOthersAreClamped() {
        DebugOverlaySettings s = DebugOverlaySettings.FromJson("""{ "debugOverlay": { "lines": 999 } }""");
        Assert.True(s.Enabled);
        Assert.Equal(DebugOverlaySettings.MaxLines, s.Lines);
        Assert.Equal(DebugOverlaySettings.MinLines, DebugOverlaySettings.FromJson("""{ "debugOverlay": { "lines": -3 } }""").Lines);
        Assert.Equal(DebugOverlaySettings.MinSeconds, DebugOverlaySettings.FromJson("""{ "debugOverlay": { "seconds": 0 } }""").Seconds);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("""{ "debugOverlay": 3 }""")]
    [InlineData("""{ "debugOverlay": { "enabled": "yes" } }""")]
    [InlineData("""{ "debugOverlay": { "lines": 2.5 } }""")]
    [InlineData("""{ "debugOverlay": { "seconds": "8" } }""")]
    [InlineData("not json")]
    public void ABadStateFileIsRefused(string json) =>
        Assert.ThrowsAny<JsonException>(() => DebugOverlaySettings.FromJson(json));

    [Theory]
    [InlineData(3, 6)]
    [InlineData(6, 10)]
    [InlineData(10, 16)]
    [InlineData(16, 3)]
    [InlineData(4, 6)]   // a count from the file between the steps
    [InlineData(32, 3)]
    public void ShiftF6StepsTheLineCount(int lines, int next) =>
        Assert.Equal(next, new DebugOverlaySettings { Lines = lines }.NextLines());
}
