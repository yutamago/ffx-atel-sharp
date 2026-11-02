// SPDX-License-Identifier: MIT

using System.Text.Json;
using Ffx.Atel.Decompiler;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>data/actor-names.json: English worker names for actor models (docs/plan.md, Decisions 2026-11-02).</summary>
public sealed class ActorNameTests {
    [Fact]
    public void EveryEntryNamesAModelWithAUniqueCSharpName() {
        using Stream stream = typeof(ScriptDecompiler).Assembly.GetManifestResourceStream("actor-names.json")!;
        using JsonDocument doc = JsonDocument.Parse(stream);
        HashSet<string> names = new(StringComparer.Ordinal);
        int n = 0;
        foreach (JsonProperty p in doc.RootElement.GetProperty("actors").EnumerateObject()) {
            Assert.True(Enum.TryParse(p.Name, out Model _), $"{p.Name} is not a Model member");
            string name = p.Value.GetProperty("name").GetString()!;
            Assert.True(name.Length > 0 && char.IsAsciiLetterUpper(name[0]) && name.All(char.IsAsciiLetterOrDigit), name);
            Assert.True(names.Add(name), $"{name} twice");
            Assert.False(string.IsNullOrWhiteSpace(p.Value.GetProperty("evidence").GetString()), $"{p.Name} has no evidence");
            n++;
        }
        Assert.True(n > 50);
    }
}
