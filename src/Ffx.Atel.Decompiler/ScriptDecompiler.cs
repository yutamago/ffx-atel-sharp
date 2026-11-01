// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Reflection;
using System.Text;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler;

/// <summary>A script as C# source.</summary>
public sealed record DecompiledScript(SourceNames Names, string Source);

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
        sb.Append("using Ffx;\nusing Ffx.Atel;\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"namespace {names.Namespace};\n\n");

        string[] workerNames = WorkerNames(image.Workers.Count);
        WriteScriptClass(sb, names, image, workerNames);
        for (int w = 0; w < image.Workers.Count; w++) {
            sb.Append('\n');
            WriteWorker(sb, block, image, w, workerNames[w]);
        }
        WriteData(sb, names, image);
        return new DecompiledScript(names, sb.ToString());
    }

    /// <summary><c>W00</c>, <c>W01</c>, …: as many digits as the highest number needs, at least two.</summary>
    public static string[] WorkerNames(int count) {
        int digits = Math.Max(2, (count - 1).ToString(CultureInfo.InvariantCulture).Length);
        return Enumerable.Range(0, count).Select(w => "W" + w.ToString("D" + digits, CultureInfo.InvariantCulture)).ToArray();
    }

    // ----- The script class ------------------------------------------------------------------

    private static void WriteScriptClass(StringBuilder sb, SourceNames names, ScriptImage image, string[] workerNames) {
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
        if (image.IntConstants.Count > 0) {
            WriteList(sb, "Ints", image.IntConstants.Select(v => ((int)v).ToString(CultureInfo.InvariantCulture)).ToList(), 10);
        }
        if (image.FloatConstants.Count > 0) {
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
        StringBuilder a = new($"[Var(Storage.{storage}, {Hex((uint)v.Offset, 4)}");
        if (v.Extra != 0) a.Append(CultureInfo.InvariantCulture, $", Extra = {Hex(v.Extra, 4)}");
        if ((v.Location & 0x0100_0000) != 0) a.Append(", Bit24 = true");
        // An array's length is its size; a variable of length 1 is a scalar.
        a.Append(v.Length == 1
            ? $")] public static {type} V{index};"
            : string.Create(CultureInfo.InvariantCulture, $")] public static {type}[] V{index} = new {type}[{v.Length}];"));
        return a.ToString();
    }

    // ----- Workers ---------------------------------------------------------------------------

    private static void WriteWorker(StringBuilder sb, ScriptBlock block, ScriptImage image, int w, string name) {
        WorkerImage wi = image.Workers[w];
        Bytecode.Script.Worker worker = block.Workers[w];
        List<string> words = [];
        if (wi.Unknown0C != 0) words.Add($"Word0C = {Hex(wi.Unknown0C, 8)}");
        if (wi.PrivateLength != 0) words.Add($"PrivateLength = {Hex(wi.PrivateLength, 2)}");
        if (words.Count > 0) sb.Append(CultureInfo.InvariantCulture, $"[Worker({string.Join(", ", words)})]\n");
        if (wi.Private is not null) WriteDataAttribute(sb, "PrivateData", wi.Private);
        sb.Append(CultureInfo.InvariantCulture, $"sealed class {name} : Worker{wi.Type}\n{{\n");

        // One method per distinct entry offset, in code order; the method runs to the next one.
        List<uint> starts = worker.Entries.Distinct().Order().ToList();
        Dictionary<uint, List<int>> entriesAt = [];
        for (int i = 0; i < worker.Entries.Length; i++) {
            if (!entriesAt.TryGetValue(worker.Entries[i], out List<int>? list)) entriesAt[worker.Entries[i]] = list = [];
            list.Add(i);
        }
        Dictionary<uint, List<int>> labelsAt = [];
        for (int i = 0; i < worker.Labels.Length; i++) {
            if (!labelsAt.TryGetValue(worker.Labels[i], out List<int>? list)) labelsAt[worker.Labels[i]] = list = [];
            list.Add(i);
        }
        List<Instr> code = block.Decode(worker);
        int k = 0;
        for (int m = 0; m < starts.Count; m++) {
            uint end = m + 1 < starts.Count ? starts[m + 1] : worker.End;
            List<int> numbers = entriesAt[starts[m]];
            if (m > 0) sb.Append('\n');
            sb.Append(Indent).Append(string.Join("", numbers.Select(n => $"[Entry({n})]"))).Append('\n');
            sb.Append(Indent).Append(CultureInfo.InvariantCulture, $"void E{numbers[0]}()\n");
            sb.Append(Indent).Append("{\n");
            const string Body = Indent + Indent;
            sb.Append(Body).Append("Asm(\"\"\"\n");
            for (; k < code.Count && code[k].Offset < end; k++) {
                Instr instr = code[k];
                if (labelsAt.TryGetValue(instr.Offset, out List<int>? labels)) {
                    foreach (int l in labels) sb.Append(Body).Append(CultureInfo.InvariantCulture, $"l{l}:\n");
                }
                string line = Bytecode.Script.Assembly.Render(block, w, instr);
                if (instr.Opcode.Operand == OperandKind.Func && instr.Operand is ushort id && NativeNames.Value.TryGetValue(id, out string? native)) {
                    line = $"{line,-24} ; {native}";
                }
                sb.Append(Body).Append(Indent).Append(line).Append('\n');
            }
            sb.Append(Body).Append("\"\"\");\n");
            sb.Append(Indent).Append("}\n");
        }
        sb.Append("}\n");
    }

    /// <summary>The C# name of each native (<c>Btl.SetStat</c>), from the reference assembly.</summary>
    private static readonly Lazy<Dictionary<ushort, string>> NativeNames = new(() => {
        Dictionary<ushort, string> names = [];
        foreach (Type t in typeof(NativeAttribute).Assembly.GetExportedTypes()) {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                if (m.GetCustomAttribute<NativeAttribute>() is NativeAttribute n) names.TryAdd((ushort)n.Id, $"{t.Name}.{m.Name}");
            }
        }
        return names;
    });

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
