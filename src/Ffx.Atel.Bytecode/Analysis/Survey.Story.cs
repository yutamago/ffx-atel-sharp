// SPDX-License-Identifier: MIT

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Bytecode.Analysis;

public sealed partial class Survey {
    /// <summary>
    ///     Story progress: <c>g_SaveData + 0xbec</c>, a <c>u16</c> (ffx-rust's docs/formats/save.md, <c>g_SaveScenario</c>,
    ///     read by <c>AtelGetScenarioFlag</c>; Fahrenheit's <c>story_progress</c>).
    /// </summary>
    public const int StoryProgressSaveOffset = 0xbec;

    /// <summary>
    ///     Where storage 0 starts in the save data: <c>AtelSetUpCtrlWork</c> (<c>0x00871190</c>) sets the ctrl work's
    ///     <c>+0x2c</c> to <c>MsGetSaveEventAddress() + 0x1ec</c> (<c>&amp;g_SaveData + 0x1ec</c>) unless it is already set.
    /// </summary>
    public const int Storage0SaveOffset = 0x1ec;

    /// <summary>Story progress as a storage 0 offset (<c>0xa00</c>).</summary>
    public const int StoryProgressOffset = StoryProgressSaveOffset - Storage0SaveOffset;

    private static bool IsStoryVar(ScriptBlock b, int w, ushort v) =>
        b.Variable(w, v) is (uint loc, _) && (loc >> 25 & 7) == 0 && (loc & 0xff_ffff) == StoryProgressOffset;

    private void MeasureStory() {
        Section("Story progress");
        long reads = 0, writes = 0, constWrites = 0, scripts = 0;
        Dictionary<string, long> types = [], writeKinds = [], readUses = [];
        HashSet<string> readers = [];
        StoryWrites.Clear();
        foreach (SurveyScript s in _scripts) {
            bool any = false;
            foreach (WorkerFlow f in s.Flows) {
                for (int i = 0; i < f.Code.Count; i++) {
                    Instr ins = f.Code[i];
                    if (ins.Opcode.Operand != OperandKind.Var || !IsStoryVar(s.Block, f.WorkerIndex, ins.Operand!.Value)) continue;
                    any = true;
                    Bump(types, VarType(s.Block, f.WorkerIndex, ins.Operand!.Value));
                    if (ins.Code is Opcodes.PopV or Opcodes.PopV1 or Opcodes.PopVA or Opcodes.PopVA1) {
                        writes++;
                        int p = f.SingleOperand(i, ins.Code is Opcodes.PopVA or Opcodes.PopVA1 ? 1 : 0);
                        if (p >= 0 && f.IntValue(p) is int v) {
                            constWrites++;
                            if (!StoryWrites.TryGetValue(v, out SortedSet<string>? set)) StoryWrites[v] = set = new(StringComparer.Ordinal);
                            set.Add(s.Name);
                            Bump(writeKinds, $"{s.Kind}{(f.Reachable[i] ? "" : ", unreachable")}");
                        }
                        else Bump(writeKinds, "not a constant");
                    }
                    else {
                        reads++;
                        readers.Add(s.Name);
                        int u = f.Consumer[i];
                        Bump(readUses, u >= 0 ? f.Code[u].Opcode.Name : "-");
                    }
                }
            }
            if (any) scripts++;
        }
        Line($"`g_SaveData + 0x{StoryProgressSaveOffset:x}` (ffx-rust `docs/formats/save.md`: `g_SaveScenario`, read by `AtelGetScenarioFlag`; Fahrenheit's `story_progress`). "
             + $"Storage 0 starts at `g_SaveData + 0x{Storage0SaveOffset:x}` (`AtelSetUpCtrlWork`, `0x00871190`, sets the ctrl work's `+0x2c` to `MsGetSaveEventAddress() + 0x{Storage0SaveOffset:x}`), "
             + $"so scripts reach it as storage 0 offset `0x{StoryProgressOffset:x}`, typed {string.Join(", ", types.Select(k => $"`{k.Key}` ({N(k.Value)} uses)"))}, in {N(scripts)} scripts.");
        Line();
        Line($"{N(writes)} stores, {N(constWrites)} of a constant ({string.Join(", ", writeKinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {N(k.Value)}"))}); "
             + $"{N(StoryWrites.Count)} distinct values, from {N(StoryWrites.Values.SelectMany(x => x).Distinct().Count())} scripts. "
             + $"{N(StoryWrites.Count(k => k.Value.Count == 1))} values are set by exactly one script, so a value can be named after it; "
             + $"{N(StoryWrites.Count(k => k.Value.Count > 1))} by several.");
        static bool Dev(string n) => n.StartsWith("test", StringComparison.Ordinal) || n.StartsWith("sample", StringComparison.Ordinal);
        List<KeyValuePair<int, List<string>>> real = [.. StoryWrites.Select(k => KeyValuePair.Create(k.Key, k.Value.Where(n => !Dev(n)).ToList())).Where(k => k.Value.Count > 0)];
        Line($"Leaving out the developer maps (`test*`, `sample*`): {N(real.Count)} values, {N(real.Count(k => k.Value.Count == 1))} set by exactly one script, "
             + $"{N(real.Count(k => k.Value.Count == 2))} by two, {N(real.Count(k => k.Value.Count > 2))} by more.");
        Line($"{N(reads)} reads in {N(readers.Count)} scripts; consumed by {string.Join(", ", readUses.OrderByDescending(k => k.Value).Take(6).Select(k => $"`{k.Key}` {N(k.Value)}"))}.");
        Line();
        Line("The first values in progress order, without the developer maps (the full table: `atel survey --story <file.json>`):");
        Line();
        Table(["Value", "Set by"], real.Take(12).Select(k => new object?[] { k.Key, string.Join(", ", k.Value.Take(4)) + (k.Value.Count > 4 ? ", …" : "") }));
    }

    /// <summary>The story progress values and the scripts that store each, as JSON.</summary>
    public string StoryJson() {
        if (StoryWrites.Count == 0) MeasureStory();
        JsonArray values = [];
        foreach ((int v, SortedSet<string> set) in StoryWrites) {
            values.Add(new JsonObject { ["value"] = v, ["scripts"] = new JsonArray([.. set.Select(n => (JsonNode)n)]) });
        }
        JsonObject doc = new() {
            ["generator"] = "atel survey",
            ["variable"] = $"storage 0, offset 0x{StoryProgressOffset:x} (g_SaveData + 0x{StoryProgressSaveOffset:x}, story progress)",
            ["values"] = values,
        };
        return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n") + "\n";
    }

    private void MeasureEntryData() {
        Section("Entry numbers as data");
        Line("Whether entry numbers are ever computed or stored (a save holding one would pin it): the entry operand of every request, when it is not a constant.");
        Line();
        Dictionary<string, long> sources = [];
        long nonConst = 0;
        foreach ((SurveyScript s, WorkerFlow f) in AllFlows()) {
            for (int i = 0; i < f.Code.Count; i++) {
                byte op = f.Code[i].Code;
                if (op is not ((>= Opcodes.Req0 and <= Opcodes.Op3b) or (>= Opcodes.Req1 and <= 0x53))) continue;
                int start = f.Operands[i][2];
                int end = i;
                if (start < 0 || (start == end - 1 && f.IntValue(start) is not null)) continue;
                nonConst++;
                string what;
                if (start == end - 1 && f.Code[start].Opcode.Operand == OperandKind.Var) {
                    ushort v = f.Code[start].Operand!.Value;
                    what = s.Block.Variable(f.WorkerIndex, v) is (uint loc, _) ? $"`{f.Code[start].Opcode.Name}` of a storage {loc >> 25 & 7} variable" : "a variable";
                }
                else if (start == end - 1) what = $"`{f.Code[start].Opcode.Name}`";
                else if (f.Code[end - 1].Code == Opcodes.PushVA && s.Block.Variable(f.WorkerIndex, f.Code[end - 1].Operand!.Value) is (uint aloc, _)) {
                    what = $"an element of a storage {aloc >> 25 & 7} array (`pushva`)";
                }
                else what = $"an expression ending in `{f.Code[end - 1].Opcode.Name}`";
                Bump(sources, what);
            }
        }
        Line($"{N(nonConst)} requests take their entry from something other than a constant: "
             + $"{(nonConst == 0 ? "none" : string.Join(", ", sources.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {N(k.Value)}")))}.");
    }
}
