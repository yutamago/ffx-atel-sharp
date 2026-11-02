// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Reflection;
using System.Text;
using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler;

/// <summary>A script as C# source.</summary>
public sealed record DecompiledScript(SourceNames Names, string Source, DecompileStats Stats);

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

    /// <summary>Decompiles a script file's block; <paramref name="archivePath"/> names it.</summary>
    /// <exception cref="ScriptFormatException">The block has a shape the source cannot hold.</exception>
    public static DecompiledScript Decompile(string archivePath, ScriptFile file) {
        SourceNames names = SourceNames.ForPath(archivePath);
        ScriptBlock block = file.Script;
        ScriptImage image = ScriptImage.FromBlock(block);
        StringBuilder sb = new();
        string shortPath = archivePath.StartsWith("ffx_ps2/ffx/master/jppc/", StringComparison.Ordinal) ? archivePath["ffx_ps2/ffx/master/jppc/".Length..] : archivePath;
        sb.Append(CultureInfo.InvariantCulture, $"// {shortPath}, decompiled by atel.\n");
        sb.Append("using Ffx;\nusing Ffx.Atel;\n");
        sb.Append(CultureInfo.InvariantCulture, $"using static {names.Namespace}.{names.ClassName};\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"namespace {names.Namespace};\n\n");

        string[] workerNames = WorkerNames(image.Workers.Count);
        VarInfo[] vars = [.. image.Variables.Select((v, i) => VarInfoOf(v, i))];
        WorkerDecompiler[] workers = [.. Enumerable.Range(0, image.Workers.Count).Select(w => new WorkerDecompiler(block, w, vars, image.Variables, Natives, workerNames))];
        bool duplicates = image.IntConstants.Distinct().Count() != image.IntConstants.Count || image.FloatConstants.Distinct().Count() != image.FloatConstants.Count;
        foreach (WorkerDecompiler w in workers) {
            if (duplicates) w.KeepDuplicateIndices(image);
            w.PlaceLabels();
        }

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
        };

        WriteScriptClass(sb, names, image, workerNames, stats.IntPoolPinned, stats.FloatPoolPinned);
        for (int w = 0; w < image.Workers.Count; w++) {
            sb.Append('\n');
            WriteWorker(sb, image, workers[w], w, workerNames[w]);
        }
        WriteData(sb, names, image);
        return new DecompiledScript(names, sb.ToString(), stats);
    }

    private static VarInfo VarInfoOf(VarEntry v, int index) {
        CType type = v.Type switch {
            0 => CType.Byte, 1 => CType.SByte, 2 => CType.UShort, 3 => CType.Short, 5 => CType.Int, 6 => CType.Float,
            _ => CType.Int,
        };
        return new VarInfo(string.Create(CultureInfo.InvariantCulture, $"V{index}"), type, v.IsArray, v.RowLength);
    }

    /// <summary><c>W00</c>, <c>W01</c>, …: as many digits as the highest number needs, at least two.</summary>
    public static string[] WorkerNames(int count) {
        int digits = Math.Max(2, (count - 1).ToString(CultureInfo.InvariantCulture).Length);
        return Enumerable.Range(0, count).Select(w => "W" + w.ToString("D" + digits, CultureInfo.InvariantCulture)).ToArray();
    }

    // ----- The script class ------------------------------------------------------------------

    private static void WriteScriptClass(StringBuilder sb, SourceNames names, ScriptImage image, string[] workerNames, bool pinInts, bool pinFloats) {
        sb.Append(CultureInfo.InvariantCulture, $"[{names.Attribute}({Str(names.Name)})]\n");
        if (workerNames.Length > 0) {
            WriteList(sb, "Workers", workerNames.Select(n => $"typeof({n})").ToList(), 6);
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
        sb.Append(CultureInfo.InvariantCulture, $"public static partial class {names.ClassName}\n{{\n");
        for (int i = 0; i < image.Variables.Count; i++) {
            sb.Append(Indent).Append(VarDeclaration(image.Variables[i], i)).Append('\n');
        }
        sb.Append("}\n");
    }

    /// <summary><c>[Var(Storage.Private, 0x0012)] public static byte[] V3 = new byte[4];</c></summary>
    private static string VarDeclaration(VarEntry v, int index) {
        string type = v.Type switch {
            0 => "byte", 1 => "sbyte", 2 => "ushort", 3 => "short", 4 => "uint", 5 => "int", 6 => "float",
            _ => throw new ScriptFormatException($"variable {index} has type {v.Type}, which has no C# type"),
        };
        string storage = ((Storage)v.Storage).ToString();
        string head = $"[Var(Storage.{storage}, {Hex((uint)v.Offset, 4)})] public static";
        // Location bit 24 is a variable declared as an array, also of length 1; the row length
        // makes it 2-D (it divides the length in every shipped script).
        if (!v.IsArray) {
            if (v.Length != 1 || v.RowLength != 0) throw new ScriptFormatException($"variable {index} is not an array but has length {v.Length}, row length {v.RowLength}");
            return string.Create(CultureInfo.InvariantCulture, $"{head} {type} V{index};");
        }
        if (v.RowLength == 0) return string.Create(CultureInfo.InvariantCulture, $"{head} {type}[] V{index} = new {type}[{v.Length}];");
        if (v.Length % v.RowLength != 0) throw new ScriptFormatException($"variable {index}: row length {v.RowLength} does not divide length {v.Length}");
        return string.Create(CultureInfo.InvariantCulture, $"{head} {type}[,] V{index} = new {type}[{v.Length / v.RowLength}, {v.RowLength}];");
    }

    // ----- Workers ---------------------------------------------------------------------------

    private static void WriteWorker(StringBuilder sb, ScriptImage image, WorkerDecompiler worker, int w, string name) {
        WorkerImage wi = image.Workers[w];
        List<string> words = [];
        if (wi.Unknown0C != 0) words.Add($"Word0C = {Hex(wi.Unknown0C, 8)}");
        if (wi.PrivateLength != 0) words.Add($"PrivateLength = {Hex(wi.PrivateLength, 2)}");
        if (words.Count > 0) sb.Append(CultureInfo.InvariantCulture, $"[Worker({string.Join(", ", words)})]\n");
        if (wi.Private is not null) WriteDataAttribute(sb, "PrivateData", wi.Private);
        sb.Append(CultureInfo.InvariantCulture, $"public sealed class {name} : Worker{wi.Type}\n{{\n");
        SortedSet<(RegKind Kind, int Number)> registers = worker.Registers();
        foreach ((RegKind kind, int n) in registers) {
            sb.Append(Indent).Append(CultureInfo.InvariantCulture, $"[Register({n})] public {(kind == RegKind.Int ? "int" : "float")} {(kind == RegKind.Int ? "I" : "F")}{n};\n");
        }
        if (registers.Count > 0) sb.Append('\n');
        worker.WriteMethods(sb, id => NativeNames.Value.TryGetValue(id, out string? native) ? native : null);
        sb.Append("}\n");
    }

    /// <summary>The C# name of each native (<c>Btl.SetStat</c>), from the reference assembly.</summary>
    private static readonly Lazy<Dictionary<ushort, string>> NativeNames = new(() => Natives.All.ToDictionary(n => n.Id, n => n.Name));

    /// <summary>The natives of the reference assembly (<c>Ffx.Atel.Game</c>).</summary>
    public static NativeSignatures Natives => s_natives.Value;

    private static readonly Lazy<NativeSignatures> s_natives = new(() => NativeSignatures.FromAssembly(typeof(NativeAttribute).Assembly));

    // ----- Data ------------------------------------------------------------------------------

    private static void WriteData(StringBuilder sb, SourceNames names, ScriptImage image) {
        (string Name, byte[]? Data)[] areas = [
            ("MapData", image.Map), ("SharedData", image.Shared), ("OtherData", image.Other),
            ("EventData", image.Event), ("AreaData", image.Area),
        ];
        if (areas.All(a => a.Data is null)) return;
        sb.Append('\n');
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
    private long _scripts, _statements, _asm, _intPins, _floatPins, _intRule, _floatRule;

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
    }

    /// <summary>One line: statements, instructions left in <c>Asm</c>, pool pins (of them, where the rule fails).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{_statements} C# statements, {_asm} instructions in Asm; pools pinned: {_intPins} integer ({_intRule} where the rule fails), {_floatPins} float ({_floatRule} where the rule fails)");
}
