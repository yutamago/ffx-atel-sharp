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
/// <param name="Unsigned">The enum is a <c>uint</c> (<c>Sfx</c>): a cast writes the value in hex, as unsigned.</param>
internal sealed class Domain(string name, string? enumType, Func<int, string?> member, bool unsigned = false) {
    public Domain(string name, string? enumType, IReadOnlyDictionary<int, string> members, bool unsigned = false) : this(name, enumType, members.GetValueOrDefault, unsigned) {
    }

    public string Name { get; } = name;

    public string? EnumType { get; } = enumType;

    public bool Unsigned { get; } = unsigned;

    /// <summary>The member for a value, qualified (<c>Story.S01340_Bjyt0400</c>); null if it has none.</summary>
    public string? Member(int value) => member(value);
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

    /// <summary>
    ///     The bits of the save fields that have names for them (<see cref="BitsAttribute"/>), by storage 0 offset:
    ///     <c>Save.LightningReward.Streak5</c> for 1 in <c>LightningDodgingRewards</c>.
    /// </summary>
    public static IReadOnlyDictionary<int, Domain> SaveBits => s_saveBits.Value;

    private static readonly Lazy<Dictionary<int, Domain>> s_saveBits = new(() => {
        Dictionary<int, Domain> byOffset = [];
        foreach (FieldInfo f in ConstFields(typeof(Ffx.Save))) {
            if (f.GetCustomAttribute<BitsAttribute>() is not BitsAttribute bits) continue;
            Type t = bits.Constants;
            string prefix = t.DeclaringType is Type outer ? $"{outer.Name}.{t.Name}" : t.Name;
            Dictionary<int, string> members = [];
            foreach (FieldInfo c in ConstFields(t)) members.TryAdd(System.Convert.ToInt32(c.GetRawConstantValue(), CultureInfo.InvariantCulture), $"{prefix}.{c.Name}");
            byOffset.TryAdd(System.Convert.ToInt32(f.GetRawConstantValue(), CultureInfo.InvariantCulture), new Domain("bits", null, members));
        }
        return byOffset;
    });

    /// <summary>Messages of the script's table (<see cref="Ffx.TextId"/>): a constant index is <c>Text[12]</c>.</summary>
    public static Domain Text { get; } = new("text", null, v => v >= 0 ? string.Create(CultureInfo.InvariantCulture, $"Text[{v}]") : null);

    /// <summary>The domain of a parameter type: an enum of the reference assembly, or <see cref="Ffx.TextId"/>; null for any other.</summary>
    public static Domain? Enum(Type type) => type == typeof(Ffx.TextId) ? Text : !type.IsEnum ? null : s_enums.GetOrAdd(type, t => {
        Dictionary<int, string> members = [];
        foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Static)) {
            members.TryAdd(unchecked((int)System.Convert.ToInt64(f.GetRawConstantValue(), CultureInfo.InvariantCulture)), $"{t.Name}.{f.Name}");
        }
        return new Domain(t.Name, t.Name, members, System.Enum.GetUnderlyingType(t) == typeof(uint));
    });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Domain> s_enums = new();

    /// <summary>The <c>int</c> constants of a static class (<see cref="ValuesAttribute"/>: <c>Battle.Kami00_00</c>).</summary>
    private static Domain Values(Type type) => s_values.GetOrAdd(type, t => new Domain(t.Name, null, Constants(t)));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Domain> s_values = new();

    /// <summary>A parameter's or result's domain: its enum (or <c>TextId</c>), else the constants its <see cref="ValuesAttribute"/> names.</summary>
    private static Domain? Of(ParameterInfo p) =>
        Enum(p.ParameterType) ?? (p.GetCustomAttribute<ValuesAttribute>() is ValuesAttribute v ? Values(v.Constants) : null);

    /// <summary>The domain of each parameter of each native whose parameters have one (an enum, named constants): by native id.</summary>
    public static IReadOnlyDictionary<ushort, Domain?[]> NativeParameters => s_natives.Value.Parameters;

    /// <summary>The domain of the results of natives that have one (<c>Btl.GetBattleId</c>: <c>Battle</c>), by native id.</summary>
    public static IReadOnlyDictionary<ushort, Domain> NativeResults => s_natives.Value.Results;

    private static readonly Lazy<(Dictionary<ushort, Domain?[]> Parameters, Dictionary<ushort, Domain> Results)> s_natives = new(() => {
        Dictionary<ushort, Domain?[]> parameters = [];
        Dictionary<ushort, Domain> results = [];
        foreach (Type t in Game.GetExportedTypes()) {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                if (m.GetCustomAttribute<NativeAttribute>() is not NativeAttribute a) continue;
                Domain?[] domains = [.. m.GetParameters().Select(Of)];
                if (domains.Any(d => d is not null)) parameters.TryAdd((ushort)a.Id, domains);
                if (Of(m.ReturnParameter) is Domain r) results.TryAdd((ushort)a.Id, r);
            }
        }
        return (parameters, results);
    });

    private static Dictionary<int, string> Constants(Type type) {
        Dictionary<int, string> byValue = [];
        foreach (FieldInfo f in ConstFields(type)) byValue.TryAdd(System.Convert.ToInt32(f.GetRawConstantValue(), CultureInfo.InvariantCulture), $"{type.Name}.{f.Name}");
        return byValue;
    }

    private static IEnumerable<FieldInfo> ConstFields(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(int));
}
