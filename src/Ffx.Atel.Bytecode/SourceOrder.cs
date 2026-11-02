// SPDX-License-Identifier: MIT

namespace Ffx.Atel.Bytecode;

/// <summary>
///     The order of a script's source files: by path without the <c>.cs</c> extension, ordinal, so that
///     <c>W36.cs</c> comes before <c>W36.2.cs</c>. The compiler lays a worker's entry methods out in this
///     order of its <c>partial</c> parts, then in declaration order (docs/plan.md, "Phase 6: source layout").
/// </summary>
public static class SourceOrder {
    public static int Compare(string? a, string? b) => string.CompareOrdinal(Key(a ?? ""), Key(b ?? ""));

    public static IComparer<string> Comparer { get; } = Comparer<string>.Create(Compare);

    private static string Key(string path) {
        string p = path.Replace('\\', '/');
        return p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? p[..^3] : p;
    }
}
