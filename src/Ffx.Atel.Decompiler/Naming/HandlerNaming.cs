// SPDX-License-Identifier: MIT

using System.Globalization;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler.Naming;

/// <summary>
///     Names for monster and battle workers and their entries from the file's handler table
///     (<see cref="HandlerTable"/>, section 1): which worker, and which of its entries, the battle runs for which
///     of its signals. FFX.exe asks for a handler through <c>MsAtelRequestExe</c> (<c>0x00797d50</c>) and its
///     callers, which put the signal <c>n</c> and a value <c>k</c> in a request; the handler's table maps
///     <c>k</c> to the entry the worker runs at level 7 (ffx-rust <c>crates/ffx-battle/src/atel.rs</c>,
///     <c>action.rs</c>; <c>docs/research/handlers.md</c> in this repo). A worker handles at most one signal in every shipped
///     file, and an entry at most one <c>k</c>, so each gets one name.
/// </summary>
internal static class HandlerNaming {
    /// <summary>
    ///     What the battle's action requests (<c>MsActionRequest</c>, <c>0x007aceb0</c>) ask for, by <c>k</c>: a
    ///     monster's handler <c>0x3d</c>, a party member's <c>0x29</c> + actor of the battle file, and the voice
    ///     cues <c>MsActionRequest</c> sends with the same <c>k</c> (<c>MsSetVoiceCheck</c>, up to 8).
    /// </summary>
    private static readonly string?[] ActionKinds = [
        "OnTurn",        // 0: MsCommandCueProcess, the actor's turn when its cue entry has no command yet: the command the script uses goes to the command cue
        "OnTurnStart",   // 1: MsBattleProcess_InputCue_00792a90, its turn comes up (once per turn, actor +0xdf7)
        "OnTargeted",    // 2: MsCommandCueProcess through MsActionTarget, every target of a command before its damage (who: the attacker; a command inserted)
        "OnHit",         // 3: AfterDamageProcess, on each actor a command hurt (who: the attacker; a command inserted, the counter); also a counter entry's turn
        "OnDeath",       // 4: MsDamageCheckDeath, it dies (who: the killer)
        "OnEnter",       // 5: MsBtlReadMonsterProcess, MsBtlReadPlayerProcess: it comes into the battle
        "OnTurnEnd",     // 6: MsDeleteInputCue, its input entry goes and the next turn is counted
        "OnActionEnd",   // 7: MsEffectBackToIdle, MsEffectStart (state 1), MsParseCommand: its command has played
        "OnPoison",      // 8: MsPoisonProcess, poison damage after its turn (who: itself)
    ];

    /// <summary>The command class of each magic camera handler (<c>MsAtelRequestMagicCamera</c>, <c>0x00798000</c>): its <c>k</c> is the command ID's low 12 bits.</summary>
    private static readonly Dictionary<int, (string Worker, int Class)> CommandCameras = new() {
        [0x41] = ("CommandCameras", 0x3000),
        [0x44] = ("ItemCameras", 0x2000),
        [0x47] = ("MonsterCommandCameras", 0x4000),
        [0x4a] = ("BossCommandCameras", 0x6000),
    };

    /// <summary>A handler's worker name for signal <paramref name="signal"/> in a script of <paramref name="category"/>; null for one we do not know.</summary>
    public static string? WorkerName(ScriptCategory category, int signal) {
        if (category == ScriptCategory.Monster) {
            return signal switch {
                0x00 => "Cameras",         // MsAtelRequestCamera: the camera numbers, with the battle's
                0x3d => "Ai",              // MsActionRequest: its turn, hits, death (ActionKinds)
                0x40 => "Motions",         // MsSetMotion: asked before the battle's own motion handler
                0x04 => "DefaultMotions",  // MsSetMotion: asked after the battle's own motion handler
                _ => null,
            };
        }
        if (category != ScriptCategory.Battle) return null;
        return signal switch {
            0x00 => "Cameras",
            0x03 => "Motions",                                   // MsSetMotion: every actor's, after its own
            >= 0x05 and <= 0x20 => Actor(signal - 0x05, "Motions"),  // MsSetMotion: party member i's, i + 5
            >= 0x25 and <= 0x28 => Actor(signal - 0x17, "SpecialActions"), // MsActionRequest with special, actors 0xe-0x11
            >= 0x29 and <= 0x3a => Actor(signal - 0x29, "Actions"), // MsActionRequest: party member i's, i + 0x29
            0x3e => "SystemRequests",                            // MsAtelRequestSystem (native 0x7097) for k below 8
            0x3f => "SystemRequests2",                           // MsAtelRequestSystem for k 8-0x17
            0x4d => "PartyVoices",                               // MsSetVoice: 0x4d for party members
            0x4e => "MonsterVoices",                             // MsSetVoice: 0x4e for monsters
            >= 0x4f and <= 0x6a => Actor(signal - 0x4f, "Voices"),   // MsSetVoice: speaker i, i + 0x4f
            0x6c => "BattleEvents",                              // MsAtelRequestSystemCheckExe: first
            0x89 => "BattleEvents2",                             // MsAtelRequestSystemCheckExe: second
            >= 0x6d and <= 0x88 => Actor(signal - 0x6d, "VoiceCues"), // MsSetVoiceCheck: actor i, i + 0x6d
            _ when CommandCameras.TryGetValue(signal, out (string Worker, int) c) => c.Worker,
            _ => null,
        };
    }

    /// <summary>An entry name for value <paramref name="k"/> of signal <paramref name="signal"/>'s handler; null for one we do not know.</summary>
    public static string? EntryName(ScriptCategory category, int signal, int k) {
        string? Kind() => k < ActionKinds.Length ? ActionKinds[k] : null;
        string N(string prefix) => prefix + k.ToString(CultureInfo.InvariantCulture);
        if (category == ScriptCategory.Monster) {
            return signal switch {
                0x00 => N("OnCamera"),
                0x3d => Kind(),
                0x40 or 0x04 => N("OnMotion"),
                _ => null,
            };
        }
        if (category != ScriptCategory.Battle) return null;
        return signal switch {
            0x00 => N("OnCamera"),
            0x03 or (>= 0x05 and <= 0x20) => N("OnMotion"),
            >= 0x25 and <= 0x3a => Kind(),
            >= 0x6d and <= 0x88 => Kind(),
            0x3e or 0x3f => N("OnRequest"),
            >= 0x4d and <= 0x6a => N("OnLine"),
            0x6c or 0x89 => k switch {
                5 => "OnBattleStart",    // the battle starts (MsBtlReadManage, 0x00783020, its last step)
                4 => "OnBattleEnd",      // its end status is known (MsBattleProcess_Status_007917d0, step 0x17)
                1 => "OnPartyDefeated",  // the end: no party member still fights (MsGetBattleEndStatus 1, step 0x1d)
                _ => null,
            },
            _ when CommandCameras.TryGetValue(signal, out (string, int Class) c)
                   && GameNames.Enum(typeof(Ffx.Command))!.Member(c.Class | k) is string member => "On" + member[(member.IndexOf('.', StringComparison.Ordinal) + 1)..],
            _ => null,
        };
    }

    /// <summary>Actor <paramref name="i"/>'s <see cref="Ffx.Target"/> name and the role: <c>TidusActions</c>.</summary>
    private static string? Actor(int i, string role) =>
        GameNames.Enum(typeof(Ffx.Target))!.Member(i) is string m ? m[(m.IndexOf('.', StringComparison.Ordinal) + 1)..] + role : null;

    /// <summary>
    ///     Names the workers that still have their default name (<c>W03</c>) after the handler they serve; a name the
    ///     source already has is not taken. <paramref name="names"/> is changed in place.
    /// </summary>
    public static void AssignWorkers(ScriptCategory category, HandlerTable? table, string[] names, string[] defaults, ISet<string> reserved) {
        if (table is null) return;
        HashSet<string> used = new(names, StringComparer.Ordinal);
        foreach (IGrouping<int, Handler> g in table.Handlers.Where(h => h.Worker < names.Length).GroupBy(h => h.Worker)) {
            int w = g.Key;
            if (names[w] != defaults[w] || g.Count() != 1) continue;
            if (WorkerName(category, g.First().Signal) is not string name || reserved.Contains(name) || used.Contains(name)) continue;
            names[w] = name;
            used.Add(name);
        }
    }

    /// <summary>
    ///     Names the entry methods of each handler's worker after the value its table maps to them, where the
    ///     entry starts a method of its own and the overlay has not named it, and the name is free in the worker.
    /// </summary>
    public static void AssignEntries(ScriptCategory category, HandlerTable? table, WorkerDecompiler[] workers, string[] workerNames, ISet<string> reserved) {
        if (table is null) return;
        foreach (Handler h in table.Handlers) {
            if (h.Worker >= workers.Length) continue;
            WorkerDecompiler worker = workers[h.Worker];
            HashSet<int> heads = [.. worker.MethodEntries()];
            HashSet<string> used = new(worker.EntryNames.Values, StringComparer.Ordinal) { workerNames[h.Worker] };
            used.UnionWith(worker.OwnMethodNames);
            for (int k = 0; k < h.Entries.Count; k++) {
                if (h.Entries[k] is not int e || !heads.Contains(e) || worker.EntryNames.ContainsKey(e)) continue;
                if (EntryName(category, h.Signal, k) is not string name || reserved.Contains(name) || !used.Add(name)) continue;
                worker.EntryNames[e] = name;
            }
        }
    }
}
