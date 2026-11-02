// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

/// <summary>
///     The named constants of the reference assembly (<c>Ffx.Atel.Game</c>) by <c>Type.Member</c>: the
///     <c>int</c> constants of its static classes (<c>Story.S01340_Bjyt0400</c>, <c>Save.StoryProgress</c>)
///     and its enum members (<c>Command.Attack</c>). The compiler reads source from the syntax alone, so a
///     name is looked up by its last two identifiers (<c>Ffx.Story.X</c> is <c>Story.X</c>).
/// </summary>
internal static class GameConstants {
    private static readonly Lazy<(Dictionary<string, int> Constants, Dictionary<string, int> Members)> s_tables = new(() => {
        Dictionary<string, int> constants = new(StringComparer.Ordinal), members = new(StringComparer.Ordinal);
        foreach (Type t in typeof(Worker).Assembly.GetExportedTypes()) {
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Static)) {
                if (!f.IsLiteral) continue;
                object? raw = f.GetRawConstantValue();
                if (raw is not (int or uint or short or ushort or byte or sbyte)) continue;
                int value = unchecked((int)System.Convert.ToInt64(raw, CultureInfo.InvariantCulture));
                (t.IsEnum ? members : constants).TryAdd($"{t.Name}.{f.Name}", value);
            }
        }
        return (constants, members);
    });

    /// <summary>The value of <c>Type.Member</c>: a constant of a static class, or (with <paramref name="enums"/>) an enum member.</summary>
    public static int? Lookup(MemberAccessExpressionSyntax m, bool enums) {
        string type = m.Expression switch {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax inner => inner.Name.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            _ => "",
        };
        string key = $"{type}.{m.Name.Identifier.ValueText}";
        if (s_tables.Value.Constants.TryGetValue(key, out int v)) return v;
        return enums && s_tables.Value.Members.TryGetValue(key, out v) ? v : null;
    }
}
