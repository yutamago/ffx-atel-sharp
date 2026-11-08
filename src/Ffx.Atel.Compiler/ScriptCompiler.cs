// SPDX-License-Identifier: MIT

using System.Text;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Ffx.Atel.Compiler;

/// <summary>Source the compiler cannot turn into a script, with where it is.</summary>
public sealed class CompileException(string message) : Exception(message) {
    public static CompileException At(SyntaxNode node, string message) => At(node.GetLocation(), message);

    public static CompileException At(Location location, string message) {
        FileLinePositionSpan span = location.GetLineSpan();
        return new CompileException($"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): {message}");
    }
}

/// <summary>A compiled script: which file it belongs in, and its script block.</summary>
public sealed class CompiledScript(ScriptCategory category, string name, ScriptImage image) {
    public ScriptCategory Category { get; } = category;

    /// <summary>The file name, without extension (<c>bjyt0400</c>).</summary>
    public string Name { get; } = name;

    public ScriptImage Image { get; } = image;

    /// <summary>The texts of the script's <c>Debug.Print</c> calls, which the loader needs (docs/formats/debug.md).</summary>
    public IReadOnlyList<string> DebugStrings { get; init; } = [];

    /// <summary>Warnings: <c>Debug.*</c> calls left out (<see cref="DebugMode.Drop"/>).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    ///     Per worker (helpers' function workers too), each instruction's offset in the worker's code, its length and
    ///     the statement it came from: what <c>[Inject]</c> resolves a statement of the source to.
    /// </summary>
    internal IReadOnlyList<IReadOnlyList<SourceSpan>> SourceMap { get; init; } = [];

    /// <summary>The script block's bytes.</summary>
    public byte[] Block() => Image.Write();

    /// <summary>The script file: the vanilla file with its script section replaced.</summary>
    /// <exception cref="ScriptFormatException">The block does not parse (a worker without entries, …).</exception>
    public ScriptFile ApplyTo(ScriptFile vanilla) => vanilla.ReplaceScript(ScriptBlock.Parse(Block()));
}

/// <summary>An instruction's place in its worker's code and the statement it was compiled from (null when none is known).</summary>
internal readonly record struct SourceSpan(uint Offset, uint Length, Location? Where);

/// <summary>
///     Atel source to a script block, for the source shape of Phase 2 (see
///     <c>Ffx.Atel.Decompiler.ScriptDecompiler</c>): declarations are read from the syntax, entry
///     bodies are <c>Asm("""…""")</c> statements, assembled per worker.
/// </summary>
/// <remarks>
///     Reading needs only the syntax trees. <see cref="Check"/> compiles them as C# against the
///     reference assembly (<c>Ffx.Atel.Game</c>), so the source is also checked as real C#.
/// </remarks>
public static partial class ScriptCompiler {
    public static CSharpParseOptions ParseOptions { get; } = new(LanguageVersion.Latest);

    /// <summary>Parses source files; <paramref name="path"/> names each in messages.</summary>
    public static SyntaxTree Parse(string text, string path = "") => CSharpSyntaxTree.ParseText(text, ParseOptions, path, Encoding.UTF8);

    /// <summary>
    ///     The source files of a script's folder: every <c>.cs</c> under it, but those under <c>obj</c> and
    ///     <c>bin</c> (build output), in <see cref="SourceOrder"/>.
    /// </summary>
    public static List<string> SourceFiles(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(folder, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(d => d is "obj" or "bin"))
            .Order(SourceOrder.Comparer)];

    /// <summary>Parses the source files of a script's folder (<see cref="SourceFiles"/>).</summary>
    public static List<SyntaxTree> ParseFolder(string folder) => [.. SourceFiles(folder).Select(f => Parse(File.ReadAllText(f), f))];

    /// <summary>
    ///     Compiles one script from its source files. The trees are taken in <see cref="SourceOrder"/> of
    ///     their paths, so the parts of a <c>partial</c> worker lay their methods out in that order; the
    ///     workers' order is <c>[Workers]</c>'s.
    /// </summary>
    /// <exception cref="CompileException">Syntax errors, or source the compiler cannot read or assemble.</exception>
    /// <remarks>A worker may derive from a class of <paramref name="library"/>, which gives its type and the members laid out before its own.</remarks>
    public static CompiledScript Compile(IReadOnlyList<SyntaxTree> trees, ScriptLibrary? library = null, CompileOptions? options = null) {
        library ??= ScriptLibrary.Empty;
        DebugCalls debug = new((options ?? CompileOptions.Default).Debug);
        trees = [.. trees.OrderBy(t => t.FilePath, SourceOrder.Comparer)];
        foreach (SyntaxTree tree in trees) {
            if (tree.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is Diagnostic d) {
                throw CompileException.At(d.Location, d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        List<ClassDeclarationSyntax> classes = trees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()).ToList();
        ILookup<string, ClassDeclarationSyntax> byName = classes.ToLookup(c => c.Identifier.ValueText, StringComparer.Ordinal);

        // The script class: the one with [Event], [Monster] or [Battle]; its parts in source order.
        List<(ClassDeclarationSyntax Class, AttributeSyntax Attribute, ScriptCategory Category)> mains = [];
        foreach (ClassDeclarationSyntax c in classes) {
            foreach (AttributeSyntax a in Attributes(c)) {
                ScriptCategory? category = NameOf(a) switch {
                    "Event" => ScriptCategory.Event, "Monster" => ScriptCategory.Monster, "Battle" => ScriptCategory.Battle, _ => null,
                };
                if (category is not null) mains.Add((c, a, category.Value));
            }
        }
        if (mains.Count == 0) throw new CompileException("no script class: none has [Event], [Monster] or [Battle]");
        if (mains.Count > 1) throw CompileException.At(mains[1].Attribute, "a second script class; one script per compilation");
        (ClassDeclarationSyntax mainClass, AttributeSyntax nameAttribute, ScriptCategory scriptCategory) = mains[0];
        string name = (string)Positional(nameAttribute, 0);
        List<ClassDeclarationSyntax> mainParts = byName[mainClass.Identifier.ValueText].ToList();
        List<AttributeSyntax> mainAttributes = mainParts.SelectMany(Attributes).ToList();

        ScriptImage image = new();
        List<uint>? pinnedInts = null, pinnedFloats = null;
        foreach (AttributeSyntax a in mainAttributes) {
            switch (NameOf(a)) {
                case "Event" or "Monster" or "Battle" or "Workers":
                    break;
                case "Header":
                    foreach ((string key, object value) in Named(a)) {
                        switch (key) {
                            case "Author": image.Author = Latin1((string)value, a); break;
                            case "Name": image.Name = Latin1((string)value, a); break;
                            case "Word14": image.Header14 = U32(value, a); break;
                            case "Word18": image.Header18 = U32(value, a); break;
                            case "Word1C": image.Header1C = U32(value, a); break;
                            case "Word24": image.Header24 = U32(value, a); break;
                            default: throw CompileException.At(a, $"[Header] has no {key}");
                        }
                    }
                    break;
                case "Pin":
                    image.Pins[(string)Positional(a, 0)] = I32(Positional(a, 1), a);
                    break;
                case "Patch":
                    image.Patches[I32(Positional(a, 0), a)] = HexBytes((string)Positional(a, 1), a);
                    break;
                case "Ints":
                    (pinnedInts ??= []).AddRange(AllPositional(a).Select(v => U32(v, a)));
                    break;
                case "Floats":
                    (pinnedFloats ??= []).AddRange(AllPositional(a).Select(v => BitConverter.SingleToUInt32Bits(F32(v, a))));
                    break;
                case "FloatBits":
                    (pinnedFloats ??= []).AddRange(AllPositional(a).Select(v => U32(v, a)));
                    break;
                case "MapData": image.Map = Data(a); break;
                case "SharedData": image.Shared = Data(a); break;
                case "OtherData": image.Other = Data(a); break;
                case "EventData": image.Event = Data(a); break;
                case "AreaData": image.Area = Data(a); break;
                default:
                    throw CompileException.At(a, $"unknown attribute [{NameOf(a)}] on the script class");
            }
        }

        if (mainParts.SelectMany(p => p.Members).OfType<ConstructorDeclarationSyntax>().FirstOrDefault() is ConstructorDeclarationSyntax ctor) {
            throw CompileException.At(ctor.Identifier.GetLocation(), "a static constructor sets a mod's initial values (atel compile-mod); a script's are its data areas ([SharedData(…)])");
        }

        // Variables: [Var] fields of the script class, in declaration order.
        Dictionary<string, VariableSymbol> variables = new(StringComparer.Ordinal);
        foreach (FieldDeclarationSyntax field in mainParts.SelectMany(p => p.Members.OfType<FieldDeclarationSyntax>())) {
            VarEntry entry = Variable(field);
            variables[field.Declaration.Variables[0].Identifier.ValueText] = new VariableSymbol(image.Variables.Count, entry);
            image.Variables.Add(entry);
        }

        // Workers, in [Workers] order.
        AttributeSyntax? workersAttribute = mainAttributes.FirstOrDefault(a => NameOf(a) == "Workers");
        List<object> workerTypes = workersAttribute is null ? [] : AllPositional(workersAttribute);
        Dictionary<string, int> workerIndex = new(StringComparer.Ordinal);
        foreach (object t in workerTypes) {
            if (t is not TypeName(string workerName)) throw CompileException.At(workersAttribute!, "[Workers] takes typeof(…) only");
            if (!workerIndex.TryAdd(workerName, workerIndex.Count)) throw CompileException.At(workersAttribute!, $"{workerName} is listed twice");
            if (!byName[workerName].Any()) throw CompileException.At(workersAttribute!, $"no class {workerName}");
        }
        foreach (ClassDeclarationSyntax c in classes) {
            string n = c.Identifier.ValueText;
            if (!workerIndex.ContainsKey(n) && c.BaseList?.Types.FirstOrDefault() is BaseTypeSyntax b && (WorkerType(b.Type) is not null || library.Classes.Contains(SimpleName(b.Type)))) {
                throw CompileException.At(c.Identifier.GetLocation(), $"worker {n} is not in [Workers]");
            }
        }
        List<(WorkerImage Image, List<WorkerItem> Items, string Name, Location Where)> workers = [];
        Dictionary<string, Dictionary<string, int>> entryMethods = EntryMethods(byName, workerIndex, library);
        // Helper methods (of the workers and the script class) become function workers after the others.
        // A shared worker's inline methods are in its library class.
        InlineMethods inlines = InlineMethods.With([.. trees, .. library.Trees]);
        HelperTable helpers = new(mainClass.Identifier.ValueText) { Inlines = inlines };
        AddHelpers(helpers, mainClass.Identifier.ValueText, mainParts, functionWorker: false, registers: false);
        foreach (string workerName in workerIndex.Keys) {
            (List<ClassDeclarationSyntax> parts, ushort type) = WorkerParts([.. byName[workerName]], library);
            AddHelpers(helpers, workerName, parts, type == 0);
        }
        for (int k = 0; k < helpers.All.Count; k++) helpers.All[k].Worker = workerIndex.Count + k;
        // Entries with parameters or a result: their hidden fields.
        PrivateArea hidden = new(image);
        Dictionary<(string, string), EntrySignature> signatures = [];
        foreach (string workerName in workerIndex.Keys) {
            (List<ClassDeclarationSyntax> parts, ushort type) = WorkerParts([.. byName[workerName]], library);
            foreach (MethodDeclarationSyntax m in parts.SelectMany(p => p.Members).OfType<MethodDeclarationSyntax>()) {
                if (EntriesOf(m, type == 0).Count > 0 && Signature(workerName, m, HideField(RequestedOnlyByItself(workerName, m.Identifier.ValueText, trees))) is EntrySignature sig) {
                    signatures.TryAdd((workerName, m.Identifier.ValueText), sig);
                }
            }
        }
        EntrySignature? SignatureOf(string w, string m) => signatures.GetValueOrDefault((w, m));
        inlines.Script = new InlineMethods.ScriptContext(variables, workerIndex, cls => InlineRegisters(cls, byName, library)) {
            EntryMethods = entryMethods, Signatures = SignatureOf,
        };
        List<CompiledWorker> compiled = [];
        foreach ((string workerName, int number) in workerIndex) {
            List<ClassDeclarationSyntax> parts = byName[workerName].ToList();
            CompiledWorker cw = Worker(parts, library, variables, workerIndex, entryMethods, debug: debug, name: workerName, number: number, helpers: helpers, signatures: SignatureOf);
            compiled.Add(cw);
            image.Workers.Add(cw.Image);
        }
        foreach (Helper h in helpers.All) CompileHelper(h, helpers, variables, workerIndex, entryMethods, links: null, debug: debug, signatures: SignatureOf);
        // New code's locals: registers where free, else hidden variables in the private data of the workers
        // that run the code, one element per level. An entry's parameter fields: private when only its own
        // worker requests it, else at the end of the shared data.
        int Hide(Slot slot) => hidden.Add(slot.Float, SlotAllocator.Levels);
        Func<Slot, int> HideField(bool own) => slot => {
            if (own) return hidden.Add(slot.Float, 1);
            if (image.Shared is null) throw new CompileException($"{slot.Name} needs hidden storage, and the script has no shared data (storage 4)");
            int offset = (image.Shared.Length + 3) & ~3;
            image.Shared = [.. image.Shared, .. new byte[offset + 4 - image.Shared.Length]];
            image.Variables.Add(new VarEntry((uint)(slot.Float ? 6 : 5) << 28 | (uint)Storage.Shared << 25 | (uint)offset, 1, 0));
            return image.Variables.Count - 1;
        };
        RegisterSet[] reach = Reach([.. compiled.Select(c => (c.Declared, CallsIn(c.Emitter.Items)))]);
        RegisterSet WorkerRegisters(int w) => w < compiled.Count ? reach[w] | (compiled[w].Image.Type == 0 ? RegisterSet.All : RegisterSet.None) : RegisterSet.All;
        PlaceHelpers(helpers, WorkerRegisters, Hide);
        for (int w = 0; w < compiled.Count; w++) {
            CompiledWorker cw = compiled[w];
            PlaceSlots(cw.Emitter, WorkerRegisters(w), Hide, c => helpers.ByWorker(c)?.Waits ?? true, callClobbers: c => helpers.ByWorker(c)?.Total ?? WorkerRegisters(c));
            workers.Add((cw.Image, ItemsOf(cw.Emitter), $"worker {w} ({workerIndex.First(x => x.Value == w).Key})", cw.Where));
        }
        foreach (Helper h in helpers.All) {
            WorkerImage wi = new() { Type = 0 };
            image.Workers.Add(wi);
            workers.Add((wi, ItemsOf(h.Emitter!), $"helper {h.Name}", h.Method.Identifier.GetLocation()));
        }

        // The constant pools: pinned, or by the rules (docs/corpus.md); then the code.
        List<IReadOnlyList<CodeOp>> ops = [.. workers.Select(w => (IReadOnlyList<CodeOp>)[.. w.Items.Where(i => i.Kind == AsmLineKind.Instruction).Select(i => i.Op)])];
        foreach ((byte push, bool pinned, string attribute) in new[] { (Opcodes.PushC, pinnedInts is not null, "[Ints(…)]"), (Opcodes.PushFC, pinnedFloats is not null, "[Floats(…)]") }) {
            if (pinned) continue;
            foreach (var w in workers) {
                if (w.Items.FirstOrDefault(i => i.Kind == AsmLineKind.Instruction && i.Op.Code == push && !i.Op.Pooled) is WorkerItem raw) {
                    throw CompileException.At(raw.Where ?? w.Where, $"Asm or Pooled(…) refers to the constant pool by index; pin the pool with {attribute}");
                }
            }
        }
        image.IntConstants.AddRange(pinnedInts ?? ConstantPools.Ints(ops));
        image.FloatConstants.AddRange(pinnedFloats ?? ConstantPools.Floats(ops, ConstantPools.Argc(Natives)));
        Dictionary<uint, int> intIndex = [], floatIndex = [];
        for (int k = 0; k < image.IntConstants.Count; k++) intIndex.TryAdd(image.IntConstants[k], k);
        for (int k = 0; k < image.FloatConstants.Count; k++) floatIndex.TryAdd(image.FloatConstants[k], k);
        List<IReadOnlyList<SourceSpan>> sourceMap = [];
        foreach ((WorkerImage wi, List<WorkerItem> items, string label, Location where) in workers) {
            sourceMap.Add(Assemble(wi, items, label, where, intIndex, floatIndex));
        }
        hidden.Lay();
        return new CompiledScript(scriptCategory, name, image) { DebugStrings = [.. debug.Strings], Warnings = [.. debug.Warnings], SourceMap = sourceMap };
    }

    /// <summary>Compiles one script from source text.</summary>
    public static CompiledScript Compile(string source, string path = "") => Compile([Parse(source, path)]);

    // ----- Declarations ----------------------------------------------------------------------

    /// <param name="allocated">A mod's field: without <c>[Var]</c> it is a shared variable the linker places (offset 0 here).</param>
    private static VarEntry Variable(FieldDeclarationSyntax field, bool allocated = false) {
        AttributeSyntax? a = field.AttributeLists.SelectMany(l => l.Attributes).FirstOrDefault(x => NameOf(x) == "Var");
        if (a is null && !allocated) throw CompileException.At(field, "a field of the script class without [Var]");
        if (field.Declaration.Variables.Count != 1) throw CompileException.At(field, "one variable per [Var] field");
        TypeSyntax type = field.Declaration.Type;
        int rank = 0;
        if (type is ArrayTypeSyntax at && at.RankSpecifiers.Count == 1 && at.RankSpecifiers[0].Rank is 1 or 2) {
            type = at.ElementType;
            rank = at.RankSpecifiers[0].Rank;
        }
        uint varType = (uint)(type is PredefinedTypeSyntax p ? p.Keyword.Kind() switch {
            SyntaxKind.ByteKeyword => 0, SyntaxKind.SByteKeyword => 1, SyntaxKind.UShortKeyword => 2, SyntaxKind.ShortKeyword => 3,
            SyntaxKind.UIntKeyword => 4, SyntaxKind.IntKeyword => 5, SyntaxKind.FloatKeyword => 6,
            _ => throw CompileException.At(type, $"a variable cannot be {p.Keyword.ValueText}"),
        } : throw CompileException.At(type, "a variable is byte, sbyte, ushort, short, uint, int or float, or a 1-D or 2-D array of one"));
        object storage = a is null ? new EnumMember(nameof(Storage.Shared)) : Positional(a, 0);
        uint storageNumber = storage is EnumMember(string member) && Enum.TryParse(member, out Storage s) ? (uint)s
            : throw CompileException.At(a!, "the storage is Storage.<area>");
        int offset = a is null ? 0 : I32(Positional(a, 1), a);
        if (offset is < 0 or > 0xff_ffff) throw CompileException.At(a!, "the offset is 24 bits");
        int length = 1, rowLength = 0;
        foreach ((string key, object value) in a is null ? [] : Named(a)) {
            switch (key) {
                case "Length": length = I32(value, a!); break;
                default: throw CompileException.At(a!, $"[Var] has no {key}");
            }
        }
        // An array's length is its size: `byte[] V3 = new byte[4];`; a 2-D array's second dimension
        // is the row length: `byte[,] V3 = new byte[2, 8];`.
        VariableDeclaratorSyntax declarator = field.Declaration.Variables[0];
        InitializerExpressionSyntax? list = allocated ? ArrayInitializer(declarator.Initializer?.Value) : null;
        if (rank > 0) {
            if (declarator.Initializer?.Value is ArrayCreationExpressionSyntax { Type.RankSpecifiers: [{ Sizes: var sizes }] } && sizes.Count == rank && !sizes.Any(z => z is OmittedArraySizeExpressionSyntax)) {
                int first = I32(Eval(sizes[0]), sizes[0]);
                if (rank == 1) length = first;
                else {
                    rowLength = I32(Eval(sizes[1]), sizes[1]);
                    length = first * rowLength;
                    if (rowLength <= 0) throw CompileException.At(sizes[1], "a 2-D array has rows of at least 1");
                }
            }
            else if (list is not null) {
                // A mod's new variable with its initial values: its size is theirs (`int[] Table = { 1, 2, 3 };`).
                length = list.Expressions.Count;
                if (rank == 2) {
                    rowLength = list.Expressions.FirstOrDefault() is InitializerExpressionSyntax row ? row.Expressions.Count : 0;
                    if (rowLength <= 0 || list.Expressions.Any(r => r is not InitializerExpressionSyntax x || x.Expressions.Count != rowLength)) {
                        throw CompileException.At(list, "a 2-D array's initializer has rows of one length: { { 1, 2 }, { 3, 4 } }");
                    }
                    length *= rowLength;
                }
                if (length == 0) throw CompileException.At(list, "an array variable has at least one element");
            }
            else if (rank == 2 || !(a is not null && Named(a).Any(n => n.Key == "Length"))) {
                throw CompileException.At(declarator, rank == 2 ? "a 2-D array variable needs its size: = new T[rows, cols]" : "an array variable needs its size: = new T[n]");
            }
        }
        else if (declarator.Initializer is not null && !(allocated && a is null)) {
            throw CompileException.At(declarator.Initializer, allocated
                ? "a variable at a fixed place ([Var]) has no initial value here: its value is the save's or the game's"
                : "initial values are in the data areas ([SharedData(…)]); a mod sets one in a static constructor");
        }
        else if (length != 1) {
            throw CompileException.At(field, "a variable that is not an array has length 1");
        }
        if (length is < 0 or > 0xffff || rowLength is < 0 or > 0xffff) throw CompileException.At((SyntaxNode?)a ?? field, "the length and row length are 16 bits");
        uint location = varType << 28 | storageNumber << 25 | (rank > 0 ? VarEntry.ArrayBit : 0) | (uint)offset;
        return new VarEntry(location, (ushort)length, (ushort)rowLength);
    }

    private static string SimpleName(TypeSyntax type) => type switch {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax q => q.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => "",
    };

    /// <summary>The worker type a base class names: <c>Worker0</c> … <c>Worker6</c>, or their names (<c>Actor</c> is type 1).</summary>
    private static ushort? WorkerType(TypeSyntax type) {
        string n = SimpleName(type);
        if (n.Length == 7 && n.StartsWith("Worker", StringComparison.Ordinal) && n[6] is >= '0' and <= '6') return (ushort)(n[6] - '0');
        return TypeNames.TryGetValue(n, out ushort t) ? t : null;
    }

    /// <summary>The reference assembly's names of the worker types (<c>Ffx.Atel.Actor : Worker1</c>).</summary>
    private static readonly Dictionary<string, ushort> TypeNames = new(StringComparer.Ordinal) {
        ["FunctionWorker"] = 0, ["Actor"] = 1, ["LineTrigger"] = 2, ["BattleWorker"] = 2, ["BoxTrigger"] = 3,
        ["LogicWorker"] = 4, ["LineSensor"] = 5, ["BoxSensor"] = 6,
    };

    /// <summary>An entry, a label or an instruction of a worker, in code order; where it came from for errors.</summary>
    private sealed record WorkerItem(AsmLineKind Kind, int Number, CodeOp Op, Location? Where);

    /// <summary>A worker's header and code before its locals are placed; <see cref="Declared"/> are its <c>[Register]</c> fields.</summary>
    private sealed record CompiledWorker(WorkerImage Image, FlowEmitter Emitter, RegisterSet Declared, Location Where);

    /// <summary>The workers a code's <c>call</c>s go to.</summary>
    private static IEnumerable<int> CallsIn(IEnumerable<FlowItem> items) =>
        items.Where(i => i.Kind == FlowItemKind.Instruction && i.Op.Code == Opcodes.Call && i.Op.Operand is not null).Select(i => (int)i.Op.Operand!.Value);

    /// <summary>The workers a worker's bytecode <c>call</c>s.</summary>
    private static IEnumerable<int> CallsIn(byte[] code) {
        List<int> calls = [];
        for (int pc = 0; pc < code.Length;) {
            byte b = code[pc];
            if ((b & 0x80) != 0 && pc + 2 < code.Length && (b & 0x7f) == Opcodes.Call) calls.Add(code[pc + 1] | code[pc + 2] << 8);
            pc += (b & 0x80) != 0 ? 3 : 1;
        }
        return calls;
    }

    /// <summary>
    ///     For each worker, the registers its code may change: its own <c>[Register]</c> fields and those of every
    ///     function worker it calls, directly or not.
    /// </summary>
    private static RegisterSet[] Reach(IReadOnlyList<(RegisterSet Own, IEnumerable<int> Calls)> workers) {
        RegisterSet[] result = new RegisterSet[workers.Count];
        for (int w = 0; w < workers.Count; w++) {
            HashSet<int> seen = [w];
            Stack<int> todo = new([w]);
            RegisterSet set = RegisterSet.None;
            while (todo.TryPop(out int x)) {
                set |= workers[x].Own;
                foreach (int c in workers[x].Calls) {
                    if (c < workers.Count && seen.Add(c)) todo.Push(c);
                }
            }
            result[w] = set;
        }
        return result;
    }

    /// <summary>Places the slots of an emitter's code (<see cref="SlotAllocator"/>).</summary>
    private static SlotAllocator.Placed PlaceSlots(
        FlowEmitter emitter, RegisterSet forbidden, Func<Slot, int> hide, Func<int, bool>? callWaits = null, IReadOnlyList<Slot>? parameters = null,
        Func<int, RegisterSet>? callClobbers = null) =>
        SlotAllocator.Place(emitter.Items, new SlotContext {
            Forbidden = forbidden,
            NativeWaits = id => Natives[id]?.Polls ?? true,
            CallWaits = callWaits ?? (_ => true),
            CallClobbers = callClobbers ?? (_ => RegisterSet.All),
            Hide = hide,
            Parameters = parameters ?? [],
        });

    private static CompiledWorker Worker(
        List<ClassDeclarationSyntax> own, ScriptLibrary library, IReadOnlyDictionary<string, VariableSymbol> variables, IReadOnlyDictionary<string, int> workers,
        IReadOnlyDictionary<string, Dictionary<string, int>> entryMethods, Func<InvocationExpressionSyntax, Expr?>? links = null, DebugCalls? debug = null,
        string? name = null, int number = -1, HelperTable? helpers = null, Func<string, string, EntrySignature?>? signatures = null,
        Func<string, string, (Expr, Expr)?>? requestTargets = null, Func<string, Expr>? newTexts = null, bool constructors = false) {
        (List<ClassDeclarationSyntax> parts, ushort type) = WorkerParts(own, library);
        WorkerImage wi = new() { Type = type };
        foreach (AttributeSyntax a in parts.SelectMany(Attributes)) {
            switch (NameOf(a)) {
                case "Worker":
                    foreach ((string key, object value) in Named(a)) {
                        switch (key) {
                            case "Word0C": wi.Unknown0C = U32(value, a); break;
                            case "PrivateLength": wi.PrivateLength = U32(value, a); break;
                            default: throw CompileException.At(a, $"[Worker] has no {key}");
                        }
                    }
                    break;
                case "PrivateData": wi.Private = Data(a); break;
                default: throw CompileException.At(a, $"unknown attribute [{NameOf(a)}] on a worker");
            }
        }

        Dictionary<string, RegExpr> registers = Registers(parts);
        RegisterSet declared = RegisterSet.Of(registers.Values);
        StatementReader reader = new(variables, registers, NativesByName.Value, workers, wi.Type == 0, links) {
            EntryMethods = entryMethods, DebugCalls = debug ?? new(DebugMode.Natives),
            Class = name, Helpers = helpers is null ? null : helpers.Find, ScriptClass = helpers?.ScriptClass, Inlines = helpers?.Inlines,
            InlineClasses = [.. parts.Select(p => p.Identifier.ValueText).Distinct(StringComparer.Ordinal).Reverse()],
            Signatures = signatures, RequestTargets = requestTargets, NewTexts = newTexts,
        };

        // Entry methods in declaration order: each places its entries, then its code follows. The
        // constructs' labels are numbered over the whole worker (FlowEmitter).
        FlowEmitter emitter = new() { TagOf = reader.LocationOf };
        Location where = parts[0].Identifier.GetLocation();
        try {
            foreach (MemberDeclarationSyntax member in parts.SelectMany(p => p.Members)) {
                if (member is FieldDeclarationSyntax) continue;
                // A mod's new worker: its static constructor sets its private data's initial values (read by CompileMod).
                if (constructors && member is ConstructorDeclarationSyntax) continue;
                if (member is not MethodDeclarationSyntax method) throw CompileException.At(member, "a worker holds methods and [Register] fields only (for now)");
                if (helpers?.Contains(method) == true || InlineMethods.IsInline(method)) continue;
                where = method.Identifier.GetLocation();
                List<AttributeSyntax> attributes = method.AttributeLists.SelectMany(l => l.Attributes).ToList();
                List<int> entries = EntriesOf(method, wi.Type == 0);
                List<int>? pin = null;
                foreach (AttributeSyntax e in attributes) {
                    switch (NameOf(e)) {
                        case "Entry": break;
                        case "Labels": (pin ??= []).AddRange(AllPositional(e).Select(v => I32(v, e))); break;
                        default: throw CompileException.At(e, $"unknown attribute [{NameOf(e)}] on an entry");
                    }
                }
                if (entries.Count == 0) throw CompileException.At(method.Identifier.GetLocation(), "an async method without [Entry] (or an Init or Main override, or Run in a function worker)");
                EntrySignature? signature = name is null ? null : signatures?.Invoke(name, method.Identifier.ValueText);
                reader.Result = signature?.Result;
                List<Stmt> body;
                if (method.Body is BlockSyntax block) body = signature is null ? reader.Block(block.Statements) : reader.Method(block, signature.Parameters);
                else if (method.ExpressionBody is ArrowExpressionClauseSyntax arrow) {
                    // `=> e`: one statement, then the end (`return e;` gives its own).
                    body = reader.Method(arrow, signature?.Parameters ?? []);
                    if (signature?.Result is null) body.Add(new OpStmt(wi.Type == 0 ? Opcodes.Ret : Opcodes.End));
                }
                else throw CompileException.At(method, "an entry has a body");
                emitter.BeginMethod(pin);
                foreach (int n in entries) emitter.Entry(n);
                emitter.Block(body);
            }
            emitter.Number();
        }
        catch (FlowException ex) {
            throw CompileException.At(ex.Tag as Location ?? where, ex.Message);
        }
        foreach (Helper h in reader.Called) h.CalledBy.Add(number);
        return new CompiledWorker(wi, emitter, declared, parts[0].Identifier.GetLocation());
    }

    /// <summary>
    ///     A worker's parts and type: its base class is Worker0 … Worker6, or a library class, whose parts
    ///     (and its base's) come first.
    /// </summary>
    /// <summary>The registers an inline method of class <paramref name="cls"/> may name: its worker's (with the library classes it derives from), or a library class's.</summary>
    private static Dictionary<string, RegExpr> InlineRegisters(string cls, ILookup<string, ClassDeclarationSyntax> byName, ScriptLibrary library) {
        List<ClassDeclarationSyntax> own = [.. byName[cls]];
        if (own.Count == 0) own = [.. library.Classes[cls]];
        if (own.Count == 0 || own.All(p => p.BaseList is null)) return Registers(own);
        try {
            return Registers(WorkerParts(own, library).Parts);
        }
        catch (CompileException) {
            return Registers(own);
        }
    }

    private static (List<ClassDeclarationSyntax> Parts, ushort Type) WorkerParts(List<ClassDeclarationSyntax> own, ScriptLibrary library) {
        List<ClassDeclarationSyntax> parts = [.. own];
        ushort? type = null;
        BaseTypeSyntax? b = own.Select(p => p.BaseList?.Types.FirstOrDefault()).LastOrDefault(x => x is not null);
        HashSet<string> seen = new(StringComparer.Ordinal);
        while (b is not null) {
            type = WorkerType(b.Type);
            if (type is not null) break;
            string baseName = SimpleName(b.Type);
            List<ClassDeclarationSyntax> inherited = [.. library.Classes[baseName]];
            if (inherited.Count == 0 || !seen.Add(baseName)) throw CompileException.At(b, "a worker derives from a worker type (Actor, LineTrigger, …, or Worker0 … Worker6) or a library class");
            parts.InsertRange(0, inherited);
            b = inherited.Select(p => p.BaseList?.Types.FirstOrDefault()).LastOrDefault(x => x is not null);
        }
        return (parts, type ?? throw CompileException.At(own[0].Identifier.GetLocation(), "a worker derives from a worker type (Actor, LineTrigger, …, or Worker0 … Worker6) or a library class"));
    }

    /// <summary>A worker's <c>[Register(n)]</c> fields: <c>int</c> for the integer registers, <c>float</c> for the float ones.</summary>
    private static Dictionary<string, RegExpr> Registers(IEnumerable<ClassDeclarationSyntax> parts) {
        Dictionary<string, RegExpr> registers = new(StringComparer.Ordinal);
        foreach (FieldDeclarationSyntax field in parts.SelectMany(p => p.Members.OfType<FieldDeclarationSyntax>())) {
            AttributeSyntax a = Attributes(field).FirstOrDefault(x => NameOf(x) == "Register")
                ?? throw CompileException.At(field, "a worker field is a [Register] (for now)");
            int n = I32(Positional(a, 0), a);
            RegExpr reg = field.Declaration.Type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.IntKeyword } ? new RegExpr(RegKind.Int, n)
                : field.Declaration.Type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.FloatKeyword } ? new RegExpr(RegKind.Float, n)
                : throw CompileException.At(field.Declaration.Type, "a register is int or float");
            if (n < 0 || n >= (reg.Kind == RegKind.Int ? 4 : 10)) throw CompileException.At(a, reg.Kind == RegKind.Int ? "integer registers are 0-3" : "float registers are 0-9");
            foreach (VariableDeclaratorSyntax v in field.Declaration.Variables) registers[v.Identifier.ValueText] = reg;
        }
        return registers;
    }

    /// <summary>The emitter's items as entries, labels a jump uses, and instructions with their label numbers.</summary>
    private static List<WorkerItem> ItemsOf(FlowEmitter emitter) {
        List<WorkerItem> items = [];
        foreach (FlowItem it in emitter.Items) {
            Location? at = it.Tag as Location;
            switch (it.Kind) {
                case FlowItemKind.Entry:
                    items.Add(new WorkerItem(AsmLineKind.Entry, it.Entry, default, at));
                    break;
                case FlowItemKind.Label:
                    if (it.Target!.Used || it.Target.Fixed is not null) items.Add(new WorkerItem(AsmLineKind.Label, it.Target.Number, default, at));
                    break;
                default:
                    items.Add(new WorkerItem(AsmLineKind.Instruction, 0, it.Target is FlowLabel t ? it.Op with { Operand = (ushort)t.Number } : it.Op, at));
                    break;
            }
        }
        return items;
    }

    /// <summary>The lines of <c>Asm("""…""")</c>; line numbers in assembly errors are the source file's.</summary>
    internal static AsmStmt AsmLines(ExpressionStatementSyntax statement, InvocationExpressionSyntax call) {
        if (call.ArgumentList.Arguments.Count != 1 || call.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax { Token.Value: string text } literal) {
            throw CompileException.At(statement, "Asm takes one string literal");
        }
        FileLinePositionSpan span = literal.GetLocation().GetLineSpan();
        bool multiLine = literal.Token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken);
        int first = span.StartLinePosition.Line + 1 + (multiLine ? 1 : 0);
        string[] lines = text.Split('\n');
        List<AsmLine> result = [];
        for (int n = 0; n < lines.Length; n++) {
            AsmLine? line;
            try {
                line = Bytecode.Script.Assembly.ParseLine(lines[n], first + n);
            }
            catch (AssemblyException ex) {
                throw new CompileException($"{span.Path}({(ex.Line > 0 ? ex.Line : first)}): {StripLine(ex)}");
            }
            if (line is not AsmLine l) continue;
            if (l.Kind == AsmLineKind.Worker) throw new CompileException($"{span.Path}({first + n}): `worker` inside an entry");
            result.Add(l);
        }
        return new AsmStmt(result);
    }

    /// <summary>Lays a worker's items out as code, entry and label tables, with the pools' indices.</summary>
    /// <returns>Each instruction's offset, length and statement (<see cref="CompiledScript.SourceMap"/>).</returns>
    private static List<SourceSpan> Assemble(WorkerImage wi, List<WorkerItem> items, string name, Location where, Dictionary<uint, int> ints, Dictionary<uint, int> floats) {
        WorkerAssembler asm = new() { Name = name };
        List<SourceSpan> map = [];
        uint pc = 0;
        foreach (WorkerItem item in items) {
            try {
                switch (item.Kind) {
                    case AsmLineKind.Entry: asm.PlaceEntry(item.Number); break;
                    case AsmLineKind.Label: asm.PlaceLabel(item.Number); break;
                    default:
                        CodeOp op = item.Op;
                        ushort? index = null;
                        if (op.Pooled) {
                            Dictionary<uint, int> pool = op.Code == Opcodes.PushC ? ints : floats;
                            index = pool.TryGetValue(op.Value, out int k) ? (ushort)k
                                : throw CompileException.At(item.Where ?? where, $"the pinned {(op.Code == Opcodes.PushC ? "integer" : "float")} pool has no {(op.Code == Opcodes.PushC ? ((int)op.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) : BitConverter.UInt32BitsToSingle(op.Value).ToString("R", System.Globalization.CultureInfo.InvariantCulture))}");
                        }
                        Instr instr = op.ToInstr(index);
                        asm.Emit(instr);
                        map.Add(new SourceSpan(pc, instr.Length, item.Where));
                        pc += instr.Length;
                        break;
                }
            }
            catch (AssemblyException ex) {
                throw CompileException.At(item.Where ?? where, StripLine(ex));
            }
        }
        AssembledWorker done;
        try {
            done = asm.Finish();
        }
        catch (AssemblyException ex) {
            throw CompileException.At(where, ex.Message);
        }
        if (done.Entries.Length == 0) throw CompileException.At(where, "a worker without entries");
        if (done.Entries.Concat(done.Labels).Any(o => o >= done.Code.Length)) {
            throw CompileException.At(where, "an entry or label after the worker's last instruction");
        }
        wi.Code = done.Code;
        wi.Entries = done.Entries;
        wi.Labels = done.Labels;
        return pc == done.Code.Length ? map : [];
    }

    /// <summary>The natives of the reference assembly.</summary>
    public static NativeSignatures Natives => s_natives.Value;

    private static readonly Lazy<NativeSignatures> s_natives = new(() => NativeSignatures.FromAssembly(typeof(Atel.Worker).Assembly));

    internal static readonly Lazy<Dictionary<string, NativeSignature>> NativesByName = new(() => Natives.All.ToDictionary(n => n.Name, StringComparer.Ordinal));

    private static string StripLine(AssemblyException ex) =>
        ex.Line > 0 && ex.Message.StartsWith($"line {ex.Line}: ", StringComparison.Ordinal) ? ex.Message[$"line {ex.Line}: ".Length..] : ex.Message;

    private static void Wrap(SyntaxNode node, Action action) {
        try {
            action();
        }
        catch (AssemblyException ex) {
            throw CompileException.At(node, StripLine(ex));
        }
    }

    private static byte[] Data(AttributeSyntax a) {
        int length = I32(Positional(a, 0), a);
        if (length < 0) throw CompileException.At(a, "a negative length");
        try {
            return HexText.Parse((string)Positional(a, 1), length);
        }
        catch (FormatException ex) {
            throw CompileException.At(a, ex.Message);
        }
    }

    private static byte[] HexBytes(string hex, AttributeSyntax a) {
        try {
            return Convert.FromHexString(hex);
        }
        catch (FormatException ex) {
            throw CompileException.At(a, ex.Message);
        }
    }

    private static byte[] Latin1(string s, AttributeSyntax a) =>
        s.All(c => c <= 0xff) ? Encoding.Latin1.GetBytes(s) : throw CompileException.At(a, "the string has characters outside Latin-1");

    // ----- Attribute arguments ---------------------------------------------------------------

    private sealed record TypeName(string Name);

    private sealed record EnumMember(string Name);

    private static IEnumerable<AttributeSyntax> Attributes(MemberDeclarationSyntax m) => m.AttributeLists.SelectMany(l => l.Attributes);

    /// <summary><c>Event</c> for <c>[Event]</c>, <c>[EventAttribute]</c> and <c>[Ffx.Atel.Event]</c>.</summary>
    internal static string NameOf(AttributeSyntax a) {
        string n = a.Name switch {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax q => q.Name.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => a.Name.ToString(),
        };
        return n.EndsWith("Attribute", StringComparison.Ordinal) && n.Length > "Attribute".Length ? n[..^"Attribute".Length] : n;
    }

    private static IEnumerable<AttributeArgumentSyntax> Arguments(AttributeSyntax a) => a.ArgumentList?.Arguments ?? [];

    private static List<object> AllPositional(AttributeSyntax a) =>
        Arguments(a).Where(x => x.NameEquals is null && x.NameColon is null).Select(x => Eval(x.Expression)).ToList();

    private static object Positional(AttributeSyntax a, int i) {
        List<object> args = AllPositional(a);
        return i < args.Count ? args[i] : throw CompileException.At(a, $"[{NameOf(a)}] needs {i + 1} arguments");
    }

    private static IEnumerable<(string Key, object Value)> Named(AttributeSyntax a) =>
        Arguments(a).Where(x => x.NameEquals is not null).Select(x => (x.NameEquals!.Name.Identifier.ValueText, Eval(x.Expression)));

    /// <summary>The value of a constant attribute argument: literals, negation, <c>typeof</c>, the reference assembly's constants (<c>Save.StoryProgress</c>), enum members.</summary>
    private static object Eval(ExpressionSyntax e) => e switch {
        LiteralExpressionSyntax l => l.Token.Value ?? throw CompileException.At(e, "null is not a value here"),
        ParenthesizedExpressionSyntax p => Eval(p.Expression),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } u => Eval(u.Operand) switch {
            int i => (object)(-(long)i), uint i => -(long)i, long i => -i, ulong i and <= 0x8000_0000_0000_0000 => (long)(0 - i),
            float f => (object)(-f), double d => (object)(-d),
            _ => throw CompileException.At(e, "cannot negate this"),
        },
        TypeOfExpressionSyntax t => new TypeName(t.Type switch {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => t.Type.ToString(),
        }),
        MemberAccessExpressionSyntax m when GameConstants.Lookup(m, enums: false) is int constant => constant,
        MemberAccessExpressionSyntax m => new EnumMember(m.Name.Identifier.ValueText),
        _ => throw CompileException.At(e, "not a constant the compiler reads (yet)"),
    };

    private static long Integer(object v, SyntaxNode at) => v switch {
        int i => i, uint i => i, long i => i, ulong i and <= long.MaxValue => (long)i,
        short i => i, ushort i => i, byte i => i, sbyte i => i, char c => c,
        _ => throw CompileException.At(at, $"expected an integer, not {v}"),
    };

    private static int I32(object v, SyntaxNode at) => Integer(v, at) is long l and >= int.MinValue and <= int.MaxValue ? (int)l : throw CompileException.At(at, $"{v} is out of range");

    /// <summary>A 32-bit word, written signed or unsigned.</summary>
    private static uint U32(object v, SyntaxNode at) => Integer(v, at) is long l and >= int.MinValue and <= uint.MaxValue ? (uint)l : throw CompileException.At(at, $"{v} is out of range");

    private static float F32(object v, SyntaxNode at) => v switch {
        float f => f,
        double d => (float)d,
        _ => Integer(v, at),
    };

    // ----- Checking as C# --------------------------------------------------------------------

    private static readonly Lazy<MetadataReference[]> References = new(() => {
        string[] platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator);
        string[] wanted = ["System.Runtime.dll", "System.Private.CoreLib.dll"];
        return [
            .. platform.Where(p => wanted.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)).Select(p => MetadataReference.CreateFromFile(p)),
            MetadataReference.CreateFromFile(typeof(Atel.Worker).Assembly.Location),
        ];
    });

    /// <summary>
    ///     Warnings the Atel SDK will silence: CS0162 (unreachable code), because the original kept
    ///     the code of <c>if (0)</c> and of everything after an <c>end</c> (docs/corpus.md, "Code no
    ///     entry reaches"), and the decompiler writes it; CS1998 (an <c>async</c> method without
    ///     <c>await</c>), because an entry other workers request is <c>async Task</c> whether or not it
    ///     waits itself (docs/plan.md, "Requests between workers").
    /// </summary>
    public static IReadOnlyDictionary<string, ReportDiagnostic> Suppressed { get; } =
        new Dictionary<string, ReportDiagnostic> { ["CS0162"] = ReportDiagnostic.Suppress, ["CS1998"] = ReportDiagnostic.Suppress };

    /// <summary>
    ///     Compiles the trees as C# against the reference assembly, with the Atel analyzers
    ///     (<c>Ffx.Atel.Analyzers</c>, docs/analyzers.md) attached, and returns the diagnostics:
    ///     warnings and errors, C#'s and the ATL rules'.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Check(IReadOnlyList<SyntaxTree> trees, ScriptLibrary? library = null) =>
        Diagnose(Compilation("AtelCheck", trees, library is null || library.Trees.Count == 0 ? [] : [library.Reference]));

    /// <summary>A compilation's warnings and errors, with the Atel analyzers attached.</summary>
    internal static IReadOnlyList<Diagnostic> Diagnose(CSharpCompilation compilation) {
        CompilationWithAnalyzers analyzed = compilation.WithAnalyzers(Analyzers.AtelAnalyzers.All,
            new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null, concurrentAnalysis: false, logAnalyzerExecutionTime: false));
        return analyzed.GetAllDiagnosticsAsync().GetAwaiter().GetResult().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
    }

    internal static CSharpCompilation Compilation(string name, IEnumerable<SyntaxTree> trees, IEnumerable<MetadataReference> references) =>
        CSharpCompilation.Create(
            name, trees, [.. References.Value, .. references],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
                .WithSpecificDiagnosticOptions(Suppressed));
}
