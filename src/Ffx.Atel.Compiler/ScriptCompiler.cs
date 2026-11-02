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

    /// <summary>The script block's bytes.</summary>
    public byte[] Block() => Image.Write();

    /// <summary>The script file: the vanilla file with its script section replaced.</summary>
    /// <exception cref="ScriptFormatException">The block does not parse (a worker without entries, …).</exception>
    public ScriptFile ApplyTo(ScriptFile vanilla) => vanilla.ReplaceScript(ScriptBlock.Parse(Block()));
}

/// <summary>
///     Atel source to a script block, for the source shape of Phase 2 (see
///     <c>Ffx.Atel.Decompiler.ScriptDecompiler</c>): declarations are read from the syntax, entry
///     bodies are <c>Asm("""…""")</c> statements, assembled per worker.
/// </summary>
/// <remarks>
///     Reading needs only the syntax trees. <see cref="Check"/> compiles them as C# against the
///     reference assembly (<c>Ffx.Atel.Game</c>), so the source is also checked as real C#.
/// </remarks>
public static class ScriptCompiler {
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
    public static CompiledScript Compile(IReadOnlyList<SyntaxTree> trees, ScriptLibrary? library = null) {
        library ??= ScriptLibrary.Empty;
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
        foreach ((string workerName, int number) in workerIndex) {
            List<ClassDeclarationSyntax> parts = byName[workerName].ToList();
            (WorkerImage wi, List<WorkerItem> items) = Worker(parts, library, variables, workerIndex);
            workers.Add((wi, items, $"worker {number} ({workerName})", parts[0].Identifier.GetLocation()));
            image.Workers.Add(wi);
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
        foreach ((WorkerImage wi, List<WorkerItem> items, string label, Location where) in workers) {
            Assemble(wi, items, label, where, intIndex, floatIndex);
        }
        return new CompiledScript(scriptCategory, name, image);
    }

    /// <summary>Compiles one script from source text.</summary>
    public static CompiledScript Compile(string source, string path = "") => Compile([Parse(source, path)]);

    // ----- Declarations ----------------------------------------------------------------------

    private static VarEntry Variable(FieldDeclarationSyntax field) {
        AttributeSyntax a = field.AttributeLists.SelectMany(l => l.Attributes).FirstOrDefault(x => NameOf(x) == "Var")
            ?? throw CompileException.At(field, "a field of the script class without [Var]");
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
        object storage = Positional(a, 0);
        uint storageNumber = storage is EnumMember(string member) && Enum.TryParse(member, out Storage s) ? (uint)s
            : throw CompileException.At(a, "the storage is Storage.<area>");
        int offset = I32(Positional(a, 1), a);
        if (offset is < 0 or > 0xff_ffff) throw CompileException.At(a, "the offset is 24 bits");
        int length = 1, rowLength = 0;
        foreach ((string key, object value) in Named(a)) {
            switch (key) {
                case "Length": length = I32(value, a); break;
                default: throw CompileException.At(a, $"[Var] has no {key}");
            }
        }
        // An array's length is its size: `byte[] V3 = new byte[4];`; a 2-D array's second dimension
        // is the row length: `byte[,] V3 = new byte[2, 8];`.
        VariableDeclaratorSyntax declarator = field.Declaration.Variables[0];
        if (rank > 0) {
            if (declarator.Initializer?.Value is ArrayCreationExpressionSyntax { Type.RankSpecifiers: [{ Sizes: var sizes }] } && sizes.Count == rank) {
                int first = I32(Eval(sizes[0]), sizes[0]);
                if (rank == 1) length = first;
                else {
                    rowLength = I32(Eval(sizes[1]), sizes[1]);
                    length = first * rowLength;
                    if (rowLength <= 0) throw CompileException.At(sizes[1], "a 2-D array has rows of at least 1");
                }
            }
            else if (rank == 2 || !Named(a).Any(n => n.Key == "Length")) {
                throw CompileException.At(declarator, rank == 2 ? "a 2-D array variable needs its size: = new T[rows, cols]" : "an array variable needs its size: = new T[n]");
            }
        }
        else if (declarator.Initializer is not null) {
            throw CompileException.At(declarator.Initializer, "initial values are not read yet (they are in the data areas)");
        }
        else if (length != 1) {
            throw CompileException.At(field, "a variable that is not an array has length 1");
        }
        if (length is < 0 or > 0xffff || rowLength is < 0 or > 0xffff) throw CompileException.At(a, "the length and row length are 16 bits");
        uint location = varType << 28 | storageNumber << 25 | (rank > 0 ? VarEntry.ArrayBit : 0) | (uint)offset;
        return new VarEntry(location, (ushort)length, (ushort)rowLength);
    }

    private static string SimpleName(TypeSyntax type) => type switch {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax q => q.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => "",
    };

    private static ushort? WorkerType(TypeSyntax type) {
        string n = SimpleName(type);
        return n.Length == 7 && n.StartsWith("Worker", StringComparison.Ordinal) && n[6] is >= '0' and <= '6' ? (ushort)(n[6] - '0') : null;
    }

    /// <summary>An entry, a label or an instruction of a worker, in code order; where it came from for errors.</summary>
    private sealed record WorkerItem(AsmLineKind Kind, int Number, CodeOp Op, Location? Where);

    private static (WorkerImage Image, List<WorkerItem> Items) Worker(
        List<ClassDeclarationSyntax> own, ScriptLibrary library, IReadOnlyDictionary<string, VariableSymbol> variables, IReadOnlyDictionary<string, int> workers) {
        // The base class: Worker0 … Worker6, or a library class, whose parts (and its base's) come first.
        List<ClassDeclarationSyntax> parts = [.. own];
        ushort? type = null;
        BaseTypeSyntax? b = own.Select(p => p.BaseList?.Types.FirstOrDefault()).LastOrDefault(x => x is not null);
        HashSet<string> seen = new(StringComparer.Ordinal);
        while (b is not null) {
            type = WorkerType(b.Type);
            if (type is not null) break;
            string baseName = SimpleName(b.Type);
            List<ClassDeclarationSyntax> inherited = [.. library.Classes[baseName]];
            if (inherited.Count == 0 || !seen.Add(baseName)) throw CompileException.At(b, "a worker derives from Worker0 … Worker6 or a library class");
            parts.InsertRange(0, inherited);
            b = inherited.Select(p => p.BaseList?.Types.FirstOrDefault()).LastOrDefault(x => x is not null);
        }
        WorkerImage wi = new() { Type = type ?? throw CompileException.At(own[0].Identifier.GetLocation(), "a worker derives from Worker0 … Worker6 or a library class") };
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

        // [Register(n)] fields: int for the integer registers, float for the float ones.
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
        StatementReader reader = new(variables, registers, NativesByName.Value, workers, wi.Type == 0);

        // Entry methods in declaration order: each places its entries, then its code follows. The
        // constructs' labels are numbered over the whole worker (FlowEmitter).
        FlowEmitter emitter = new() { TagOf = reader.LocationOf };
        Location where = parts[0].Identifier.GetLocation();
        try {
            foreach (MemberDeclarationSyntax member in parts.SelectMany(p => p.Members)) {
                if (member is FieldDeclarationSyntax) continue;
                if (member is not MethodDeclarationSyntax method) throw CompileException.At(member, "a worker holds entry methods and [Register] fields only (for now)");
                where = method.Identifier.GetLocation();
                List<AttributeSyntax> attributes = method.AttributeLists.SelectMany(l => l.Attributes).ToList();
                List<int> entries = [];
                List<int>? pin = null;
                foreach (AttributeSyntax e in attributes) {
                    switch (NameOf(e)) {
                        case "Entry": entries.Add(I32(Positional(e, 0), e)); break;
                        case "Labels": (pin ??= []).AddRange(AllPositional(e).Select(v => I32(v, e))); break;
                        default: throw CompileException.At(e, $"unknown attribute [{NameOf(e)}] on an entry");
                    }
                }
                // The engine's entries as overrides of the base class: Init is entry 0, Main entry 1.
                if (entries.Count == 0 && method.Modifiers.Any(SyntaxKind.OverrideKeyword)) {
                    switch (method.Identifier.ValueText) {
                        case "Init": entries.Add(0); break;
                        case "Main": entries.Add(1); break;
                    }
                }
                if (entries.Count == 0) throw CompileException.At(method.Identifier.GetLocation(), "a method without [Entry] (or an Init or Main override)");
                if (method.ExpressionBody is not null || method.Body is null) throw CompileException.At(method, "an entry has a block body");
                List<Stmt> body = reader.Block(method.Body.Statements);
                emitter.BeginMethod(pin);
                foreach (int n in entries) emitter.Entry(n);
                emitter.Block(body);
            }
            emitter.Number();
        }
        catch (FlowException ex) {
            throw CompileException.At(ex.Tag as Location ?? where, ex.Message);
        }
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
        return (wi, items);
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
    private static void Assemble(WorkerImage wi, List<WorkerItem> items, string name, Location where, Dictionary<uint, int> ints, Dictionary<uint, int> floats) {
        WorkerAssembler asm = new() { Name = name };
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
                        asm.Emit(op.ToInstr(index));
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
    }

    /// <summary>The natives of the reference assembly.</summary>
    public static NativeSignatures Natives => s_natives.Value;

    private static readonly Lazy<NativeSignatures> s_natives = new(() => NativeSignatures.FromAssembly(typeof(Atel.Worker).Assembly));

    private static readonly Lazy<Dictionary<string, NativeSignature>> NativesByName = new(() => Natives.All.ToDictionary(n => n.Name, StringComparer.Ordinal));

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
    private static string NameOf(AttributeSyntax a) {
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
    ///     entry reaches"), and the decompiler writes it.
    /// </summary>
    public static IReadOnlyDictionary<string, ReportDiagnostic> Suppressed { get; } =
        new Dictionary<string, ReportDiagnostic> { ["CS0162"] = ReportDiagnostic.Suppress };

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
