// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Reflection;
using System.Text;
using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler.Layout;
using Ffx.Atel.Decompiler.Naming;

namespace Ffx.Atel.Decompiler;

/// <summary>A script's code as structured statements (<see cref="ScriptDecompiler.Structured"/>).</summary>
public sealed record StructuredScript(SourceNames Names, IReadOnlyList<StructuredWorker> Workers);

/// <summary>
///     A worker's methods as structured statements: per method, the entries it starts and its body;
///     <see cref="Print"/> writes statements of this worker as the decompiler would (C#, indented by depth 0).
/// </summary>
public sealed record StructuredWorker(int Index, string Name, IReadOnlyList<StructuredMethod> Methods, Func<IReadOnlyList<Stmt>, string> Print);

/// <summary>A method of a worker: the entries it starts (the first is its own) and its statements.</summary>
public sealed record StructuredMethod(IReadOnlyList<int> Entries, IReadOnlyList<Stmt> Body);

/// <summary>A script as C# source.</summary>
public sealed record DecompiledScript(SourceNames Names, ScriptSource Parts, DecompileStats Stats) {
    /// <summary>The script as one file (Phase 2's shape).</summary>
    public string Source => Parts.SingleFile();

    /// <summary>The script as a folder: the script class, a file per worker, the data (<see cref="SourceLayout"/>).</summary>
    public List<SourceFile> Files(int splitLines = SourceLayout.SplitLines, int partLines = SourceLayout.PartLines) => SourceLayout.Files(Parts, splitLines, partLines);

    /// <summary>The dead <c>if (false)</c> blocks, by worker and offset; empty unless asked for (<see cref="ScriptDecompiler.Decompile"/>'s <c>deadBlocks</c>).</summary>
    public IReadOnlyList<DeadBlock> DeadBlocks { get; init; } = [];
}

/// <summary>What the decompiler made of a script: C# statements, what stayed assembly, and the pins.</summary>
public sealed record DecompileStats {
    /// <summary>Statements written as C#.</summary>
    public int Statements { get; init; }

    /// <summary>Instructions still inside <c>Asm</c>.</summary>
    public int AsmInstructions { get; init; }

    /// <summary>Whether the source pins the integer pool (<c>[Ints]</c>): the rule does not give it, or assembly needs its indices.</summary>
    public bool IntPoolPinned { get; init; }

    public bool FloatPoolPinned { get; init; }

    /// <summary>Whether the pool differs from the rule's order (a pin the rules cannot avoid).</summary>
    public bool IntRuleFails { get; init; }

    public bool FloatRuleFails { get; init; }

    /// <summary>Structured constructs by kind (<c>If</c>, <c>IfElse</c>, <c>While</c>, <c>Switch</c>, …).</summary>
    public IReadOnlyDictionary<string, int> Constructs { get; init; } = new Dictionary<string, int>();

    /// <summary>Dead blocks written with a name (<c>if (Cut.ZanarkandCrowdFacingReaction)</c>), by name.</summary>
    public IReadOnlyDictionary<string, int> Cuts { get; init; } = new Dictionary<string, int>();

    /// <summary>The signatures of those blocks (<see cref="DeadSignature"/>, the keys of <c>data/cut-names.toml</c>).</summary>
    public IReadOnlySet<string> CutSignatures { get; init; } = new HashSet<string>();

    /// <summary>The signatures of the workers' own inline methods (<see cref="InlineNames.Signature"/>, the keys of <c>data/inline-names.toml</c>).</summary>
    public IReadOnlySet<string> HelperSignatures { get; init; } = new HashSet<string>();

    /// <summary><c>goto</c> statements left (<c>goto lN;</c>, <c>if (…) goto lN;</c>).</summary>
    public int Gotos { get; init; }

    /// <summary>Methods whose labels are pinned with <c>[Labels(…)]</c>.</summary>
    public int LabelPins { get; init; }

    /// <summary>Constructs the structurer made but dropped, because their lowering or C# did not hold.</summary>
    public int Fallbacks { get; init; }

    /// <summary>Methods that hold several entries' code (<c>Entry(n);</c>).</summary>
    public int MergedMethods { get; init; }

    /// <summary>Workers written with gotos only, because the structured form did not give the original back.</summary>
    public int FlatWorkers { get; init; }

    /// <summary>
    ///     Names placed (Phase 5): constants and casts by domain (<c>story</c>, <c>Command</c>,
    ///     <c>Command-cast</c>), variables named after the save (<c>save-named</c>, <c>save-unnamed</c>).
    /// </summary>
    public IReadOnlyDictionary<string, int> Names { get; init; } = new Dictionary<string, int>();

    /// <summary>
    ///     Inline method calls by method (<c>inline Dialogue.Say</c>), the folds dropped by reason
    ///     (<c>inline-fallback-print</c>, <c>inline-fallback-verify</c>), and the lines of dialogue
    ///     (<c>MsgOpen</c> calls): all (<c>dialogue-lines</c>), inside a call (<c>dialogue-one-call</c>), in dead
    ///     <c>if (false)</c> code, which stays as it is (<c>dialogue-dead</c>).
    /// </summary>
    public IReadOnlyDictionary<string, int> Inline { get; init; } = new Dictionary<string, int>();
}

/// <summary>
///     Bytecode to Atel source, Phase 2 of the plan ("skeleton"): the script's declarations, and every
///     entry's code as <c>Asm("""…""")</c> in the syntax of <see cref="Bytecode.Script.Assembly"/>.
/// </summary>
/// <remarks>
///     The script block is taken apart into a <see cref="ScriptImage"/>, and every part of it becomes a
///     declaration the compiler reads back (docs/plan.md, "Phase 2: the source shape"):
///     <list type="bullet">
///         <item>the script class: <c>[Event]</c>/<c>[Monster]</c>/<c>[Battle]</c>, <c>[Workers]</c> in order,
///         <c>[Header]</c>, <c>[Pin]</c>, <c>[Patch]</c>, the constant pools (<c>[Ints]</c>, <c>[Floats]</c>) and the
///         variable table as <c>[Var]</c> fields <c>V0</c>, <c>V1</c>, …;</item>
///         <item>a class per worker, whose base class gives its type; a method per distinct entry offset,
///         in code order, with its <c>[Entry(n)]</c> numbers;</item>
///         <item>the data areas as hex (<see cref="HexText"/>) in a second part of the script class at the
///         end of the file.</item>
///     </list>
///     The text is written directly rather than through Roslyn's <c>SyntaxFactory</c>: the shape is
///     fixed, and raw strings come out as they are meant to be read.
/// </remarks>
public static class ScriptDecompiler {
    private const string Indent = "    ";

    /// <summary>Why constructs were dropped, over every script decompiled in this process (for tuning).</summary>
    public static IReadOnlyDictionary<string, int> DroppedReasons => WorkerDecompiler.Reasons;

    /// <summary>Decompiles a script file's block; <paramref name="archivePath"/> names it.</summary>
    /// <param name="archivePath">The file's path in the game's archive.</param>
    /// <param name="file">The script file.</param>
    /// <param name="inline">
    ///     The inline methods whose calls the source may use where they give the original code back
    ///     (docs/encoding.md, "Inline methods"): the reference assembly's when null, none when empty.
    /// </param>
    /// <param name="overlay">Hand-written names for workers, entries and variables (<see cref="NameOverlay"/>); by default this repo's <c>data/names/</c> file of the script.</param>
    /// <param name="deadBlocks">Whether to list the dead blocks with their code (<see cref="DecompiledScript.DeadBlocks"/>; <c>atel dead</c>).</param>
    /// <param name="cuts">Names for dead blocks (<see cref="CutNames"/>); by default this repo's <c>data/cut-names.toml</c>.</param>
    /// <param name="repeats">
    ///     How each worker's repeated code becomes inline methods of its own (<see cref="RepeatOptions"/>): the
    ///     defaults when null, none with <see cref="RepeatOptions.Disabled"/>.
    /// </param>
    /// <exception cref="ScriptFormatException">The block has a shape the source cannot hold.</exception>
    /// <exception cref="FormatException">The overlay names something the script does not have, or gives a name twice.</exception>
    public static DecompiledScript Decompile(string archivePath, ScriptFile file, IReadOnlyList<InlineMacro>? inline = null, NameOverlay? overlay = null, bool deadBlocks = false, CutNames? cuts = null, RepeatOptions? repeats = null) {
        overlay ??= NameOverlay.Load(null, archivePath);
        SourceNames names = SourceNames.ForPath(archivePath);
        ScriptBlock block = file.Script;
        ScriptImage image = ScriptImage.FromBlock(block);
        string shortPath = archivePath.StartsWith("ffx_ps2/ffx/master/jppc/", StringComparison.Ordinal) ? archivePath["ffx_ps2/ffx/master/jppc/".Length..] : archivePath;

        HandlerTable? handlers = HandlerTable.Of(file);
        (string[] varNames, string[] workerNames, WorkerDecompiler[] workers) = StructuredWorkers(names, block, image, inline, overlay, cuts, repeats ?? new RepeatOptions(), handlers);
        EntryNaming.Apply(workers, workerNames, overlay);
        HandlerNaming.AssignEntries(names.Category, handlers, workers, workerNames, WorkerNaming.ReservedWith(varNames));
        RequestNaming.Assign(workers, workerNames);

        // The pools follow the rules unless the original differs, or assembly names them by index.
        List<IReadOnlyList<CodeOp>> ops = [.. workers.Select(w => (IReadOnlyList<CodeOp>)w.Ops())];
        bool asmInts = workers.Any(w => w.AsmUsesPools().Ints), asmFloats = workers.Any(w => w.AsmUsesPools().Floats);
        bool intRule = ConstantPools.Ints(ops).SequenceEqual(image.IntConstants);
        bool floatRule = ConstantPools.Floats(ops, ConstantPools.Argc(Natives)).SequenceEqual(image.FloatConstants);
        DecompileStats stats = new() {
            Statements = workers.Sum(w => w.Statements),
            AsmInstructions = workers.Sum(w => w.AsmInstructions),
            IntPoolPinned = image.IntConstants.Count > 0 && (!intRule || asmInts),
            FloatPoolPinned = image.FloatConstants.Count > 0 && (!floatRule || asmFloats),
            IntRuleFails = !intRule,
            FloatRuleFails = !floatRule,
            Constructs = workers.SelectMany(w => w.Flow.Constructs).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value)),
            Cuts = workers.SelectMany(w => w.Flow.Cuts).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value)),
            CutSignatures = workers.SelectMany(w => w.Flow.CutSignatures).ToHashSet(StringComparer.Ordinal),
            HelperSignatures = workers.SelectMany(w => w.Flow.HelperSignatures).ToHashSet(StringComparer.Ordinal),
            Gotos = workers.Sum(w => w.Flow.Gotos),
            LabelPins = workers.Sum(w => w.Flow.LabelPins),
            Fallbacks = workers.Sum(w => w.Flow.Fallbacks),
            MergedMethods = workers.Sum(w => w.Flow.MergedMethods),
            FlatWorkers = workers.Sum(w => w.Flow.FlatWorkers),
            Inline = workers.SelectMany(w => w.InlineCounts).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value)),
        };

        StringBuilder main = new(), layout = new(), data = new();
        WriteScriptClass(main, layout, names, image, workerNames, varNames, stats.IntPoolPinned, stats.FloatPoolPinned);
        WorkerSource[] workerSources = [.. Enumerable.Range(0, image.Workers.Count).Select(w => WriteWorker(image, workers[w], w, workerNames[w], names.Category) with { Signature = WorkerSignature.Of(block, w) })];
        // Comments read off the code: who requests each entry, and the story index on the script class.
        workerSources = FlowNotes.Annotate(workers, workerNames, workerSources);
        WriteData(data, names, image);
        string layoutPart = LayoutComment + layout + $"static partial class {names.ClassName}\n{{\n}}\n";
        ScriptSource parts = new(names, shortPath, FlowNotes.StoryIndex(workers, workerNames) + main, workerSources, layoutPart, data.Length > 0 ? data.ToString() : null);
        Dictionary<string, int> placed = new(StringComparer.Ordinal);
        foreach ((string k, int n) in workers.SelectMany(w => w.Names)) placed[k] = placed.GetValueOrDefault(k) + n;
        placed["entry-override"] = workers.Sum(w => w.Overrides);
        placed["entry-async-task"] = workers.Sum(w => w.AsyncTaskMethods);
        placed["entry-async-void"] = workers.Sum(w => w.AsyncVoidMethods);
        placed["worker-named"] = workerNames.Count(n => !n.StartsWith('W') || !n[1..].All(char.IsAsciiDigit));
        foreach (VarEntry v in image.Variables.Where(v => v.Storage == 0)) {
            string k = GameNames.SaveFields.ContainsKey(v.Offset) ? "save-named" : "save-unnamed";
            placed[k] = placed.GetValueOrDefault(k) + 1;
        }
        return new DecompiledScript(names, parts, stats with { Names = placed }) {
            DeadBlocks = deadBlocks ? DeadTwins.Count(workers, [.. workers.SelectMany((w, i) => w.DeadBlocks(workerNames[i]))]) : [],
        };
    }

    private static VarInfo VarInfoOf(VarEntry v, string name) {
        CType type = v.Type switch {
            0 => CType.Byte, 1 => CType.SByte, 2 => CType.UShort, 3 => CType.Short, 5 => CType.Int, 6 => CType.Float,
            _ => CType.Int,
        };
        Domain? domain = v.Storage == 0 && v.Offset == GameNames.StoryOffset && !v.IsArray ? GameNames.Story : null;
        // A bit-flag save field's masks (Save.LightningReward), for a byte-wide variable at the field's offset.
        Domain? bits = v.Storage == 0 && type is CType.Byte && GameNames.SaveBits.TryGetValue(v.Offset, out Domain? b) ? b : null;
        return new VarInfo(name, type, v.IsArray, v.RowLength, domain, bits);
    }

    /// <summary>
    ///     The variables' C# names: the overlay's name for its storage and offset (<see cref="NameOverlay"/>);
    ///     else a save variable (storage 0) by the <see cref="Ffx.Save"/> field at its offset
    ///     (<c>StoryProgress</c>), else <c>Save_0123</c>; the others <c>V3</c> by table index. A name the
    ///     table already gave (the same location declared twice) gets <c>_2</c>, <c>_3</c>, ….
    /// </summary>
    /// <exception cref="FormatException">The overlay names a location the table does not declare.</exception>
    public static string[] VariableNames(IReadOnlyList<VarEntry> table, NameOverlay? overlay = null) {
        overlay ??= NameOverlay.Empty;
        foreach ((int storage, int offset) in overlay.Variables.Keys) {
            if (!table.Any(v => v.Storage == storage && v.Offset == offset)) {
                throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"{overlay.Source}: the script declares no variable at {(Storage)storage}:0x{offset:x4}"));
            }
        }
        string[] names = new string[table.Count];
        // Overlay names first, so that a generated name cannot take one.
        HashSet<string> taken = new(overlay.Variables.Values, StringComparer.Ordinal);
        if (taken.Count != overlay.Variables.Count) throw new FormatException($"{overlay.Source}: two variables have the same name");
        HashSet<(int, int)> placed = [];
        for (int i = 0; i < table.Count; i++) {
            VarEntry v = table[i];
            if (overlay.Variables.TryGetValue((v.Storage, v.Offset), out string? given) && placed.Add((v.Storage, v.Offset))) {
                names[i] = given;
                continue;
            }
            string name = overlay.Variables.TryGetValue((v.Storage, v.Offset), out given) ? given
                : v.Storage != 0 ? string.Create(CultureInfo.InvariantCulture, $"V{i}")
                : GameNames.SaveFields.TryGetValue(v.Offset, out string? field) ? field
                : string.Create(CultureInfo.InvariantCulture, $"Save_{v.Offset:x4}");
            string unique = name;
            for (int k = 2; !taken.Add(unique); k++) unique = string.Create(CultureInfo.InvariantCulture, $"{name}_{k}");
            names[i] = unique;
        }
        return names;
    }

    /// <summary><c>W00</c>, <c>W01</c>, …: as many digits as the highest number needs, at least two.</summary>
    public static string[] WorkerNames(int count) {
        int digits = Math.Max(2, (count - 1).ToString(CultureInfo.InvariantCulture).Length);
        return Enumerable.Range(0, count).Select(w => "W" + w.ToString("D" + digits, CultureInfo.InvariantCulture)).ToArray();
    }

    // ----- The script class ------------------------------------------------------------------

    /// <summary>
    ///     The script class's main part: its attribute, <c>[Workers]</c> and the variables. The attributes that
    ///     only pin the layout (<c>[Header]</c>, <c>[Pin]</c>, <c>[Patch]</c>, the pools) go to <paramref name="layout"/>,
    ///     which becomes the layout part (<c>Bjyt0400.Layout.cs</c>), so the main file reads as the script's outline.
    /// </summary>
    private static void WriteScriptClass(StringBuilder main, StringBuilder sb, SourceNames names, ScriptImage image, string[] workerNames, string[] varNames, bool pinInts, bool pinFloats) {
        main.Append(CultureInfo.InvariantCulture, $"[{names.Attribute}({Str(names.Name)})]\n");
        if (workerNames.Length > 0) {
            WriteList(main, "Workers", workerNames.Select(n => $"typeof({n})").ToList(), 6);
        }
        List<string> header = [$"Author = {Str(image.AuthorText)}", $"Name = {Str(image.NameText)}"];
        foreach ((string name, uint v) in new[] { ("Word14", image.Header14), ("Word18", image.Header18), ("Word1C", image.Header1C), ("Word24", image.Header24) }) {
            if (v != 0) header.Add($"{name} = {Hex(v, 8)}");
        }
        sb.Append(CultureInfo.InvariantCulture, $"[Header({string.Join(", ", header)})]\n");
        foreach ((string region, int offset) in image.Pins) sb.Append(CultureInfo.InvariantCulture, $"[Pin({Str(region)}, {Hex((uint)offset, 4)})]\n");
        foreach ((int offset, byte[] bytes) in image.Patches) {
            sb.Append(CultureInfo.InvariantCulture, $"[Patch({Hex((uint)offset, 4)}, {Str(Convert.ToHexStringLower(bytes))})]\n");
        }
        if (pinInts) {
            WriteList(sb, "Ints", image.IntConstants.Select(v => ((int)v).ToString(CultureInfo.InvariantCulture)).ToList(), 10);
        }
        if (pinFloats) {
            List<string?> literals = image.FloatConstants.Select(FloatLiteral).ToList();
            if (literals.All(l => l is not null)) WriteList(sb, "Floats", literals.Select(l => l!).ToList(), 8);
            else WriteList(sb, "FloatBits", image.FloatConstants.Select(v => Hex(v, 8)).ToList(), 8);
        }
        main.Append(CultureInfo.InvariantCulture, $"public static partial class {names.ClassName}\n{{\n");
        for (int i = 0; i < image.Variables.Count; i++) {
            main.Append(Indent).Append(VarDeclaration(image.Variables[i], i, varNames[i])).Append('\n');
        }
        main.Append("}\n");
    }

    /// <summary><c>[Var(Storage.Private, 0x0012)] public static byte[] V3 = new byte[4];</c></summary>
    private static string VarDeclaration(VarEntry v, int index, string name) {
        string type = v.Type switch {
            0 => "byte", 1 => "sbyte", 2 => "ushort", 3 => "short", 4 => "uint", 5 => "int", 6 => "float",
            _ => throw new ScriptFormatException($"variable {index} has type {v.Type}, which has no C# type"),
        };
        string storage = ((Storage)v.Storage).ToString();
        // A save field is placed by its constant: [Var(Storage.Save, Save.StoryProgress)].
        string offset = v.Storage == 0 && GameNames.SaveFields.TryGetValue(v.Offset, out string? field) ? $"Save.{field}" : Hex((uint)v.Offset, 4);
        string head = $"[Var(Storage.{storage}, {offset})] public static";
        // Location bit 24 is a variable declared as an array, also of length 1; the row length
        // makes it 2-D (it divides the length in every shipped script).
        if (!v.IsArray) {
            if (v.Length != 1 || v.RowLength != 0) throw new ScriptFormatException($"variable {index} is not an array but has length {v.Length}, row length {v.RowLength}");
            return string.Create(CultureInfo.InvariantCulture, $"{head} {type} {name};");
        }
        if (v.RowLength == 0) return string.Create(CultureInfo.InvariantCulture, $"{head} {type}[] {name} = new {type}[{v.Length}];");
        if (v.Length % v.RowLength != 0) throw new ScriptFormatException($"variable {index}: row length {v.RowLength} does not divide length {v.Length}");
        return string.Create(CultureInfo.InvariantCulture, $"{head} {type}[,] {name} = new {type}[{v.Length / v.RowLength}, {v.RowLength}];");
    }

    // ----- Workers ---------------------------------------------------------------------------

    /// <summary>
    ///     The base class of a worker of <paramref name="type"/>: the reference assembly's name for the type
    ///     (<c>Actor</c> for 1), <c>BattleWorker</c> for type 2 outside events (every monster and battle
    ///     worker), else <c>Worker7</c>, which does not compile.
    /// </summary>
    public static string TypeClass(int type, ScriptCategory category) => type switch {
        0 => "FunctionWorker",
        1 => "Actor",
        2 => category == ScriptCategory.Event ? "LineTrigger" : "BattleWorker",
        3 => "BoxTrigger",
        4 => "LogicWorker",
        5 => "LineSensor",
        6 => "BoxSensor",
        _ => string.Create(CultureInfo.InvariantCulture, $"Worker{type}"),
    };

    private static WorkerSource WriteWorker(ScriptImage image, WorkerDecompiler worker, int w, string name, ScriptCategory category) {
        WorkerImage wi = image.Workers[w];
        StringBuilder sb = new();
        List<string> words = [];
        if (wi.Unknown0C != 0) words.Add($"Word0C = {Hex(wi.Unknown0C, 8)}");
        if (wi.PrivateLength != 0) words.Add($"PrivateLength = {Hex(wi.PrivateLength, 2)}");
        if (words.Count > 0) sb.Append(CultureInfo.InvariantCulture, $"[Worker({string.Join(", ", words)})]\n");
        string pins = sb.ToString();
        sb.Clear();
        if (wi.Private is not null) WriteDataAttribute(sb, "PrivateData", wi.Private);
        string privateData = sb.ToString();
        sb.Clear();
        SortedSet<(RegKind Kind, int Number)> registers = worker.Registers();
        foreach ((RegKind kind, int n) in registers) {
            sb.Append(Indent).Append(CultureInfo.InvariantCulture, $"[Register({n})] public static {(kind == RegKind.Int ? "int" : "float")} {(kind == RegKind.Int ? "I" : "F")}{n};\n");
        }
        if (registers.Count > 0) sb.Append('\n');
        worker.WriteMethods(sb, id => NativeNames.Value.TryGetValue(id, out string? native) ? native : null);
        return new WorkerSource(name, w, "", TypeClass(wi.Type, category), sb.ToString()) { Pins = pins, PrivateData = privateData };
    }

    /// <summary>The script's workers, structured and with the calls of <paramref name="inline"/> folded: the first half of <see cref="Decompile"/>.</summary>
    private static (string[] VarNames, string[] WorkerNames, WorkerDecompiler[] Workers) StructuredWorkers(SourceNames names, ScriptBlock block, ScriptImage image, IReadOnlyList<InlineMacro>? inline, NameOverlay overlay, CutNames? cuts, RepeatOptions repeats, HandlerTable? handlers) {
        string[] varNames = VariableNames(image.Variables, overlay);
        string[] defaults = WorkerNames(image.Workers.Count);
        string[] taken = [.. varNames, names.ClassName, .. names.Namespace.Split('.'), .. MemberNames];
        string[] workerNames = WorkerNaming.Names(block, image, defaults, taken, overlay);
        // The battle's handlers name the workers it runs (Ai, Cameras), before the roles read off their code.
        HandlerNaming.AssignWorkers(names.Category, handlers, workerNames, defaults, WorkerNaming.ReservedWith(taken));
        VarInfo[] vars = [.. image.Variables.Select((v, i) => VarInfoOf(v, varNames[i]))];
        WorkerDecompiler[] workers = [.. Enumerable.Range(0, image.Workers.Count).Select(w => new WorkerDecompiler(block, w, vars, image.Variables, Natives, workerNames, cuts) { Script = names.ClassName })];
        foreach (WorkerDecompiler w in workers) w.Structure();
        // Role names read the structured code; nothing has printed a name yet.
        WorkerNaming.AssignRoles(names.Category, workers, workerNames, defaults, taken);
        inline ??= Compiler.InlineMethods.Game.All;
        foreach (WorkerDecompiler w in workers) {
            w.FoldInline(inline);
            w.FoldRepeats(repeats);
        }
        return (varNames, workerNames, workers);
    }

    /// <summary>
    ///     A script's code as structured statements, without writing it (<c>atel repeats</c>): per worker, its
    ///     methods' bodies as <see cref="Decompile"/> writes them, after the calls of <paramref name="inline"/>
    ///     are folded (the reference assembly's when null, none when empty).
    /// </summary>
    /// <param name="archivePath">The file's path in the game's archive.</param>
    /// <param name="file">The script file.</param>
    /// <param name="inline">The inline methods to fold.</param>
    /// <param name="repeats">Each worker's repeated code as inline methods of its own (<see cref="RepeatOptions"/>); none when null.</param>
    public static StructuredScript Structured(string archivePath, ScriptFile file, IReadOnlyList<InlineMacro>? inline = null, RepeatOptions? repeats = null) {
        SourceNames names = SourceNames.ForPath(archivePath);
        ScriptBlock block = file.Script;
        (_, string[] workerNames, WorkerDecompiler[] workers) = StructuredWorkers(names, block, ScriptImage.FromBlock(block), inline, NameOverlay.Load(null, archivePath), null, repeats ?? RepeatOptions.Disabled, HandlerTable.Of(file));
        return new StructuredScript(names, [.. workers.Select((w, i) => new StructuredWorker(i, workerNames[i], w.Bodies, w.Print))]);
    }

    /// <summary>The C# name of each native (<c>Btl.SetStat</c>), from the reference assembly.</summary>
    private static readonly Lazy<Dictionary<ushort, string>> NativeNames = new(() => Natives.All.ToDictionary(n => n.Id, n => n.Name));

    /// <summary>The natives of the reference assembly (<c>Ffx.Atel.Game</c>).</summary>
    public static NativeSignatures Natives => s_natives.Value;

    /// <summary>The names of the object-oriented surface a worker class must not take (it would hide them, or be hidden).</summary>
    private static IEnumerable<string> MemberNames => [.. Compiler.NativeMembers.Default.WorkerRoots, .. Compiler.NativeMembers.Default.StaticRoots, "Workers"];

    private static readonly Lazy<NativeSignatures> s_natives = new(() => NativeSignatures.FromAssembly(typeof(NativeAttribute).Assembly));

    // ----- Data ------------------------------------------------------------------------------

    /// <summary>The comment at the top of the layout part.</summary>
    private const string LayoutComment =
        "// What keeps the compiled script byte for byte the game's: the header, the layout and the\n// constant pools. Nothing here needs editing.\n";

    /// <summary>The data part of the script class: the data areas as hex; nothing when the script has none.</summary>
    private static void WriteData(StringBuilder sb, SourceNames names, ScriptImage image) {
        (string Name, byte[]? Data)[] areas = [
            ("MapData", image.Map), ("SharedData", image.Shared), ("OtherData", image.Other),
            ("EventData", image.Event), ("AreaData", image.Area),
        ];
        if (areas.All(a => a.Data is null)) return;
        sb.Append(ScriptSource.DataComment);
        foreach ((string name, byte[]? data) in areas) {
            if (data is not null) WriteDataAttribute(sb, name, data);
        }
        sb.Append(CultureInfo.InvariantCulture, $"static partial class {names.ClassName}\n{{\n}}\n");
    }

    private static void WriteDataAttribute(StringBuilder sb, string name, byte[] data) {
        string hex = HexText.Format(data, Indent);
        if (hex.Length == 0) {
            sb.Append(CultureInfo.InvariantCulture, $"[{name}({Hex((uint)data.Length, 2)}, \"\")]\n");
            return;
        }
        sb.Append(CultureInfo.InvariantCulture, $"[{name}({Hex((uint)data.Length, 2)}, \"\"\"\n").Append(hex).Append(Indent).Append("\"\"\")]\n");
    }

    // ----- Literals --------------------------------------------------------------------------

    private static void WriteList(StringBuilder sb, string attribute, List<string> items, int perLine) {
        if (items.Count <= perLine) {
            sb.Append(CultureInfo.InvariantCulture, $"[{attribute}({string.Join(", ", items)})]\n");
            return;
        }
        sb.Append(CultureInfo.InvariantCulture, $"[{attribute}(\n");
        for (int i = 0; i < items.Count; i += perLine) {
            sb.Append(Indent).Append(string.Join(", ", items.Skip(i).Take(perLine)));
            sb.Append(i + perLine < items.Count ? ",\n" : "\n");
        }
        sb.Append(")]\n");
    }

    private static string Hex(uint v, int digits) => "0x" + v.ToString("x" + digits, CultureInfo.InvariantCulture);

    /// <summary>A C# float literal that compiles to exactly these bits, or null (NaN, infinities, -0).</summary>
    public static string? FloatLiteral(uint bits) {
        float f = BitConverter.UInt32BitsToSingle(bits);
        if (!float.IsFinite(f) || bits == 0x8000_0000) return null;
        string s = f.ToString("R", CultureInfo.InvariantCulture);
        return BitConverter.SingleToUInt32Bits(float.Parse(s, CultureInfo.InvariantCulture)) == bits ? s + "f" : null;
    }

    /// <summary>A C# string literal; characters outside printable ASCII as <c>\u00XX</c> (the strings are Latin-1).</summary>
    public static string Str(string s) {
        StringBuilder sb = new("\"");
        foreach (char c in s) {
            if (c is '"' or '\\') sb.Append('\\').Append(c);
            else if (c is >= ' ' and <= '~') sb.Append(c);
            else sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
        }
        return sb.Append('"').ToString();
    }
}

/// <summary>Totals of <see cref="DecompileStats"/> over many scripts; safe to add to from several threads.</summary>
public sealed class DecompileTotals {
    private long _scripts, _statements, _asm, _intPins, _floatPins, _intRule, _floatRule, _gotos, _labelPins, _fallbacks, _merged, _flat;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _constructs = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _names = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _inline = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _cuts = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _cutSignatures = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _helperSignatures = new(StringComparer.Ordinal);

    public long Scripts => _scripts;
    public long Statements => _statements;
    public long AsmInstructions => _asm;

    public void Add(DecompileStats s) {
        Interlocked.Increment(ref _scripts);
        Interlocked.Add(ref _statements, s.Statements);
        Interlocked.Add(ref _asm, s.AsmInstructions);
        if (s.IntPoolPinned) Interlocked.Increment(ref _intPins);
        if (s.FloatPoolPinned) Interlocked.Increment(ref _floatPins);
        if (s.IntRuleFails) Interlocked.Increment(ref _intRule);
        if (s.FloatRuleFails) Interlocked.Increment(ref _floatRule);
        Interlocked.Add(ref _gotos, s.Gotos);
        Interlocked.Add(ref _labelPins, s.LabelPins);
        Interlocked.Add(ref _fallbacks, s.Fallbacks);
        Interlocked.Add(ref _merged, s.MergedMethods);
        Interlocked.Add(ref _flat, s.FlatWorkers);
        foreach ((string k, int n) in s.Constructs) _constructs.AddOrUpdate(k, n, (_, v) => v + n);
        foreach ((string k, int n) in s.Names) _names.AddOrUpdate(k, n, (_, v) => v + n);
        foreach ((string k, int n) in s.Inline) _inline.AddOrUpdate(k, n, (_, v) => v + n);
        foreach ((string k, int n) in s.Cuts) _cuts.AddOrUpdate(k, n, (_, v) => v + n);
        foreach (string sig in s.CutSignatures) _cutSignatures.TryAdd(sig, 0);
        foreach (string sig in s.HelperSignatures) _helperSignatures.TryAdd(sig, 0);
    }

    /// <summary>Named dead blocks over all scripts, by name (<see cref="DecompileStats.Cuts"/>).</summary>
    public IReadOnlyDictionary<string, long> Cuts => _cuts;

    /// <summary>The signatures of the named dead blocks over all scripts.</summary>
    public IReadOnlyCollection<string> CutSignatures => (IReadOnlyCollection<string>)_cutSignatures.Keys;

    /// <summary>The signatures of the workers' own inline methods over all scripts.</summary>
    public IReadOnlyCollection<string> HelperSignatures => (IReadOnlyCollection<string>)_helperSignatures.Keys;

    /// <summary>Inline calls and dialogue lines over all scripts (<see cref="DecompileStats.Inline"/>).</summary>
    public IReadOnlyDictionary<string, long> Inline => _inline;

    /// <summary>The inline numbers: calls by method, dialogue lines as one call of all, fallbacks by reason.</summary>
    public string InlineSummary() => "inline: " + string.Join(", ", _inline.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} {kv.Value}")));

    public long Gotos => _gotos;

    /// <summary>Names placed over all scripts, by kind (<see cref="DecompileStats.Names"/>).</summary>
    public IReadOnlyDictionary<string, long> Names => _names;

    /// <summary>The naming numbers: names placed by kind.</summary>
    public string NameSummary() => "names: " + string.Join(", ", _names.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} {kv.Value}")))
        + string.Create(CultureInfo.InvariantCulture, $", cut-named {_cuts.Values.Sum()} ({_cuts.Count} names)");
    public long LabelPins => _labelPins;
    public long FlatWorkers => _flat;

    /// <summary>The control-flow numbers: constructs by kind, gotos left, label pins, fallbacks.</summary>
    public string FlowSummary() => string.Create(CultureInfo.InvariantCulture,
        $"constructs: {string.Join(", ", _constructs.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}; {_gotos} gotos left; {_labelPins} methods with [Labels] pins; {_fallbacks} constructs dropped; {_merged} methods holding several entries; {_flat} workers flat")
        ;

    /// <summary>One line: statements, instructions left in <c>Asm</c>, pool pins (of them, where the rule fails).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{_statements} C# statements, {_asm} instructions in Asm; pools pinned: {_intPins} integer ({_intRule} where the rule fails), {_floatPins} float ({_floatRule} where the rule fails)");
}
