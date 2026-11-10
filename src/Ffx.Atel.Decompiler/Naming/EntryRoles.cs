// SPDX-License-Identifier: MIT

using System.Globalization;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler.Naming;

/// <summary>
///     Names for the entry methods that still have their number (<c>E5</c>, <c>E5Async</c>) after the overlay and
///     the battle's handlers (docs/source-repo.md, "Names"), by fixed rules, in this order:
///     <list type="number">
///         <item>
///             <b>Trigger entries</b> of an event worker: the entry the engine requests when the player talks to,
///             touches or crosses the worker, by the type's default table (FFX.exe <c>0x00c52ad8</c>, ffx-rust
///             <c>docs/formats/script.md</c>, "Trigger entries") and the worker's own <c>SetTriggerEntry</c> with
///             constants: <c>OnTalk</c>, <c>OnTouch</c>, <c>OnCross</c>, <c>OnTouchBegin</c>, <c>OnTouchEnd</c>; a line
///             sensor's entry 2 <c>OnCross</c>, a box sensor's 2 and 3 <c>OnTouchBegin</c>, <c>OnTouchEnd</c>.
///             Only kinds the worker can receive: a kind's bit in worker <c>+0xab</c> is on by default
///             (<c>0xf7</c> for an actor, so no touch; <c>0xff</c> for triggers and sensors), turned on by
///             <c>EnableTrigger(kind)</c>, and an actor is talked to only when it has a model.
///         </item>
///         <item>
///             <b>What the entry does</b>, read off the natives its code calls (inline methods' code included):
///             only message windows and voices showing constant texts: <c>Say12</c> (one text),
///             <c>Say12To15</c> (several, in order), <c>Say12Etc</c>; only one kind of native besides
///             waiting: <see cref="OnlyNames"/>; otherwise a change to one constant map: <c>GoToKami0100</c>;
///             otherwise texts among other code: <c>Scene12</c> (the first text it shows).
///         </item>
///     </list>
///     A name several methods of a worker would take gets each method's entry (<c>Hide_7</c>, <c>Hide_9</c>),
///     as workers do; a name the worker, its script or the reference assembly already uses is not taken.
/// </summary>
internal static class EntryRoles {
    /// <summary>A trigger kind's name, by kind (null: not identified, ffx-rust's table).</summary>
    private static readonly string?[] KindNames = ["OnTalk", null, null, "OnTouch", "OnCross", "OnTouchBegin", "OnTouchEnd", null];

    /// <summary>The default entry per trigger kind 0-7, by worker type (FFX.exe <c>0x00c52ad8</c>; -1: none).</summary>
    private static int[]? DefaultEntries(int type) => type switch {
        1 => [2, 3, 4, 5, -1, -1, -1, 1],
        2 or 3 => [2, 3, -1, 5, 4, 6, 7, 1],
        _ => null,
    };

    /// <summary>The kinds a worker type's checks request by default with a level (<c>0x00c52ac0</c>, not <c>0xff</c>).</summary>
    private static bool HasLevel(int type, int kind) => type switch {
        1 => kind is 0 or 1 or 2 or 3,
        2 or 3 => kind is 0 or 1 or 3 or 4 or 5 or 6,
        _ => false,
    };

    /// <summary>The worker's <c>+0xab</c> at start (FFX.exe <c>Atel_InitWorker</c>, ffx-rust script.md).</summary>
    private static int DefaultBits(int type) => type switch {
        1 => 0xf7,
        2 or 3 or 5 or 6 => 0xff,
        _ => 0,
    };

    private const ushort SetModel = 0x0001, EnableTrigger = 0x0034, DisableTrigger = 0x0035, MsgSetText = 0x0064, Hide = 0x5008, BodyHit = 0x500f;

    /// <summary>The natives that change the map, their first argument the <see cref="Ffx.Map"/> (as <see cref="WorkerNaming"/>).</summary>
    private static readonly HashSet<ushort> MapChanges = [0x0011, 0x010b, 0x010c, 0x00ab];

    /// <summary>What a native is for, as far as these names go.</summary>
    private enum Use { Neutral, Dialogue, Camera, Motion, Turn, Move, Place, Sound, Effect, Light, Visibility, Unload, Sprite, Fade, Other }

    /// <summary>What a character does with itself (<c>Walk</c>, <c>TurnInPlace</c>, <c>PlayMotion</c>, <c>Place</c>).</summary>
    private static readonly HashSet<Use> Body = [Use.Move, Use.Turn, Use.Motion, Use.Place, Use.Visibility];

    /// <summary>The name of an entry that calls only natives of one <see cref="Use"/> besides waiting.</summary>
    private static readonly Dictionary<Use, string> OnlyNames = new() {
        [Use.Camera] = "MoveCamera",
        [Use.Motion] = "PlayMotion",
        [Use.Turn] = "TurnInPlace",
        [Use.Move] = "Walk",
        [Use.Sound] = "PlaySound",
        [Use.Effect] = "RunEffects",
        [Use.Light] = "SetLights",
        [Use.Sprite] = "DrawSprites",
        [Use.Fade] = "FadeScreen",
        [Use.Place] = "Place",
    };

    /// <summary>A native's use by its reference assembly name (FFX.exe's, <c>data/natives.json</c>).</summary>
    private static Use UseOf(ushort id) {
        string? name = ScriptDecompiler.Natives[id]?.Name;
        if (name is null) return Use.Other;
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string cls = name[..dot], m = name[(dot + 1)..];
        bool S(string p) => m.StartsWith(p, StringComparison.Ordinal);
        if (cls == "Common" && (S("Wait") || S("Random") || m is "GetWorkerId" or "ControlDisable" or "ControlEnable")) return Use.Neutral;
        if (name is "Chr.WaitCdIdle") return Use.Neutral;
        if (S("Msg") || S("Voice") || name is "Common.SoundWaitPoll" or "Common.MacroDicText"
            or "Chr.SetAutoTalkOf" or "Chr.SetAutoTalk" or "Common.PadPressed" or "Common.AlwaysTrue019c") return Use.Dialogue;
        if (cls == "Cam" || name == "Common.AttachCamera") return Use.Camera;
        if (cls == "Chr" && m.Contains("Motion", StringComparison.Ordinal)) return Use.Motion;
        if (cls == "Common" && (m is "SetPos" or "SetPosition" or "SetYaw" or "WorkerStopMove" or "WorkerStopTurn" || S("GetEntrance"))
            || name is "Chr.SetOffset" or "Chr.SetScale" or "Chr.SetGroundFollow" or "Chr.SetGroundMode") return Use.Place;
        if (cls == "Common" && (S("Turn") || S("SetTurn") || S("GetTurn") || m is "AngleToPoint" or "PitchToPoint" or "GetWorkerPos"
                                || S("GetWorker") && m.Length == "GetWorkerX".Length)) return Use.Turn;
        if (cls == "Common" && (S("Move") || S("SetMove") || S("SetArriveRadius") || S("WorkerStopMove") || S("StopMove") || S("GetYawToMove") || S("GetPitchToMove"))) return Use.Move;
        if (cls == "Common" && (S("Sfx") || S("Snd") || S("Sep") || S("Music"))) return Use.Sound;
        if (cls == "Fx") return Use.Effect;
        if (name is "Screen.LightSet" or "Chr.RefreshAllBones" or "Chr.AllBoneUpdate" or "Chr.SetBoneUpdateOf") return Use.Light;
        if (name is "Chr.Hide" or "Chr.SetBodyHit" or "Common.EnableTrigger" or "Common.DisableTrigger" or "Chr.FadeAlpha" or "Chr.SetDistanceCull"
            || cls == "Chr" && S("SetHide")) return Use.Visibility;
        if (name is "Common.WorkerDetach" or "Common.WorkerDetachAndDispose" or "Chr.DisposeData" or "Chr.DisposeDataWithCheck") return Use.Unload;
        if (cls == "Common" && S("Sprite")) return Use.Sprite;
        if (cls == "Screen" && S("Fade") || cls == "Common" && S("SetMapFade")) return Use.Fade;
        return Use.Other;
    }

    /// <summary>What a method's code does, as far as the names go.</summary>
    private sealed class Facts {
        public HashSet<Use> Uses { get; } = [];
        public List<int> Texts { get; } = [];
        public bool ComputedText, Requests, Writes, Calls;
        public List<int?> Maps { get; } = [];
        public List<(ushort Native, int? Arg)> Visibility { get; } = [];

        /// <summary>Calls per use, waiting left out.</summary>
        public Dictionary<Use, int> Counts { get; } = [];

        /// <summary>The natives called, waiting left out.</summary>
        public HashSet<ushort> Natives { get; } = [];

        public bool Voice;
    }

    private static int? Constant(Expr e) => e switch {
        IntLit l => l.Value,
        PoolExpr { Code: Opcodes.PushC } p => (int)p.Value,
        _ => null,
    };

    private static Facts Read(IReadOnlyList<Stmt> body) {
        Facts f = new();
        void Native(ushort id, IReadOnlyList<Expr> args) {
            Use use = UseOf(id);
            if (use != Use.Neutral) {
                f.Uses.Add(use);
                f.Counts[use] = f.Counts.GetValueOrDefault(use) + 1;
                f.Natives.Add(id);
            }
            if (ScriptDecompiler.Natives[id]?.Name.StartsWith("Common.Voice", StringComparison.Ordinal) == true) f.Voice = true;
            if (id == MsgSetText && args.Count == 2) {
                if (Constant(args[1]) is int t) f.Texts.Add(t);
                else f.ComputedText = true;
            }
            if (MapChanges.Contains(id)) f.Maps.Add(args.Count > 0 ? Constant(args[0]) : null);
            if (use == Use.Visibility) f.Visibility.Add((id, args.Count > 0 ? Constant(args[0]) : null));
        }
        Inline.Walk(body, e => {
            if (e is CallExpr c) Native(c.Native, c.Args);
        }, s => {
            switch (s) {
                case CallStmt c: Native(c.Native, c.Args); break;
                case RequestStmt: f.Requests = true; break;
                case CallWorkerStmt: f.Calls = true; break;
                case AssignStmt or CompoundStmt: f.Writes = true; break;
                case OpArgsStmt: f.Uses.Add(Use.Other); break;
            }
        });
        return f;
    }

    /// <summary>The name the code of one method gives it (rule 2), or null.</summary>
    private static string? ContentName(IReadOnlyList<Stmt> body, Domain maps) {
        Facts f = Read(body);
        bool plain = !f.Requests && !f.Calls && !f.Writes;
        if (plain && f.Uses.SetEquals([Use.Dialogue]) && f.Texts.Count > 0 && !f.ComputedText) return "Say" + Texts(f.Texts);
        if (plain && f.Uses.SetEquals([Use.Dialogue]) && f.Texts.Count == 0 && !f.ComputedText && f.Voice) return "PlayVoice";
        if (plain && f.Uses.Count == 1 && OnlyNames.TryGetValue(f.Uses.First(), out string? only)) return only;
        // A character's own movement: walking, turning, a motion, being placed (and shown or hidden on the way).
        if (!f.Requests && !f.Calls && f.Uses.Count > 0 && f.Uses.IsSubsetOf(Body)) {
            if (f.Uses.Contains(Use.Move)) return "Walk";
            if (f.Uses.Contains(Use.Turn)) return "TurnInPlace";
            if (f.Uses.Contains(Use.Motion)) return "PlayMotion";
            if (f.Uses.Contains(Use.Place)) return "Place";
        }
        // Effects and their sounds: RunEffects; letting go of the model (Detach, DisposeData), also hidden first: Unload.
        if (plain && f.Uses.Contains(Use.Effect) && f.Uses.IsSubsetOf([Use.Effect, Use.Sound])) return "RunEffects";
        if (plain && f.Uses.Contains(Use.Unload) && f.Uses.IsSubsetOf([Use.Unload, Use.Visibility, Use.Move, Use.Place])) return "Unload";
        // Shown or hidden, with triggers, collision and effects: Hide, Show.
        if (plain && f.Uses.Contains(Use.Visibility) && f.Uses.IsSubsetOf([Use.Visibility, Use.Effect, Use.Sound])) {
            int?[] hides = [.. f.Visibility.Where(v => v.Native == Hide).Select(v => v.Arg).Distinct()];
            if (hides is [1]) return "Hide";
            if (hides is [0]) return "Show";
            if (hides.Length == 0 && f.Visibility.All(v => v.Native == BodyHit) && f.Visibility.Select(v => v.Arg).Distinct().ToArray() is [int on]) {
                return on != 0 ? "BodyHitOn" : "BodyHitOff";
            }
            if (hides.Length == 0 && f.Visibility.All(v => v.Native == DisableTrigger)) return "DisableTriggers";
            if (hides.Length == 0 && f.Visibility.All(v => v.Native == EnableTrigger)) return "EnableTriggers";
        }
        if (f.Maps.Count > 0 && f.Maps.All(m => m is not null) && f.Maps.Distinct().Count() == 1 && maps.Member(f.Maps[0]!.Value) is string map) {
            return "GoTo" + map[(map.IndexOf('.', StringComparison.Ordinal) + 1)..];
        }
        if (f.Texts.Count > 0) return "Scene" + f.Texts[0].ToString(CultureInfo.InvariantCulture);
        // One native (any number of times) besides waiting: its name (SetDistanceCull, TriggerSetActive).
        if (plain && f.Natives.Count == 1 && ScriptDecompiler.Natives[f.Natives.First()]?.Name is string native) {
            return native[(native.IndexOf('.', StringComparison.Ordinal) + 1)..];
        }
        // Most of the calls (two thirds, at least six) of one use: a long walk, a camera move.
        int calls = f.Counts.Values.Sum();
        if (!f.Requests && !f.Calls && calls >= 6 && f.Counts.MaxBy(kv => kv.Value) is var (top, n) && n * 3 >= calls * 2) {
            if (OnlyNames.TryGetValue(top, out string? most)) return most;
        }
        return null;
    }

    /// <summary><c>12</c>, <c>12To15</c> (increasing), <c>12Etc</c>.</summary>
    private static string Texts(List<int> texts) {
        string first = texts[0].ToString(CultureInfo.InvariantCulture);
        if (texts.Count == 1) return first;
        bool increasing = texts.Zip(texts.Skip(1)).All(p => p.Second > p.First);
        return increasing ? first + "To" + texts[^1].ToString(CultureInfo.InvariantCulture) : first + "Etc";
    }

    /// <summary>
    ///     The trigger names of a worker's entries (rule 1): entry → name, for an event worker of type 1, 2 or 3.
    /// </summary>
    private static Dictionary<int, string> TriggerNames(WorkerDecompiler worker, IReadOnlyList<StructuredMethod> methods) {
        Dictionary<int, string> found = [];
        int type = worker.Type;
        // A sensor (types 5, 6) checks another worker against its line or box at level 1, every kind of
        // trigger running entry 2 (a box's kind 6, a touch ending, entry 3): FFX.exe rows 2 and 3.
        if (type is 5 or 6) {
            bool setsTable = false;
            foreach (StructuredMethod m in methods) Inline.Walk(m.Body, _ => { }, s => setsTable |= s is OpArgsStmt { Op: Opcodes.Op79 });
            if (setsTable) return found;
            found[2] = type == 5 ? "OnCross" : "OnTouchBegin";
            if (type == 6) found[3] = "OnTouchEnd";
            return found;
        }
        if (DefaultEntries(type) is not int[] defaults) return found;
        HashSet<int> enabled = [], disabled = [];
        bool anyEnable = false;
        List<(int Kind, int Entry)> set = [];
        bool setComputed = false;
        foreach (StructuredMethod m in methods) {
            Inline.Walk(m.Body, _ => { }, s => {
                switch (s) {
                    case CallStmt { Native: EnableTrigger } c:
                        if (c.Args.Count == 1 && Constant(c.Args[0]) is int k) enabled.Add(k);
                        else anyEnable = true;
                        break;
                    case CallStmt { Native: DisableTrigger } c:
                        if (c.Args.Count == 1 && Constant(c.Args[0]) is int d) disabled.Add(d);
                        break;
                    case OpArgsStmt { Op: Opcodes.Op79, Args: [_, Expr kind, Expr entry] }:
                        if (Constant(kind) is int sk && Constant(entry) is int se) set.Add((sk, se));
                        else setComputed = true;
                        break;
                }
            });
        }
        bool hasModel = worker.NativeIds().Contains(SetModel);
        bool Receives(int kind) {
            if (!HasLevel(type, kind) || KindNames[kind] is null) return false;
            if (type == 1 && kind == 0 && !hasModel) return false;
            bool on = (DefaultBits(type) >> kind & 1) != 0;
            return enabled.Contains(kind) || anyEnable || on && !disabled.Contains(kind);
        }
        if (setComputed) return found;
        // A default entry, then those the worker points a kind at; a name twice gets the entry (OnTalk, OnTalk_6).
        List<(int Entry, string Name)> names = [];
        for (int kind = 0; kind < 8; kind++) {
            if (defaults[kind] >= 0 && Receives(kind)) names.Add((defaults[kind], KindNames[kind]!));
        }
        foreach ((int kind, int entry) in set) {
            if (kind is >= 0 and < 8 && Receives(kind) && !names.Contains((entry, KindNames[kind]!))) names.Add((entry, KindNames[kind]!));
        }
        foreach (IGrouping<int, (int Entry, string Name)> g in names.GroupBy(n => n.Entry)) {
            // An entry several kinds request keeps the first (talk before touch, …).
            found[g.Key] = g.First().Name;
        }
        foreach (IGrouping<string, int> g in found.Keys.ToList().GroupBy(e => found[e]).Where(g => g.Count() > 1)) {
            int first = defaults[Array.IndexOf(KindNames, g.Key)];
            foreach (int e in g.Where(e => e != first)) found[e] = string.Create(CultureInfo.InvariantCulture, $"{g.Key}_{e}");
        }
        return found;
    }

    /// <summary>Names the entry methods of <paramref name="workers"/> that have no name yet (see the class).</summary>
    public static void Assign(ScriptCategory category, WorkerDecompiler[] workers, string[] workerNames, ISet<string> reserved) {
        Domain maps = GameNames.Enum(typeof(Ffx.Map))!;
        for (int w = 0; w < workers.Length; w++) {
            WorkerDecompiler worker = workers[w];
            HashSet<int> heads = [.. worker.MethodEntries()];
            if (heads.Count == 0) continue;
            IReadOnlyList<StructuredMethod> methods = worker.Bodies;
            HashSet<string> used = new(worker.EntryNames.Values, StringComparer.Ordinal);
            used.UnionWith(worker.OwnMethodNames);
            used.UnionWith(workerNames);
            bool Free(string n) => !reserved.Contains(n) && !used.Contains(n) && !used.Contains(n + "Async");

            Dictionary<int, string> chosen = [];
            if (category == ScriptCategory.Event) {
                foreach ((int e, string n) in TriggerNames(worker, methods)) {
                    if (heads.Contains(e) && !worker.EntryNames.ContainsKey(e)) chosen[e] = n;
                }
            }
            foreach (StructuredMethod m in methods) {
                int head = m.Entries[0];
                if (!heads.Contains(head) || worker.EntryNames.ContainsKey(head) || chosen.ContainsKey(head)) continue;
                if (ContentName(m.Body, maps) is string n) chosen[head] = n;
            }
            // A name several methods take gets each one's entry.
            foreach (IGrouping<string, int> g in chosen.Keys.ToList().GroupBy(e => chosen[e])) {
                foreach (int e in g) {
                    string name = g.Count() == 1 ? g.Key : string.Create(CultureInfo.InvariantCulture, $"{g.Key}_{e}");
                    if (!Free(name)) continue;
                    worker.EntryNames[e] = name;
                    used.Add(name);
                }
            }
        }
    }
}
