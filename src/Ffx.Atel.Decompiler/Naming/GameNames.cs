// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Reflection;

namespace Ffx.Atel.Decompiler.Naming;

/// <summary>
///     A set of named values an expression can belong to by use (docs/plan.md, "Readability": values are
///     typed by use): the story progress values (<c>Story.S01340_Bjyt0400</c>, constants of type <c>int</c>)
///     or an enum of the reference assembly (<c>Command.Attack</c>), whose values without a member are
///     written as casts (<c>(Command)1234</c>).
/// </summary>
/// <param name="Name">What the statistics count it as (<c>story</c>, the enum's name).</param>
/// <param name="EnumType">The enum's C# name; null for <c>int</c> constants.</param>
internal sealed class Domain(string name, string? enumType, IReadOnlyDictionary<int, string> members) {
    public string Name { get; } = name;

    public string? EnumType { get; } = enumType;

    /// <summary>The member for a value, qualified (<c>Story.S01340_Bjyt0400</c>); null if it has none.</summary>
    public string? Member(int value) => members.GetValueOrDefault(value);
}

/// <summary>
///     The names the reference assembly (<c>Ffx.Atel.Game</c>) gives to values and storage locations:
///     <see cref="Ffx.Story"/>, <see cref="Ffx.Save"/>, and the enums natives take. Read by reflection, as
///     the natives are, so that the assembly stays the one place the names are written.
/// </summary>
internal static class GameNames {
    private static readonly Assembly Game = typeof(NativeAttribute).Assembly;

    /// <summary>The story progress values (<c>Story</c>), by value.</summary>
    public static Domain Story => s_story.Value;

    private static readonly Lazy<Domain> s_story = new(() => new Domain("story", null, Constants(typeof(Ffx.Story))));

    /// <summary>Storage 0 offset of the story progress (<see cref="Ffx.Save.StoryProgress"/>).</summary>
    public const int StoryOffset = Ffx.Save.StoryProgress;

    /// <summary>The known fields of the save (storage 0), by storage 0 offset: the constant's name (<c>StoryProgress</c>).</summary>
    public static IReadOnlyDictionary<int, string> SaveFields => s_save.Value;

    private static readonly Lazy<Dictionary<int, string>> s_save = new(() => {
        Dictionary<int, string> byOffset = [];
        foreach (FieldInfo f in ConstFields(typeof(Ffx.Save))) byOffset.TryAdd(System.Convert.ToInt32(f.GetRawConstantValue(), CultureInfo.InvariantCulture), f.Name);
        return byOffset;
    });

    /// <summary>The domain of an enum of the reference assembly, by its C# name; null if there is none.</summary>
    public static Domain? Enum(Type type) => !type.IsEnum ? null : s_enums.GetOrAdd(type, t => {
        Dictionary<int, string> members = [];
        foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Static)) {
            members.TryAdd(System.Convert.ToInt32(f.GetRawConstantValue(), CultureInfo.InvariantCulture), $"{t.Name}.{f.Name}");
        }
        return new Domain(t.Name, t.Name, members);
    });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Domain> s_enums = new();

    /// <summary>The domain of each parameter of each native whose parameter types are enums: by native id.</summary>
    public static IReadOnlyDictionary<ushort, Domain?[]> NativeParameters => s_params.Value;

    private static readonly Lazy<Dictionary<ushort, Domain?[]>> s_params = new(() => {
        Dictionary<ushort, Domain?[]> byId = [];
        foreach (Type t in Game.GetExportedTypes()) {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                if (m.GetCustomAttribute<NativeAttribute>() is not NativeAttribute a) continue;
                Domain?[] domains = [.. m.GetParameters().Select(p => Enum(p.ParameterType))];
                if (domains.Any(d => d is not null)) byId.TryAdd((ushort)a.Id, domains);
            }
        }
        return byId;
    });

    private static Dictionary<int, string> Constants(Type type) {
        Dictionary<int, string> byValue = [];
        foreach (FieldInfo f in ConstFields(type)) byValue.TryAdd(System.Convert.ToInt32(f.GetRawConstantValue(), CultureInfo.InvariantCulture), $"{type.Name}.{f.Name}");
        return byValue;
    }

    private static IEnumerable<FieldInfo> ConstFields(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(int));
}
