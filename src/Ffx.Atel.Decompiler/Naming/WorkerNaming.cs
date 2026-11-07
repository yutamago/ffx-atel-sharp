// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Reflection;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler.Naming;

/// <summary>
///     Names for workers where the bytes say what they are: a worker that loads exactly one model
///     (<c>Common.SetModel</c>, native <c>0x0001</c>, with a constant) is named after that model's member of
///     <see cref="Ffx.Model"/> (the PS2 headers' <c>chr_</c> names): <c>Takarabox002</c>, or the English name of
///     an actor whose character is known (<see cref="ActorNames"/>: <c>Tidu</c> → <c>Tidus</c>). Several
///     workers with the same model get their number (<c>Tidus_03</c>, <c>Tidus_07</c>); a model without a
///     <c>chr_</c> name (only its file code, <c>W101</c>) names nothing. A worker without a model name is
///     named after its role where its structured code says it (<see cref="AssignRoles"/>: <c>Director</c>,
///     <c>ExitToKami0100</c>, <c>CameraLine</c>); all others keep <c>W03</c>. Function workers keep theirs too.
/// </summary>
internal static class WorkerNaming {
    private const ushort SetModel = 0x0001;

    /// <summary>
    ///     The names, given the default ones (<c>W00</c>, …) and names already taken in the source. The
    ///     overlay's names (<see cref="NameOverlay.Workers"/>) come first; no other worker takes one of them.
    /// </summary>
    /// <exception cref="FormatException">The overlay names a worker the script does not have, gives a name twice, or a name the source already uses.</exception>
    public static string[] Names(ScriptBlock block, ScriptImage image, string[] defaults, IEnumerable<string> taken, NameOverlay? overlay = null) {
        overlay ??= NameOverlay.Empty;
        HashSet<string> reserved = new(taken, StringComparer.Ordinal);
        reserved.UnionWith(Reserved.Value);
        foreach ((int w, string name) in overlay.Workers.OrderBy(kv => kv.Key)) {
            if (w >= defaults.Length) throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"{overlay.Source}: worker {w}: the script has {defaults.Length} workers"));
            if (reserved.Contains(name)) throw new FormatException($"{overlay.Source}: worker {w}: {name} is already a name in the source or the reference assembly");
        }
        if (overlay.Workers.Values.Distinct(StringComparer.Ordinal).Count() != overlay.Workers.Count) throw new FormatException($"{overlay.Source}: two workers have the same name");
        reserved.UnionWith(overlay.Workers.Values);
        // A default name (W03) the overlay gave another worker would clash too.
        for (int w = 0; w < defaults.Length; w++) {
            if (!overlay.Workers.ContainsKey(w) && overlay.Workers.Values.Contains(defaults[w], StringComparer.Ordinal)) {
                throw new FormatException($"{overlay.Source}: {defaults[w]} is worker {w}'s default name; give another");
            }
        }
        Domain models = GameNames.Enum(typeof(Ffx.Model))!;
        string?[] baseNames = new string?[defaults.Length];
        for (int w = 0; w < defaults.Length; w++) {
            Bytecode.Script.Worker worker = block.Workers[w];
            if (worker.Type == 0) continue;
            HashSet<int> loaded = [];
            bool unknown = false;
            List<Instr> code = block.Decode(worker);
            for (int i = 0; i < code.Count; i++) {
                if (code[i].Code is not (Opcodes.Func or Opcodes.FuncFacc) || code[i].Operand != SetModel) continue;
                Instr arg = i > 0 ? code[i - 1] : default;
                int? value = arg.Code switch {
                    Opcodes.PushImm => (short)arg.Operand!.Value,
                    Opcodes.PushC when arg.Operand < image.IntConstants.Count => (int)image.IntConstants[arg.Operand!.Value],
                    _ => null,
                };
                if (value is int v) loaded.Add(v);
                else unknown = true;
            }
            if (unknown || loaded.Count != 1) continue;
            string? member = models.Member(loaded.First());
            string? name = member?[(member.IndexOf('.', StringComparison.Ordinal) + 1)..];
            // An actor whose character is known gets its English name (Tidu → Tidus); the Model member keeps the code.
            if (name is not null && ActorNames.Value.TryGetValue(name, out string? english)) name = english;
            // Only the headers' chr_ names: a file code (W101, C001) reads like a default name.
            if (name is not null && !(name.Length > 1 && char.IsAsciiLetterUpper(name[0]) && name[1..].All(char.IsAsciiDigit))) baseNames[w] = name;
        }
        string[] names = (string[])defaults.Clone();
        foreach ((int w, string name) in overlay.Workers) names[w] = name;
        foreach (IGrouping<string, int> g in Enumerable.Range(0, defaults.Length).Where(w => baseNames[w] is not null).GroupBy(w => baseNames[w]!)) {
            // The overlay's workers still count, so that a rename leaves the others' names as they were.
            foreach (int w in g.Where(w => !overlay.Workers.ContainsKey(w))) {
                string name = g.Count() == 1 ? g.Key : $"{g.Key}_{defaults[w][1..]}";
                if (!reserved.Contains(name)) names[w] = name;
            }
        }
        return names;
    }

    /// <summary>The natives that change the map, their first argument the <see cref="Ffx.Map"/>: <c>MapChange</c>, <c>MapJump</c>, <c>MapChangeDefault</c>, <c>MapChange_00ab</c>.</summary>
    private static readonly HashSet<ushort> MapChanges = [0x0011, 0x010b, 0x010c, 0x00ab];

    /// <summary><c>MapJumpToEntrance</c> (<c>0x8006</c>): a jump within the map, by entrance only.</summary>
    private const ushort JumpToEntrance = 0x8006;

    /// <summary>What a native is for, as far as the role names go (<see cref="AssignRoles"/>).</summary>
    private enum Use { Neutral, Camera, Dialogue, Trigger, Light, Bones, Other }

    /// <summary>
    ///     A native's use by its reference assembly name (FFX.exe's, <c>data/natives.json</c>): the camera
    ///     (<c>Cam.*</c>, <c>Common.AttachCamera</c>), message windows and voices (<c>Msg*</c>, <c>Voice*</c>,
    ///     and the player's control around them), the trigger's own setup (<c>Trigger*</c>, <c>Line*</c>,
    ///     <c>SetTalk*</c>, <c>SetTriggerMode</c>, <c>SetTouchRange</c>, <c>Enable</c>/<c>DisableTrigger</c>,
    ///     the yaw of a box and the target of a sensor), the lights (<c>Screen.LightSet</c>, FFX.exe's
    ///     <c>Light_Set</c>) and the skeleton updates after them, or waiting (<c>Common.Wait*</c>).
    /// </summary>
    private static Use UseOf(ushort id) {
        string? name = ScriptDecompiler.Natives[id]?.Name;
        if (name is null) return Use.Other;
        string cls = name[..name.IndexOf('.', StringComparison.Ordinal)], method = name[(cls.Length + 1)..];
        if (cls == "Common" && method.StartsWith("Wait", StringComparison.Ordinal)) return Use.Neutral;
        if (cls == "Cam" || name == "Common.AttachCamera") return Use.Camera;
        if (method.StartsWith("Msg", StringComparison.Ordinal) || method.StartsWith("Voice", StringComparison.Ordinal)
            || name is "Common.ControlDisable" or "Common.ControlEnable") return Use.Dialogue;
        if (cls == "Common" && (method.StartsWith("Trigger", StringComparison.Ordinal) || method.StartsWith("Line", StringComparison.Ordinal)
                                || method.StartsWith("SetTalk", StringComparison.Ordinal)
                                || method is "SetTriggerMode" or "SetTouchRange" or "EnableTrigger" or "DisableTrigger" or "PosSetYaw" or "PosSetField28")) return Use.Trigger;
        if (name is "Screen.LightSet") return Use.Light;
        if (name is "Chr.RefreshAllBones" or "Chr.AllBoneUpdate" or "Chr.SetBoneUpdateOf") return Use.Bones;
        return Use.Other;
    }

    /// <summary>
    ///     Role names for the workers that still have their default name (<c>W27</c>), read off what their
    ///     structured code does (<see cref="WorkerDecompiler.Structure"/> first), in this order:
    ///     <list type="bullet">
    ///         <item>a trigger (event worker of type 2, 3, 5 or 6) whose map changes all go to one constant map:
    ///         <c>ExitTo</c> and the <see cref="Ffx.Map"/> member (<c>ExitToKami0100</c>);</item>
    ///         <item>a worker whose <c>Main</c> switches on <c>StoryProgress</c> and requests other workers'
    ///         entries from its cases (the story index): <c>Director</c>;</item>
    ///         <item>a trigger that only sets the camera, or only opens messages, besides its own setup:
    ///         <c>CameraLine</c>, <c>CameraBox</c>, <c>DialogueLine</c>, <c>DialogueBox</c> (line: types 2
    ///         and 5, box: 3 and 6);</item>
    ///         <item>another worker that only drives the camera, only opens messages or only sets the lights:
    ///         <c>CameraWorker</c>, <c>DialogueWorker</c>, <c>Lighting</c>.</item>
    ///     </list>
    ///     A role several workers share takes their numbers (<c>CameraLine_21</c>, <c>CameraLine_25</c>), as
    ///     model names do; a name the source already has is not taken. <paramref name="names"/> is changed in
    ///     place, so the decompilers that hold it print the new names.
    /// </summary>
    public static void AssignRoles(ScriptCategory category, WorkerDecompiler[] workers, string[] names, string[] defaults, IEnumerable<string> taken) {
        Domain maps = GameNames.Enum(typeof(Ffx.Map))!;
        HashSet<string> reserved = new(taken, StringComparer.Ordinal);
        reserved.UnionWith(Reserved.Value);
        reserved.UnionWith(names);
        string?[] roles = new string?[workers.Length];
        for (int w = 0; w < workers.Length; w++) {
            if (names[w] != defaults[w] || workers[w].Type == 0) continue;
            roles[w] = Role(workers[w], category, maps);
        }
        foreach (IGrouping<string, int> g in Enumerable.Range(0, workers.Length).Where(w => roles[w] is not null).GroupBy(w => roles[w]!)) {
            foreach (int w in g) {
                string name = g.Count() == 1 ? g.Key : $"{g.Key}_{defaults[w][1..]}";
                if (!reserved.Contains(name)) names[w] = name;
            }
        }
    }

    private static string? Role(WorkerDecompiler worker, ScriptCategory category, Domain maps) {
        int type = worker.Type;
        bool trigger = category == ScriptCategory.Event && type is 2 or 3 or 5 or 6;
        HashSet<ushort> ids = worker.NativeIds();
        if (trigger && ids.Overlaps(MapChanges) && !ids.Contains(JumpToEntrance)) {
            List<int?> to = worker.FirstArguments(MapChanges);
            if (to.Count > 0 && to.All(m => m is not null) && to.Distinct().Count() == 1 && maps.Member(to[0]!.Value) is string map) {
                return "ExitTo" + map[(map.IndexOf('.', StringComparison.Ordinal) + 1)..];
            }
        }
        if (category == ScriptCategory.Event && !trigger && worker.DirectsStory()) return "Director";
        HashSet<Use> uses = [.. ids.Select(UseOf).Where(u => u is not Use.Neutral)];
        if (trigger) {
            uses.Remove(Use.Trigger);
            string shape = type is 2 or 5 ? "Line" : "Box";
            return uses.Count != 1 ? null : uses.First() switch {
                Use.Camera => "Camera" + shape,
                Use.Dialogue => "Dialogue" + shape,
                _ => null,
            };
        }
        if (uses.Contains(Use.Light)) uses.Remove(Use.Bones);
        return uses.Count != 1 ? null : uses.First() switch {
            Use.Camera => "CameraWorker",
            Use.Dialogue => "DialogueWorker",
            Use.Light => "Lighting",
            _ => null,
        };
    }

    /// <summary>
    ///     English names of actor models by <see cref="Ffx.Model"/> member (<c>data/actor-names.json</c>,
    ///     hand-written: only models whose character is certain).
    /// </summary>
    internal static readonly Lazy<Dictionary<string, string>> ActorNames = new(() => {
        using Stream stream = typeof(WorkerNaming).Assembly.GetManifestResourceStream("actor-names.json")
                              ?? throw new InvalidOperationException("actor-names.json is not embedded");
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(stream);
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (System.Text.Json.JsonProperty p in doc.RootElement.GetProperty("actors").EnumerateObject()) names[p.Name] = p.Value.GetProperty("name").GetString()!;
        return names;
    });

    /// <summary>The names a worker or an entry method must not take: <see cref="Reserved"/> and <paramref name="taken"/>.</summary>
    public static HashSet<string> ReservedWith(IEnumerable<string> taken) {
        HashSet<string> names = new(Reserved.Value, StringComparer.Ordinal);
        names.UnionWith(taken);
        return names;
    }

    /// <summary>Names a worker class must not take: the reference assembly's types and the members of <c>Worker</c>.</summary>
    private static readonly Lazy<HashSet<string>> Reserved = new(() => {
        System.Reflection.Assembly game = typeof(NativeAttribute).Assembly;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (Type t in game.GetExportedTypes()) names.Add(t.Name.Split('`')[0]);
        foreach (MemberInfo m in typeof(Ffx.Atel.Worker).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) names.Add(m.Name);
        return names;
    });
}
