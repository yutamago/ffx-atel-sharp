// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Ffx.Atel.Linker;

/// <summary>
///     Debug text: the natives the loader adds in a native group the game leaves empty, and how a string
///     gets from the source to them (docs/formats/debug.md).
/// </summary>
/// <remarks>
///     ATEL has no strings, so the compiler pushes a string's <see cref="Id"/>, a 32-bit hash of its
///     text, and the package carries the text (<see cref="ScriptPackage.DebugStrings"/>); the loader
///     keeps every text it was given by id (<see cref="DebugStringTable"/>). An interpolated string becomes
///     a composite format (<c>$"hp = {Hp}"</c> is <c>hp = {0}</c>) whose values are pushed before it.
/// </remarks>
public static class DebugText {
    /// <summary>The native group the loader provides (<c>id &gt;&gt; 12</c>); the game registers none for it.</summary>
    public const int Group = 0xf;

    /// <summary>
    ///     <c>Debug.Print</c>: pops the string's id, the value count <c>n</c>, then <c>n</c> values (the
    ///     last pushed first; each an <c>int</c> or a <c>float</c> by its stack slot), and logs the text.
    ///     Result 0.
    /// </summary>
    public const ushort Print = (Group << 12) | 0x000;

    /// <summary><c>Debug.Break</c>: no arguments; logs the worker's registers and breaks into an attached .NET debugger. Result 0.</summary>
    public const ushort Break = (Group << 12) | 0x001;

    /// <summary>The most values one <c>Debug.Print</c> takes: the stack holds 20, and the count and id take two.</summary>
    public const int MaxValues = 16;

    /// <summary>The id of a text: FNV-1a over its UTF-8 bytes.</summary>
    public static int Id(string text) {
        uint h = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(text)) {
            h ^= b;
            h *= 16777619;
        }
        return unchecked((int)h);
    }

    /// <summary>
    ///     A printed line: <paramref name="format"/> (a composite format, <c>{0}</c> the first value) with the
    ///     values, invariant culture. Without the text (an id the loader was never given) or with a format
    ///     that does not fit the values, the id and the values.
    /// </summary>
    public static string Format(string? format, int id, IReadOnlyList<object> values) {
        if (format is not null) {
            try {
                return string.Format(CultureInfo.InvariantCulture, format, [.. values]);
            }
            catch (FormatException) {
                // Falls through: the text and the values as they are.
            }
        }
        StringBuilder s = new(format ?? string.Create(CultureInfo.InvariantCulture, $"#{unchecked((uint)id):x8}"));
        if (values.Count > 0) {
            s.Append(" [");
            s.AppendJoin(", ", values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
            s.Append(']');
        }
        return s.ToString();
    }
}

/// <summary>Every debug text the loader was given, by <see cref="DebugText.Id"/>; thread-safe.</summary>
public sealed class DebugStringTable {
    private readonly ConcurrentDictionary<int, string> _texts = new();

    public int Count => _texts.Count;

    /// <summary>Adds the texts; returns those whose id another text already has (that text stays).</summary>
    public List<(string Text, string Kept)> Add(IEnumerable<string> texts) {
        List<(string, string)> clashes = [];
        foreach (string t in texts) {
            string kept = _texts.GetOrAdd(DebugText.Id(t), t);
            if (!string.Equals(kept, t, StringComparison.Ordinal)) clashes.Add((t, kept));
        }
        return clashes;
    }

    /// <summary>The text of an id, or null.</summary>
    public string? Find(int id) => _texts.GetValueOrDefault(id);
}
