// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;

namespace Ffx.Atel.Bytecode.Analysis;

/// <summary>What the bytecode analyses need of a native: its argument count, kinds and name.</summary>
/// <param name="Id">The <c>func</c> operand.</param>
/// <param name="Name">Reference assembly name, <c>Class.Method</c>.</param>
/// <param name="ArgKinds">One <c>'i'</c> or <c>'f'</c> per argument, first pushed first.</param>
/// <param name="Result"><c>"int"</c>, <c>"float"</c>, <c>"bool"</c> (an int the scripts use only as a condition, 0 or not) or <c>"none"</c> (the call still pushes 0).</param>
/// <param name="ArgcSource">Where the count came from: <c>both</c>, <c>exe</c> or <c>bytecode</c>.</param>
/// <param name="KindsFromExe">Whether the kinds come from the handler's own pops (not from the scripts).</param>
/// <param name="Polls">Whether it has a poll handler: the call can wait.</param>
public sealed record NativeSignature(ushort Id, string Name, string ArgKinds, string Result, string ArgcSource, bool KindsFromExe, bool Polls = false) {
    public int Argc => ArgKinds.Length;

    /// <summary>The parameters' names, for named arguments; null where they are not known.</summary>
    public string[]? ParamNames { get; init; }
}

/// <summary>The native signatures of <c>data/natives.json</c> (written by <c>tools/gen-natives.cs</c>).</summary>
public sealed class NativeSignatures {
    private readonly Dictionary<ushort, NativeSignature> _byId;

    private NativeSignatures(Dictionary<ushort, NativeSignature> byId) => _byId = byId;

    public NativeSignature? this[ushort id] => _byId.GetValueOrDefault(id);

    public IEnumerable<NativeSignature> All => _byId.Values.OrderBy(n => n.Id);

    /// <summary>Parses the JSON text of <c>data/natives.json</c>.</summary>
    public static NativeSignatures Parse(string json) {
        using JsonDocument doc = JsonDocument.Parse(json);
        Dictionary<ushort, NativeSignature> byId = [];
        foreach (JsonElement n in doc.RootElement.GetProperty("natives").EnumerateArray()) {
            string idText = n.GetProperty("id").GetString()!;
            ushort id = ushort.Parse(idText.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string kinds = string.Concat(n.GetProperty("args").EnumerateArray().Select(a => a.GetProperty("kind").GetString() == "float" ? 'f' : 'i'));
            byId[id] = new NativeSignature(
                id,
                $"{n.GetProperty("class").GetString()}.{n.GetProperty("method").GetString()}",
                kinds,
                n.GetProperty("result").GetString()!,
                n.GetProperty("argc_source").GetString()!,
                n.GetProperty("kinds_source").GetString() == "exe",
                n.TryGetProperty("polls", out JsonElement polls) && polls.GetBoolean()) {
                ParamNames = [.. n.GetProperty("args").EnumerateArray().Select(a => a.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "")],
            };
        }
        return new NativeSignatures(byId);
    }

    public static NativeSignatures Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    ///     The natives an assembly declares: static methods with <c>Ffx.Atel.NativeAttribute</c> (the
    ///     reference assembly, <c>Ffx.Atel.Game</c>, generated from <c>data/natives.json</c>). Parameters
    ///     and results of type <c>float</c> are float kinds, all others int; the name is
    ///     <c>Class.Method</c>. The argument count source is <c>reference</c>.
    /// </summary>
    public static NativeSignatures FromAssembly(System.Reflection.Assembly assembly) {
        Dictionary<ushort, NativeSignature> byId = [];
        foreach (Type t in assembly.GetExportedTypes()) {
            foreach (System.Reflection.MethodInfo m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)) {
                System.Reflection.CustomAttributeData? a = m.CustomAttributes.FirstOrDefault(x => x.AttributeType.FullName == "Ffx.Atel.NativeAttribute");
                if (a is null) continue;
                ushort id = (ushort)(int)a.ConstructorArguments[0].Value!;
                bool polls = a.NamedArguments.Any(n => n.MemberName == "Polls" && n.TypedValue.Value is true);
                string kinds = string.Concat(m.GetParameters().Select(p => p.ParameterType == typeof(float) ? 'f' : 'i'));
                string result = m.ReturnType == typeof(float) ? "float" : m.ReturnType == typeof(void) ? "none" : m.ReturnType == typeof(bool) ? "bool" : "int";
                byId.TryAdd(id, new NativeSignature(id, $"{t.Name}.{m.Name}", kinds, result, "reference", false, polls) {
                    ParamNames = [.. m.GetParameters().Select(p => p.Name ?? "")],
                });
            }
        }
        return new NativeSignatures(byId);
    }

    /// <summary>
    ///     Finds <c>data/natives.json</c> in the current directory or any above it, then in the program's
    ///     directory or any above it; null if there is none.
    /// </summary>
    public static string? Find() {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }) {
            for (DirectoryInfo? d = new(start); d is not null; d = d.Parent) {
                string p = Path.Combine(d.FullName, "data", "natives.json");
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }
}
