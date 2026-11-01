// SPDX-License-Identifier: MIT
//
// Writes data/natives.json and src/Ffx.Atel.Game/Natives.g.cs: every native function the game's tables
// register, with its signature (argument count and kinds, result kind, whether it polls), its name and what
// is known about it. Phase 1 of docs/plan.md; how the signatures are found is in docs/natives.md.
//
//   - Handlers: the player's FFX.exe (the tables AtelInitTotal registers).
//   - Names and descriptions: ffx-rust's symbols/ffx.sym (Fahrenheit's names merged in).
//   - Argument counts, two ways, compared:
//       exe:      AtelPopStackInteger/Float calls minus AtelPushStack* calls in the handlers and the functions
//                 they call (3 levels), from the Ghidra dump's call_sites.tsv;
//       bytecode: the stack depth before every `func_facc` statement in all 1,621 scripts.
//   - Argument kinds: the order of the handler's own pop calls (the dump's C), else what the scripts push.
//
// Nothing from the game is written but addresses, names, counts and descriptions.
//
// usage: FFX_GAME_DIR=<game> dotnet run tools/gen-natives.cs -- <ffx.sym> <decomp dump dir>

#:project ../src/Ffx.Atel.Bytecode
#:property PublishAot=false

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Natives;
using Ffx.Atel.Bytecode.Script;

if (args.Length != 2 || GameData.GameDir() is not string gameDir) {
    Console.Error.WriteLine("usage: FFX_GAME_DIR=<game> dotnet run tools/gen-natives.cs -- <ffx.sym> <decomp dump dir>");
    return 1;
}
string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, ".."));
string symPath = args[0], dumpDir = args[1];

// ----- FFX.exe and its tables ------------------------------------------------------------------------------
byte[] exe = File.ReadAllBytes(Path.Combine(gameDir, "FFX.exe"));
string exeHash = Convert.ToHexStringLower(SHA256.HashData(exe));
NativeTables tables = new(exe);

// ----- ffx.sym ---------------------------------------------------------------------------------------------
Dictionary<uint, (string Name, string Comment)> symbols = [];
string? symTarget = null;
foreach (string line in File.ReadLines(symPath)) {
    if (line.StartsWith("# Target: FFX.exe sha256 ", StringComparison.Ordinal)) symTarget = line[25..].Trim();
    string[] parts = line.Split('#', 2);
    string[] w = parts[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (w.Length >= 3 && w[1] == "function" && w[0].StartsWith("0x", StringComparison.Ordinal)) {
        symbols[uint.Parse(w[0][2..], NumberStyles.HexNumber)] = (w[2], parts.Length > 1 ? parts[1].Trim() : "");
    }
}
if (symTarget is not null && symTarget != exeHash) Console.Error.WriteLine($"warning: FFX.exe is {exeHash}, ffx.sym describes {symTarget}");

// ----- The dump: call sites and C bodies ------------------------------------------------------------------
static uint Hex(string s) => uint.Parse(s.StartsWith("0x", StringComparison.Ordinal) ? s[2..] : s, NumberStyles.HexNumber);
Dictionary<uint, List<uint>> callees = [];
foreach (string line in File.ReadLines(Path.Combine(dumpDir, "call_sites.tsv")).Skip(1)) {
    string[] f = line.Split('\t');
    if (f.Length < 3 || f[2].StartsWith("0xEXT", StringComparison.Ordinal)) continue;
    uint caller = Hex(f[0]);
    if (!callees.TryGetValue(caller, out List<uint>? l)) callees[caller] = l = [];
    l.Add(Hex(f[2]));
}
Dictionary<uint, string> bodies = [];
Regex header = new(@"^// ==== 0x([0-9a-f]{8}) ====$", RegexOptions.Multiline);
foreach (string file in Directory.EnumerateFiles(Path.Combine(dumpDir, "code"), "*.c")) {
    string text = File.ReadAllText(file);
    MatchCollection ms = header.Matches(text);
    for (int i = 0; i < ms.Count; i++) {
        int end = i + 1 < ms.Count ? ms[i + 1].Index : text.Length;
        bodies[Hex(ms[i].Groups[1].Value)] = text[ms[i].Index..end];
    }
}

const uint PopI = 0x0086df00, PopF = 0x0086de50, PushI = 0x0086e3f0, PushF = 0x0086e3c0;
Dictionary<uint, (int I, int F)> netMemo = [];
(int I, int F) NetPops(uint fn, int depth) {
    if (netMemo.TryGetValue(fn, out var m)) return m;
    netMemo[fn] = (0, 0);
    int ci = 0, cf = 0;
    foreach (uint c in callees.GetValueOrDefault(fn) ?? []) {
        switch (c) {
            case PopI: ci++; break;
            case PopF: cf++; break;
            case PushI: ci--; break;
            case PushF: cf--; break;
            default:
                if (depth < 3) {
                    (int a, int b) = NetPops(c, depth + 1);
                    ci += a;
                    cf += b;
                }
                break;
        }
    }
    return netMemo[fn] = (ci, cf);
}

/// <summary>The handler's own pops in source order ('i'/'f'), or null if it pushes or calls something that pops.</summary>
string? DirectPops(uint fn) {
    if (!bodies.TryGetValue(fn, out string? body)) return null;
    if (body.Contains("AtelPushStack", StringComparison.Ordinal)) return null;
    foreach (uint c in callees.GetValueOrDefault(fn) ?? []) {
        if (c is not (PopI or PopF) && NetPops(c, 1) != (0, 0)) return null;
    }
    StringBuilder sb = new();
    foreach (Match m in Regex.Matches(body, @"AtelPopStack(Integer|Float)\(")) sb.Append(m.Groups[1].Value == "Float" ? 'f' : 'i');
    return sb.ToString();
}

// ----- The scripts: stack simulation -----------------------------------------------------------------------
using var archive = GameData.OpenArchive(gameDir);
List<(string Path, ScriptBlock Block)> scripts = [.. GameData.AllScriptPaths(archive).Select(p => (p, ScriptFile.Parse(archive.Read(p)).Script))];

Dictionary<ushort, NativeSlots> slots = tables.All().ToDictionary(t => t.Id, t => t.Slots);
Dictionary<ushort, int> argcGuess = [];
foreach ((ushort id, NativeSlots s) in slots) {
    var parts = new[] { s.Start, s.Float, s.Int }.Where(h => h is not null).Select(h => NetPops(h!.Value, 0)).ToList();
    argcGuess[id] = parts.Sum(p => p.I + p.F);
}

Dictionary<ushort, long> calls = [];
Dictionary<ushort, HashSet<string>> callers = [];
Dictionary<ushort, Dictionary<int, int>> depthVotes = [];
Dictionary<ushort, int[,]> kindVotes = []; // [position, 0 int / 1 float]
foreach ((string path, ScriptBlock s) in scripts) {
    foreach (Worker w in s.Workers) {
        foreach (Instr ins in s.Decode(w)) {
            if (ins.Code is Opcodes.Func or Opcodes.FuncFacc) {
                ushort id = ins.Operand!.Value;
                calls[id] = calls.GetValueOrDefault(id) + 1;
                if (!callers.TryGetValue(id, out var set)) callers[id] = set = [];
                set.Add(path);
            }
        }
    }
}

// Two rounds: the first counts with the exe's guesses, the second with the first round's results.
for (int round = 0; round < 2; round++) {
    depthVotes.Clear();
    kindVotes.Clear();
    foreach ((string path, ScriptBlock s) in scripts) {
        for (int wi = 0; wi < s.Workers.Count; wi++) {
            Worker w = s.Workers[wi];
            HashSet<uint> entries = [.. w.Entries];
            Dictionary<uint, int[]> labelsAt = w.Labels.Select((o, i) => (o, i)).GroupBy(t => t.o).ToDictionary(g => g.Key, g => g.Select(t => t.i).ToArray());
            Dictionary<int, List<char>> labelStack = [];
            List<char>? st = []; // 'i', 'f', '?' per slot; null = unknown
            foreach (Instr ins in s.Decode(w)) {
                if (entries.Contains(ins.Offset)) st = [];
                if (st is null && labelsAt.TryGetValue(ins.Offset, out int[]? ls)) {
                    foreach (int l in ls) if (labelStack.TryGetValue(l, out var ld)) { st = [.. ld]; break; }
                }
                byte op = ins.Code;
                if (op is Opcodes.Func or Opcodes.FuncFacc) {
                    ushort id = ins.Operand!.Value;
                    if (st is not null && op == Opcodes.FuncFacc) {
                        if (!depthVotes.TryGetValue(id, out var v)) depthVotes[id] = v = [];
                        v[st.Count] = v.GetValueOrDefault(st.Count) + 1;
                    }
                    int argc = argcGuess.GetValueOrDefault(id, -1);
                    if (st is not null && argc >= 0 && argc <= st.Count) {
                        if (!kindVotes.TryGetValue(id, out var kv)) kindVotes[id] = kv = new int[Math.Max(argc, 1), 2];
                        if (kv.GetLength(0) >= argc) {
                            for (int k = 0; k < argc; k++) {
                                char c = st[st.Count - argc + k];
                                if (c == 'i') kv[k, 0]++;
                                else if (c == 'f') kv[k, 1]++;
                            }
                        }
                        st.RemoveRange(st.Count - argc, argc);
                        if (op == Opcodes.Func) st.Add(slots.TryGetValue(id, out var sl) && sl.Float is not null ? 'f' : 'i');
                    }
                    else {
                        st = null;
                    }
                    // A `func_facc` ends a statement: the stack is empty after it.
                    if (op == Opcodes.FuncFacc) st = [];
                    continue;
                }
                if (st is not null) {
                    (int pops, int pushes) = StackEffects.Of(op);
                    char result = '?';
                    if (pushes > 0) {
                        result = op switch {
                            Opcodes.PushFC or Opcodes.PushFacc or (>= Opcodes.PushF0 and <= 0x74) => 'f',
                            Opcodes.PushV or Opcodes.PushVA => s.Variable(wi, ins.Operand!.Value) is { } v && v.Location >> 28 == 6 ? 'f' : 'i',
                            >= Opcodes.Add and <= Opcodes.Mod or Opcodes.Neg => st.Count >= pops && st.Skip(st.Count - pops).Contains('f') ? 'f' : st.Count >= pops && st.Skip(st.Count - pops).Contains('?') ? '?' : 'i',
                            _ => 'i',
                        };
                    }
                    if (pops > st.Count) st = null;
                    else {
                        st.RemoveRange(st.Count - pops, pops);
                        for (int k = 0; k < pushes; k++) st.Add(result);
                    }
                }
                if (StackEffects.Jumps(op) && st is not null) labelStack[ins.Operand!.Value] = [.. st];
                if (!StackEffects.FallsThrough(op)) st = null;
            }
        }
    }
    // Decide the counts for the next round.
    foreach ((ushort id, var votes) in depthVotes) {
        (int top, int topVotes) = votes.Select(kv => (kv.Key, kv.Value)).MaxBy(t => t.Value);
        double share = (double)topVotes / votes.Values.Sum();
        if (share >= 0.9 || !votes.ContainsKey(argcGuess.GetValueOrDefault(id, -1))) argcGuess[id] = top;
    }
}

// ----- Names and descriptions ------------------------------------------------------------------------------
string[] groupClass = new string[16];
foreach ((int g, string name) in new[] { (0x0, "Common"), (0x1, "Calc"), (0x4, "Screen"), (0x5, "Chr"), (0x6, "Cam"), (0x7, "Btl"), (0x8, "Fx"), (0x9, "Group9"), (0xb, "Movie"), (0xc, "GroupC"), (0xd, "GroupD") }) groupClass[g] = name;

static string Pascal(string s) {
    StringBuilder sb = new();
    bool up = true;
    foreach (char c in s) {
        if (!char.IsAsciiLetterOrDigit(c)) { up = true; continue; }
        sb.Append(up ? char.ToUpperInvariant(c) : c);
        up = false;
    }
    return sb.ToString();
}
static string Camel(string s) {
    string p = Pascal(s);
    if (p.Length == 0) return p;
    string c = char.ToLowerInvariant(p[0]) + p[1..];
    return SyntaxFacts.IsKeyword(c) || char.IsAsciiDigit(c[0]) ? "@" + (char.IsAsciiDigit(c[0]) ? "a" + c : c) : c;
}

string MethodName(ushort id, string? symName, string cls) {
    if (symName is null) return $"N{id:x4}";
    string n = symName;
    foreach (string prefix in new[] { "Native_", "AtelCall_", "CT_" }) if (n.StartsWith(prefix, StringComparison.Ordinal)) n = n[prefix.Length..];
    string hex = $"{id:x4}";
    n = Regex.Replace(n, $"(^|_){hex}(_|$)", "_", RegexOptions.IgnoreCase).Trim('_');
    n = Pascal(n);
    if (n.StartsWith(cls, StringComparison.Ordinal) && n.Length > cls.Length && char.IsAsciiLetterUpper(n[cls.Length])) n = n[cls.Length..];
    if (n.Length == 0 || char.IsAsciiDigit(n[0])) n = $"N{hex}" + n;
    if (n == cls) n += $"_{hex}";
    return n;
}

/// <summary>"args (a, b, c; note) -> ..." in a symbol comment: the names, if the count matches.</summary>
static List<string>? ArgNames(string comment, int argc) {
    Match m = Regex.Match(comment, @"args \(([^()]*(?:\([^()]*\)[^()]*)*)\)");
    if (!m.Success) return argc == 0 ? [] : null;
    string inner = m.Groups[1].Value.Split(';')[0];
    List<string> names = [];
    int depth = 0, start = 0;
    for (int i = 0; i <= inner.Length; i++) {
        char c = i < inner.Length ? inner[i] : ',';
        if (c is '(' or '[') depth++;
        else if (c is ')' or ']') depth--;
        else if (c == ',' && depth == 0) {
            string part = Regex.Replace(inner[start..i], @"\(.*?\)|\[.*?\]", "").Trim();
            // "movie int" -> movie; "x (float)" -> x
            string[] words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string word = words.Length == 0 ? "" : words.Length >= 2 && words[^1] is "int" or "float" or "f32" or "i32" ? string.Join(' ', words[..^1]) : part;
            names.Add(word);
            start = i + 1;
        }
    }
    if (names.Count == 1 && names[0] == "" && argc == 0) return [];
    return names.Count == argc ? names : null;
}

// ----- Output ----------------------------------------------------------------------------------------------
JsonArray natives = [];
StringBuilder cs = new();
cs.Append("""
    // SPDX-License-Identifier: MIT
    // <auto-generated> by tools/gen-natives.cs from data/natives.json: do not edit. </auto-generated>

    #nullable enable
    #pragma warning disable CS1591

    namespace Ffx;

    """);
int agree = 0, disagree = 0, onlyExe = 0, onlyBytecode = 0, named = 0, kindsKnown = 0;
foreach (IGrouping<int, KeyValuePair<ushort, NativeSlots>> group in slots.OrderBy(k => k.Key).GroupBy(k => k.Key >> 12)) {
    string cls = groupClass[group.Key];
    HashSet<string> used = [];
    cs.Append(CultureInfo.InvariantCulture, $"\n/// <summary>Natives of group {group.Key:x} (<c>0x{group.Key:x}000</c>–).</summary>\npublic static partial class {cls}\n{{\n");
    foreach ((ushort id, NativeSlots s) in group) {
        (string Name, string Comment)? sym = s.Handler is uint hh && symbols.TryGetValue(hh, out var sy) ? sy : null;
        if (sym is null) {
            foreach (uint h in s.All) if (symbols.TryGetValue(h, out var sy2) && sy2.Name.StartsWith("Native", StringComparison.Ordinal)) { sym = sy2; break; }
        }
        bool isNamed = sym is not null && !sym.Value.Name.StartsWith("FUN_", StringComparison.Ordinal);
        if (isNamed) named++;

        int exeCount = new[] { s.Start, s.Float, s.Int }.Where(h => h is not null).Sum(h => { var p = NetPops(h!.Value, 0); return p.I + p.F; });
        Dictionary<int, int>? votes = depthVotes.GetValueOrDefault(id);
        int argc = argcGuess[id];
        string source;
        if (votes is null) { source = "exe"; onlyExe++; }
        else if (votes.MaxBy(v => v.Value).Key == exeCount) { source = "both"; agree++; }
        else if (argc == exeCount) { source = "exe"; disagree++; }
        else { source = "bytecode"; disagree++; onlyBytecode++; }
        argc = Math.Max(argc, 0);

        // Kinds: the handler's own pops (reversed: the first pop is the last argument), else the scripts' pushes.
        char[] kinds = Enumerable.Repeat('i', argc).ToArray();
        string kindSource = "default";
        string? direct = new[] { s.Start, s.Float, s.Int }.Where(h => h is not null).Select(h => DirectPops(h!.Value)).FirstOrDefault(d => d is { Length: > 0 });
        if (direct is not null && direct.Length == argc) {
            for (int k = 0; k < argc; k++) kinds[k] = direct[argc - 1 - k];
            kindSource = "exe";
        }
        else if (kindVotes.TryGetValue(id, out var kv) && kv.GetLength(0) >= argc && argc > 0) {
            for (int k = 0; k < argc; k++) kinds[k] = kv[k, 1] > kv[k, 0] ? 'f' : 'i';
            kindSource = "bytecode";
        }
        if (kindSource != "default" || argc == 0) kindsKnown++;

        string method = MethodName(id, isNamed ? sym!.Value.Name : null, cls);
        if (!used.Add(method)) {
            method = $"{method}_{id:x4}";
            used.Add(method);
        }
        string comment = sym?.Comment ?? "";
        List<string> argNames = ArgNames(comment, argc) is { } an && an.All(n => Camel(n).Length > 0) ? an : [.. Enumerable.Range(0, argc).Select(i => $"a{i}")];
        List<string> paramNames = [];
        foreach (string n in argNames) {
            string c = Camel(n);
            if (c.Length == 0 || paramNames.Contains(c)) c = $"a{paramNames.Count}";
            paramNames.Add(c);
        }
        string result = s.Float is not null ? "float" : s.Int is not null ? "int" : "none";

        JsonObject o = new() {
            ["id"] = $"0x{id:x4}",
            ["class"] = cls,
            ["method"] = method,
            ["symbol"] = sym?.Name,
            ["slots"] = new JsonObject {
                ["start"] = s.Start is uint a0 ? $"0x{a0:x8}" : null,
                ["poll"] = s.Poll is uint a1 ? $"0x{a1:x8}" : null,
                ["float"] = s.Float is uint a2 ? $"0x{a2:x8}" : null,
                ["int"] = s.Int is uint a3 ? $"0x{a3:x8}" : null,
            },
            ["args"] = new JsonArray([.. Enumerable.Range(0, argc).Select(k => (JsonNode)new JsonObject { ["name"] = paramNames[k], ["kind"] = kinds[k] == 'f' ? "float" : "int" })]),
            ["result"] = result,
            ["polls"] = s.Poll is not null,
            ["calls"] = calls.GetValueOrDefault(id),
            ["scripts"] = callers.GetValueOrDefault(id)?.Count ?? 0,
            ["argc_exe"] = exeCount,
            ["argc_statements"] = votes is null ? null : new JsonObject([.. votes.OrderByDescending(v => v.Value).Select(v => KeyValuePair.Create(v.Key.ToString(CultureInfo.InvariantCulture), (JsonNode?)v.Value))]),
            ["argc_source"] = source,
            ["kinds_source"] = kindSource,
            ["doc"] = comment,
        };
        natives.Add(o);

        string doc = comment.Length > 0 ? System.Security.SecurityElement.Escape(comment) : "Not yet described.";
        cs.Append(CultureInfo.InvariantCulture, $"    /// <summary>{doc}</summary>\n");
        cs.Append(CultureInfo.InvariantCulture, $"    /// <remarks>Native <c>0x{id:x4}</c>{(sym is null ? "" : $", <c>{sym.Value.Name}</c>")}; {calls.GetValueOrDefault(id)} calls in the shipped scripts.</remarks>\n");
        string ret = result == "float" ? "float" : "int";
        string ps = string.Join(", ", Enumerable.Range(0, argc).Select(k => $"{(kinds[k] == 'f' ? "float" : "int")} {paramNames[k]}"));
        cs.Append(CultureInfo.InvariantCulture, $"    [Atel.Native(0x{id:x4}{(s.Poll is not null ? ", Polls = true" : "")})]\n");
        cs.Append(CultureInfo.InvariantCulture, $"    public static {ret} {method}({ps}) => throw Atel.Runtime.NotRunnable();\n\n");
    }
    cs.Length -= 1;
    cs.Append("}\n");
}

JsonObject doc2 = new() {
    ["generator"] = "tools/gen-natives.cs",
    ["exe_sha256"] = exeHash,
    ["natives"] = natives,
};
Directory.CreateDirectory(Path.Combine(root, "data"));
File.WriteAllText(Path.Combine(root, "data", "natives.json"), doc2.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n");
File.WriteAllText(Path.Combine(root, "src", "Ffx.Atel.Game", "Natives.g.cs"), cs.ToString());

int called = natives.Count(n => n!["calls"]!.GetValue<long>() > 0);
Console.WriteLine($"{natives.Count} natives in the tables, {called} called by the shipped scripts, {named} named");
Console.WriteLine($"argument counts: exe and statements agree {agree}, disagree {disagree} (statements win {onlyBytecode}), exe only {onlyExe}");
Console.WriteLine($"argument kinds known for {kindsKnown}");
return 0;

static string ThisFile([CallerFilePath] string path = "") => path;

static class SyntaxFacts {
    private static readonly HashSet<string> Keywords = [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long",
        "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void",
        "volatile", "while",
    ];
    public static bool IsKeyword(string s) => Keywords.Contains(s);
}
