// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

/// <summary>A mod's package for one script, and which script it is.</summary>
public sealed class CompiledMod(ScriptCategory category, string name, ScriptPackage package) {
    public ScriptCategory Category { get; } = category;

    /// <summary>The script's file name without extension (<c>m209</c>).</summary>
    public string Name { get; } = name;

    /// <summary>The package; <see cref="ScriptPackage.File"/> is for the caller to fill in (it needs the game's archive).</summary>
    public ScriptPackage Package { get; } = package;

    /// <summary>Warnings: <c>Debug.*</c> calls left out (<see cref="DebugMode.Drop"/>).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A number the linker gives (<see cref="SymbolExpr"/>): a new worker's or a new entry's.</summary>
internal sealed record LinkSymbol(RelocKind Kind, int Index);

public static partial class ScriptCompiler {
    /// <summary>
    ///     Compiles a mod's source for one script against the script's vanilla source, into a package with
    ///     only the mod's code and data (docs/formats/mod-package.md).
    /// </summary>
    /// <remarks>
    ///     The mod's files extend the vanilla classes: <c>partial class M209 { public static int Turns; }</c>
    ///     adds variables (without <c>[Var]</c> the linker allocates them in the shared data; with it they are
    ///     a fixed location); <c>partial class W01 { … }</c> adds to a vanilla worker: a method without an
    ///     attribute is a new entry, <c>[Prefix]</c>, <c>[Postfix]</c> and <c>[Replace]</c> patch a vanilla
    ///     method, <c>[Register]</c> fields name registers; a class deriving from <c>Worker0</c> …
    ///     <c>Worker6</c> (or a library class) is a new worker, its entries numbered as in vanilla source.
    /// </remarks>
    /// <exception cref="CompileException">The vanilla or the mod's source does not compile, or the mod does something a package cannot hold.</exception>
    public static CompiledMod CompileMod(IReadOnlyList<SyntaxTree> vanillaTrees, IReadOnlyList<SyntaxTree> modTrees, ScriptLibrary? library = null, CompileOptions? options = null) {
        library ??= ScriptLibrary.Empty;
        DebugCalls debug = new((options ?? CompileOptions.Default).Debug);
        CompiledScript vanilla = Compile(vanillaTrees, library);
        ScriptImage v = vanilla.Image;
        vanillaTrees = [.. vanillaTrees.OrderBy(t => t.FilePath, SourceOrder.Comparer)];
        modTrees = [.. modTrees.OrderBy(t => t.FilePath, SourceOrder.Comparer)];
        foreach (SyntaxTree tree in modTrees) {
            if (tree.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is Diagnostic d) {
                throw CompileException.At(d.Location, d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        // ----- The vanilla declarations ----------------------------------------------------------
        List<ClassDeclarationSyntax> classes = [.. vanillaTrees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())];
        ILookup<string, ClassDeclarationSyntax> byName = classes.ToLookup(c => c.Identifier.ValueText, StringComparer.Ordinal);
        ClassDeclarationSyntax mainClass = classes.First(c => Attributes(c).Any(a => NameOf(a) is "Event" or "Monster" or "Battle"));
        string mainName = mainClass.Identifier.ValueText;
        List<ClassDeclarationSyntax> mainParts = [.. byName[mainName]];
        Dictionary<string, VariableSymbol> variables = new(StringComparer.Ordinal);
        List<string> vanillaVarNames = [];
        foreach (FieldDeclarationSyntax field in mainParts.SelectMany(p => p.Members.OfType<FieldDeclarationSyntax>())) {
            string n = field.Declaration.Variables[0].Identifier.ValueText;
            variables[n] = new VariableSymbol(vanillaVarNames.Count, Variable(field));
            vanillaVarNames.Add(n);
        }
        AttributeSyntax workersAttribute = mainParts.SelectMany(Attributes).First(a => NameOf(a) == "Workers");
        List<string> vanillaWorkers = [.. AllPositional(workersAttribute).Select(t => ((TypeName)t).Name)];
        Dictionary<string, int> workers = new(StringComparer.Ordinal);
        foreach (string w in vanillaWorkers) workers[w] = workers.Count;

        // Each vanilla worker's entry methods, in layout order: their entries and code range.
        Dictionary<string, List<ClassDeclarationSyntax>> vanillaParts = new(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, (int[] Entries, uint Start, uint End)>> methods = new(StringComparer.Ordinal);
        for (int w = 0; w < vanillaWorkers.Count; w++) {
            (List<ClassDeclarationSyntax> parts, _) = WorkerParts([.. byName[vanillaWorkers[w]]], library);
            vanillaParts[vanillaWorkers[w]] = parts;
            WorkerImage wi = v.Workers[w];
            List<(string Name, int[] Entries, uint Start)> list = [];
            foreach (MethodDeclarationSyntax m in parts.SelectMany(p => p.Members).OfType<MethodDeclarationSyntax>()) {
                int[] entries = [.. EntriesOf(m, wi.Type == 0)];
                list.Add((m.Identifier.ValueText, entries, wi.Entries[entries[0]]));
            }
            Dictionary<string, (int[], uint, uint)> byMethod = new(StringComparer.Ordinal);
            for (int i = 0; i < list.Count; i++) {
                uint end = i + 1 < list.Count ? list[i + 1].Start : (uint)wi.Code.Length;
                byMethod[list[i].Name] = (list[i].Entries, list[i].Start, end);
            }
            methods[vanillaWorkers[w]] = byMethod;
        }

        // ----- The mod's declarations ------------------------------------------------------------
        ScriptPackage package = new() { Script = vanilla.Name };
        List<string> variableNames = [.. vanillaVarNames];
        DataEdits edits = new(package, v, vanilla.Category, v.Variables.Count, variableNames, var => variables.Values.First(x => x.Index == var).Entry);
        // Initial values: the static constructors of the script class and of vanilla workers, read once every variable is known.
        List<(ConstructorDeclarationSyntax Ctor, string Class, int? Worker)> ctors = [];
        StatementReader constants = new(variables, new Dictionary<string, RegExpr>(StringComparer.Ordinal), NativesByName.Value, workers, false);
        Dictionary<string, List<ClassDeclarationSyntax>> modParts = new(StringComparer.Ordinal);
        List<ClassDeclarationSyntax> mainModParts = [];
        List<(string Name, List<ClassDeclarationSyntax> Parts)> newWorkers = [];
        foreach (ClassDeclarationSyntax c in modTrees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())) {
            string n = c.Identifier.ValueText;
            if (c.Parent is ClassDeclarationSyntax) throw CompileException.At(c.Identifier.GetLocation(), "a nested class");
            if (n == mainName) {
                if (Attributes(c).FirstOrDefault() is AttributeSyntax a) throw CompileException.At(a, "a mod does not change the script class's attributes");
                mainModParts.Add(c);
                foreach (MemberDeclarationSyntax member in c.Members) {
                    if (member is MethodDeclarationSyntax m && IsHelper(m, functionWorker: false)) continue;
                    if (member is ConstructorDeclarationSyntax ctor) {
                        ctors.Add((ctor, n, null));
                        continue;
                    }
                    if (member is not FieldDeclarationSyntax field) throw CompileException.At(member, "a mod's part of the script class holds variables, helper methods and a static constructor (initial values)");
                    string fieldName = field.Declaration.Variables[0].Identifier.ValueText;
                    if (variables.ContainsKey(fieldName)) throw CompileException.At(field, $"{fieldName} is already a variable");
                    VarEntry entry = Variable(field, allocated: true);
                    bool fixedPlace = Attributes(field).Any(x => NameOf(x) == "Var");
                    if (fixedPlace && ArrayInitializer(field.Declaration.Variables[0].Initializer?.Value) is InitializerExpressionSyntax list) {
                        throw CompileException.At(list, "a variable at a fixed place ([Var]) has no initial value here: its value is the save's or the game's");
                    }
                    byte[]? initial = fixedPlace ? null : edits.Initial(field, entry, constants);
                    variables[fieldName] = new VariableSymbol(v.Variables.Count + package.Variables.Count, entry);
                    variableNames.Add(fieldName);
                    package.Variables.Add(new NewVariable {
                        Name = fieldName, Type = entry.Type, Storage = entry.Storage, Offset = fixedPlace ? entry.Offset : null,
                        Length = entry.Length, RowLength = entry.RowLength, Array = entry.IsArray, Initial = initial,
                    });
                }
            }
            else if (workers.ContainsKey(n)) {
                if (Attributes(c).FirstOrDefault() is AttributeSyntax a) throw CompileException.At(a, "a mod does not change a vanilla worker's attributes");
                if (c.BaseList is not null) throw CompileException.At(c.BaseList, "a mod does not change a vanilla worker's base class");
                if (!modParts.TryGetValue(n, out List<ClassDeclarationSyntax>? list)) modParts[n] = list = [];
                list.Add(c);
            }
            else if (byName[n].Any()) {
                throw CompileException.At(c.Identifier.GetLocation(), $"{n} is a vanilla class, but not a worker");
            }
            else {
                List<ClassDeclarationSyntax>? parts = newWorkers.FirstOrDefault(x => x.Name == n).Parts;
                if (parts is null) {
                    newWorkers.Add((n, parts = []));
                    workers[n] = vanillaWorkers.Count + newWorkers.Count - 1;
                }
                parts.Add(c);
            }
        }

        // The patches and new entries on vanilla workers, numbered before any code is read, so that
        // Link.Entry can name a new entry anywhere in the mod.
        // Helper methods (plain methods; an `async` method is a new entry) become function workers after
        // the new workers.
        InlineMethods inlines = InlineMethods.With([.. vanillaTrees, .. modTrees, .. library.Trees]);
        inlines.Script = new InlineMethods.ScriptContext(variables, workers, cls =>
            Registers([.. vanillaParts.GetValueOrDefault(cls) ?? [.. library.Classes[cls]], .. modParts.GetValueOrDefault(cls) ?? []]));
        HelperTable helpers = new(mainName) { Inlines = inlines };
        AddHelpers(helpers, mainName, mainModParts, functionWorker: false, registers: false);
        foreach ((string worker, List<ClassDeclarationSyntax> parts) in modParts) {
            List<ClassDeclarationSyntax> all = [.. vanillaParts[worker], .. parts];
            foreach (MethodDeclarationSyntax m in parts.SelectMany(p => p.Members).OfType<MethodDeclarationSyntax>()) {
                if (IsHelper(m, functionWorker: false)) helpers.Add(new Helper(worker, m, all));
            }
        }
        foreach ((string name, List<ClassDeclarationSyntax> parts) in newWorkers) {
            (List<ClassDeclarationSyntax> all, ushort type) = WorkerParts(parts, library);
            AddHelpers(helpers, name, all, type == 0);
        }
        for (int k = 0; k < helpers.All.Count; k++) helpers.All[k].Worker = vanillaWorkers.Count + newWorkers.Count + k;

        List<(string Worker, MethodDeclarationSyntax Method, EntryPatch Patch)> patches = [];
        Dictionary<EntryPatch, InjectAnchor> anchors = [];
        foreach ((string worker, List<ClassDeclarationSyntax> parts) in modParts) {
            foreach (MemberDeclarationSyntax member in parts.SelectMany(p => p.Members)) {
                if (member is FieldDeclarationSyntax) continue;
                if (member is ConstructorDeclarationSyntax ctor) {
                    ctors.Add((ctor, worker, workers[worker]));
                    continue;
                }
                if (member is not MethodDeclarationSyntax method) throw CompileException.At(member, "a worker holds methods, [Register] fields and a static constructor (its private data's initial values)");
                if (helpers.Contains(method) || InlineMethods.IsInline(method)) continue;
                if (method.Body is null && method.ExpressionBody is null) throw CompileException.At(method, "an entry has a body");
                PatchKind kind = PatchKind.Entry;
                string? target = null;
                InjectAnchor? anchor = null;
                foreach (AttributeSyntax a in method.AttributeLists.SelectMany(l => l.Attributes)) {
                    if (kind != PatchKind.Entry) throw CompileException.At(a, "one patch attribute per method");
                    if (NameOf(a) == "Inject") anchor = ReadInject(a);
                    (kind, target) = NameOf(a) switch {
                        "Prefix" => (PatchKind.Prefix, Target(a)),
                        "Postfix" => (PatchKind.Postfix, Target(a)),
                        "Replace" => (PatchKind.Replace, Target(a)),
                        "Inject" => (PatchKind.Inject, anchor!.Method),
                        "Entry" => throw CompileException.At(a, "a new entry gets its number when linking; patch a vanilla entry with [Replace(nameof(…))]"),
                        _ => throw CompileException.At(a, $"unknown attribute [{NameOf(a)}] on a mod's method"),
                    };
                    if (target is not null && !methods[worker].ContainsKey(target)) throw CompileException.At(a, $"{worker} has no entry method {target}");
                }
                if (kind == PatchKind.Entry && method.Modifiers.Any(SyntaxKind.OverrideKeyword)) {
                    throw CompileException.At(method.Identifier.GetLocation(), $"{worker} has its own {method.Identifier.ValueText}; patch it with [Prefix], [Postfix] or [Replace]");
                }
                EntryPatch entryPatch = new() { Kind = kind, Worker = workers[worker], Name = method.Identifier.ValueText, Target = target };
                if (anchor is not null) anchors[entryPatch] = anchor;
                patches.Add((worker, method, entryPatch));
            }
        }

        // A worker's number and an entry's, as numbers or as what the linker gives (Link.Worker,
        // Link.Entry, and requests written as calls: `_ = W01.BoostAsync();`).
        Expr WorkerNumber(string worker, SyntaxNode at) {
            if (!workers.TryGetValue(worker, out int number)) throw CompileException.At(at, $"no worker {worker}");
            return number >= vanillaWorkers.Count ? new SymbolExpr(new LinkSymbol(RelocKind.NewWorker, number - vanillaWorkers.Count)) : new IntLit(number);
        }
        Expr? EntryNumber(string worker, string method) {
            if (!workers.TryGetValue(worker, out int number)) return null;
            if (number >= vanillaWorkers.Count) {
                MethodDeclarationSyntax? m = newWorkers[number - vanillaWorkers.Count].Parts.SelectMany(p => p.Members).OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(x => x.Identifier.ValueText == method);
                return m is not null && EntriesOf(m) is [int first, ..] ? new IntLit(first) : null;
            }
            if (methods[worker].TryGetValue(method, out var vm)) return new IntLit(vm.Entries[0]);
            int k = patches.FindIndex(p => p.Worker == worker && p.Patch.Kind == PatchKind.Entry && p.Patch.Name == method);
            return k >= 0 ? new SymbolExpr(new LinkSymbol(RelocKind.NewEntry, k)) : null;
        }
        (Expr, Expr)? RequestTargets(string worker, string method) =>
            workers.ContainsKey(worker) && EntryNumber(worker, method) is Expr entry ? (WorkerNumber(worker, mainClass), entry) : null;

        Expr? Links(InvocationExpressionSyntax link) {
            GenericNameSyntax g = (GenericNameSyntax)((MemberAccessExpressionSyntax)link.Expression).Name;
            if (g.TypeArgumentList.Arguments is not [TypeSyntax t]) throw CompileException.At(link, "Link.Worker<T>() and Link.Entry<T>(method) name one worker");
            string worker = StatementReader.TypeName(t);
            switch (g.Identifier.ValueText) {
                case "Worker" when link.ArgumentList.Arguments.Count == 0:
                    return WorkerNumber(worker, t);
                case "Entry" when link.ArgumentList.Arguments is [ArgumentSyntax arg]: {
                    string method = StatementReader.MethodName(arg.Expression) ?? throw CompileException.At(arg, "the method by nameof(…) or as a string");
                    return EntryNumber(worker, method) ?? throw CompileException.At(arg, $"{worker} has no entry {method}");
                }
                default:
                    throw CompileException.At(link, "Link.Worker<T>() or Link.Entry<T>(method)");
            }
        }

        Dictionary<string, Dictionary<string, int>> entryMethods = new(StringComparer.Ordinal);
        // Inline methods read their bodies when first called, by then with every entry known.
        inlines.Script = inlines.Script! with { EntryMethods = entryMethods, RequestTargets = RequestTargets };
        foreach ((string w, var byMethod) in methods) entryMethods[w] = byMethod.ToDictionary(kv => kv.Key, kv => kv.Value.Entries[0], StringComparer.Ordinal);
        foreach ((string name, List<ClassDeclarationSyntax> parts) in newWorkers) {
            entryMethods[name] = parts.SelectMany(p => p.Members).OfType<MethodDeclarationSyntax>()
                .Where(m => EntriesOf(m).Count > 0).ToDictionary(m => m.Identifier.ValueText, m => EntriesOf(m)[0], StringComparer.Ordinal);
        }

        FragmentBuilder fragments = new(v.Variables.Count, vanillaWorkers.Count);

        // New messages (Text.New): each text once, numbered by the linker.
        Expr NewText(string text) {
            package.Messages ??= [];
            int k = package.Messages.FindIndex(m => m.Index is null && m.Text == text);
            if (k < 0) {
                package.Messages.Add(new PackageMessage(null, text));
                k = package.Messages.Count - 1;
            }
            return new SymbolExpr(new LinkSymbol(RelocKind.NewMessage, k));
        }

        // New code's locals: registers no code of the worker names, else hidden variables the linker
        // allocates in the private data of the workers that run the code, one element per level. An entry's
        // parameter fields: private when only its own worker requests it, else shared. The registers a
        // vanilla worker may change: its [Register] fields and those of the function workers its code calls.
        int Hide(Slot slot) => HideIn(slot, Storage.Private, SlotAllocator.Levels);
        Func<Slot, int> HideField(bool own) => slot => HideIn(slot, own ? Storage.Private : Storage.Shared, 1);
        int HideIn(Slot slot, Storage storage, int length) {
            package.Variables.Add(new NewVariable {
                Name = $"<local>{slot.Name}#{package.Variables.Count}", Type = slot.Float ? 6 : 5, Storage = (int)storage, Length = length, Array = length > 1,
            });
            return v.Variables.Count + package.Variables.Count - 1;
        }
        RegisterSet[] vanillaReach = Reach([.. vanillaWorkers.Select((name, w) => (
            RegisterSet.Of(Registers(vanillaParts[name]).Values),
            CallsIn(v.Workers[w].Code)))]);

        // Entries with parameters or a result (the mod's new entries and new workers'): their hidden fields.
        Dictionary<(string, string), EntrySignature> signatures = [];
        foreach ((string worker, MethodDeclarationSyntax method, EntryPatch patch) in patches) {
            if (patch.Kind == PatchKind.Entry && Signature(worker, method, HideField(RequestedOnlyByItself(worker, method.Identifier.ValueText, modTrees))) is EntrySignature sig) {
                signatures[(worker, method.Identifier.ValueText)] = sig;
            }
        }
        foreach ((string name, List<ClassDeclarationSyntax> parts) in newWorkers) {
            (List<ClassDeclarationSyntax> all, ushort type) = WorkerParts(parts, library);
            foreach (MethodDeclarationSyntax m in all.SelectMany(p => p.Members).OfType<MethodDeclarationSyntax>()) {
                if (EntriesOf(m, type == 0).Count > 0 && Signature(name, m, HideField(RequestedOnlyByItself(name, m.Identifier.ValueText, modTrees))) is EntrySignature sig) {
                    signatures.TryAdd((name, m.Identifier.ValueText), sig);
                }
            }
        }
        EntrySignature? SignatureOf(string w, string m) => signatures.GetValueOrDefault((w, m));

        // ----- The code: read and lowered first, its locals placed once the helpers' are -----------
        List<CompiledWorker> compiledNew = [];
        for (int i = 0; i < newWorkers.Count; i++) {
            (string name, List<ClassDeclarationSyntax> parts) = newWorkers[i];
            compiledNew.Add(Worker(parts, library, variables, workers, entryMethods, Links, debug, name, vanillaWorkers.Count + i, helpers, SignatureOf, RequestTargets, NewText, constructors: true));
        }
        List<FlowEmitter> patchCode = [];
        foreach ((string worker, MethodDeclarationSyntax method, EntryPatch patch) in patches) {
            int number = workers[worker];
            bool function = v.Workers[number].Type == 0;
            StatementReader reader = new(variables, Registers([.. vanillaParts[worker], .. modParts[worker]]), NativesByName.Value, workers, function, Links) {
                EntryMethods = entryMethods, RequestTargets = RequestTargets, DebugCalls = debug, Class = worker, Helpers = helpers.Find, ScriptClass = mainName, Inlines = helpers.Inlines,
                Signatures = SignatureOf, NewTexts = NewText,
                InlineClasses = [.. vanillaParts[worker].Select(p => p.Identifier.ValueText).Distinct(StringComparer.Ordinal).Reverse()],
            };
            EntrySignature? signature = patch.Kind == PatchKind.Entry ? SignatureOf(worker, method.Identifier.ValueText) : null;
            reader.Result = signature?.Result;
            if (patch.Kind != PatchKind.Entry && method.ParameterList.Parameters.Count > 0) throw CompileException.At(method.ParameterList, "a patch takes no parameters");
            FlowEmitter emitter = new() { TagOf = reader.LocationOf };
            try {
                List<Stmt> body = method.Body is BlockSyntax block
                    ? signature is null ? reader.Block(block.Statements) : reader.Method(block, signature.Parameters)
                    : reader.Method(method.ExpressionBody!, signature?.Parameters ?? []);
                emitter.BeginMethod();
                emitter.Block(body);
                // Entries and replacements end the entry; prefixes and postfixes run on (the linker chains them).
                if (patch.Kind is PatchKind.Entry or PatchKind.Replace) emitter.Statement(new OpStmt(function ? Opcodes.Ret : Opcodes.End));
                emitter.Number();
            }
            catch (FlowException ex) {
                throw CompileException.At(ex.Tag as Location ?? method.Identifier.GetLocation(), ex.Message);
            }
            foreach (Helper h in reader.Called) h.CalledBy.Add(number);
            patchCode.Add(emitter);
        }
        foreach (Helper h in helpers.All) CompileHelper(h, helpers, variables, workers, entryMethods, Links, RequestTargets, debug, SignatureOf, NewText);

        // The registers a worker's code may change: for a vanilla worker its [Register] fields (the mod's
        // too) and those of the function workers it calls; all in a function worker (its callers' are not known).
        RegisterSet WorkerRegisters(int w) {
            if (w < vanillaWorkers.Count) {
                string name = vanillaWorkers[w];
                RegisterSet mod = modParts.TryGetValue(name, out List<ClassDeclarationSyntax>? parts) ? RegisterSet.Of(Registers([.. vanillaParts[name], .. parts]).Values) : RegisterSet.None;
                return vanillaReach[w] | mod | (v.Workers[w].Type == 0 ? RegisterSet.All : RegisterSet.None);
            }
            int i = w - vanillaWorkers.Count;
            return i < compiledNew.Count ? compiledNew[i].Declared | (compiledNew[i].Image.Type == 0 ? RegisterSet.All : RegisterSet.None) : RegisterSet.All;
        }
        PlaceHelpers(helpers, WorkerRegisters, Hide);
        bool CallWaits(int w) => helpers.ByWorker(w)?.Waits ?? true;
        RegisterSet CallClobbers(int w) => helpers.ByWorker(w)?.Total ?? (w < vanillaReach.Length ? vanillaReach[w] : RegisterSet.All);

        // ----- New workers ---------------------------------------------------------------------
        for (int i = 0; i < newWorkers.Count; i++) {
            (string name, List<ClassDeclarationSyntax> parts) = newWorkers[i];
            CompiledWorker cw = compiledNew[i];
            WorkerImage wi = cw.Image;
            PlaceSlots(cw.Emitter, WorkerRegisters(vanillaWorkers.Count + i), Hide, CallWaits, callClobbers: CallClobbers);
            List<WorkerItem> items = ItemsOf(cw.Emitter);
            // Its private data ([Worker(PrivateLength = …)], [PrivateData]); its static constructor's initial values are read below.
            if (wi.PrivateLength != 0 && wi.Type == 0) throw CompileException.At(parts[0].Identifier.GetLocation(), "a function worker has no private data: it uses its caller's");
            if (wi.Private is not null && wi.PrivateLength == 0) throw CompileException.At(parts[0].Identifier.GetLocation(), "[PrivateData] needs [Worker(PrivateLength = …)]");
            if (wi.PrivateLength > 0) edits.NewWorker(vanillaWorkers.Count + i, wi.PrivateLength, wi.Private);
            foreach (ConstructorDeclarationSyntax ctor in parts.SelectMany(p => p.Members).OfType<ConstructorDeclarationSyntax>()) ctors.Add((ctor, name, vanillaWorkers.Count + i));
            (Fragment code, Dictionary<int, uint> entries) = fragments.Build(items, parts[0].Identifier.GetLocation());
            if (entries.Count == 0) throw CompileException.At(parts[0].Identifier.GetLocation(), "a worker without entries");
            uint[] table = new uint[entries.Keys.Max() + 1];
            for (int e = 0; e < table.Length; e++) {
                table[e] = entries.TryGetValue(e, out uint at) ? at : throw CompileException.At(parts[0].Identifier.GetLocation(), $"{name} has no entry {e}");
            }
            package.Workers.Add(new NewWorker { Name = name, Type = wi.Type, Word0C = wi.Unknown0C, Code = code, Entries = table, PrivateLength = wi.PrivateLength > 0 ? wi.PrivateLength : null });
        }

        // ----- Helpers ---------------------------------------------------------------------------
        foreach (Helper h in helpers.All) {
            (Fragment code, Dictionary<int, uint> entries) = fragments.Build(ItemsOf(h.Emitter!), h.Method.Identifier.GetLocation());
            package.Workers.Add(new NewWorker { Name = $"<helper>{h.Name}", Type = 0, Code = code, Entries = [entries[0]] });
        }

        // ----- Methods on vanilla workers ------------------------------------------------------
        for (int k = 0; k < patches.Count; k++) {
            (string worker, MethodDeclarationSyntax method, EntryPatch patch) = patches[k];
            FlowEmitter emitter = patchCode[k];
            int number = workers[worker];
            PlaceSlots(emitter, WorkerRegisters(number), Hide, CallWaits, callClobbers: CallClobbers);
            (patch.Code, Dictionary<int, uint> stray) = fragments.Build(ItemsOf(emitter), method.Identifier.GetLocation());
            if (stray.Count > 0) throw CompileException.At(method.Identifier.GetLocation(), "Entry(n) inside a mod's method");
            fragments.Workers.Add(number);
            package.Patches.Add(patch);
        }

        // ----- Injects: the statement each anchors to, in the vanilla method's code ------------------
        foreach ((string worker, MethodDeclarationSyntax _, EntryPatch patch) in patches) {
            if (!anchors.TryGetValue(patch, out InjectAnchor? anchor)) continue;
            int w = workers[worker];
            MethodDeclarationSyntax vanillaMethod = vanillaParts[worker].SelectMany(p => p.Members).OfType<MethodDeclarationSyntax>().First(m => m.Identifier.ValueText == anchor.Method);
            (_, uint start, uint end) = methods[worker][anchor.Method];
            patch.At = ResolveInject(anchor, vanillaMethod, w < vanilla.SourceMap.Count ? vanilla.SourceMap[w] : [], start, end);
        }

        // ----- Initial values -------------------------------------------------------------------
        foreach ((ConstructorDeclarationSyntax ctor, string cls, int? w) in ctors) edits.Read(ctor, cls, w, constants);
        for (int i = 0; i < newWorkers.Count; i++) package.Workers[i].Private = edits.PrivateOf(vanillaWorkers.Count + i);

        // ----- The vanilla parts it attaches to ------------------------------------------------
        foreach (int w in fragments.Workers.Order()) {
            package.Vanilla.Workers.Add(new WorkerRef(w, vanillaWorkers[w], VanillaHash.Worker(v.Workers[w])));
        }
        foreach (var target in patches.Where(p => p.Patch.Target is not null).Select(p => (p.Worker, Target: p.Patch.Target!)).Distinct()) {
            (int[] entries, uint start, uint end) = methods[target.Worker][target.Target];
            int w = workers[target.Worker];
            package.Vanilla.Entries.Add(new EntryRef(w, target.Target, entries, start, end, VanillaHash.Range(v.Workers[w], start, end)));
        }
        foreach (int i in fragments.Variables.Order()) {
            package.Vanilla.Variables.Add(new VariableRef(i, vanillaVarNames[i], VanillaHash.Variable(v.Variables[i])));
        }
        if (debug.Strings.Count > 0) package.DebugStrings = [.. debug.Strings];
        package.Format = Math.Max(package.Format, package.NeededFormat());
        return new CompiledMod(vanilla.Category, vanilla.Name, package) { Warnings = [.. debug.Warnings] };
    }

    private static string Target(AttributeSyntax a) =>
        Arguments(a).ToList() is [AttributeArgumentSyntax arg] && StatementReader.MethodName(arg.Expression) is string name
            ? name
            : throw CompileException.At(a, $"[{NameOf(a)}] names a vanilla method: [{NameOf(a)}(nameof(Init))]");

    /// <summary>Instructions to a <see cref="Fragment"/>: the operands the linker gives become relocations.</summary>
    private sealed class FragmentBuilder(int vanillaVariables, int vanillaWorkers) {
        /// <summary>The vanilla variables and workers the code uses, which the package lists with their hashes.</summary>
        public HashSet<int> Variables { get; } = [];
        public HashSet<int> Workers { get; } = [];

        public (Fragment Code, Dictionary<int, uint> Entries) Build(List<WorkerItem> items, Location where) {
            List<byte> code = [];
            Dictionary<int, uint> labels = [], entries = [];
            List<(int At, int Label, Location? Where)> jumps = [];
            List<Reloc> relocs = [];
            foreach (WorkerItem item in items) {
                switch (item.Kind) {
                    case AsmLineKind.Entry:
                        entries[item.Number] = (uint)code.Count;
                        continue;
                    case AsmLineKind.Label:
                        labels[item.Number] = (uint)code.Count;
                        continue;
                }
                CodeOp op = item.Op;
                int at = code.Count;
                OperandKind kind = Opcodes.All[op.Code].Operand;
                ushort? operand = op.Operand;
                if (op.Symbol is LinkSymbol s) {
                    relocs.Add(new Reloc(at, s.Kind, (uint)s.Index));
                    operand = 0;
                }
                else if (op.Pooled) {
                    relocs.Add(new Reloc(at, op.Code == Opcodes.PushC ? RelocKind.Int : RelocKind.Float, op.Value));
                    operand = 0;
                }
                else if (kind is OperandKind.Int or OperandKind.Float) {
                    throw CompileException.At(item.Where ?? where, "a mod cannot use a constant by its pool index (Pooled or Asm)");
                }
                else if (kind == OperandKind.Label && operand is ushort label) {
                    jumps.Add((at, label, item.Where));
                    operand = 0;
                }
                else if (kind == OperandKind.Var && operand is ushort var) {
                    if (var < vanillaVariables) Variables.Add(var);
                    relocs.Add(var < vanillaVariables ? new Reloc(at, RelocKind.Var, var) : new Reloc(at, RelocKind.NewVar, (uint)(var - vanillaVariables)));
                    operand = 0;
                }
                else if (kind == OperandKind.Worker && operand is ushort worker) {
                    if (worker < vanillaWorkers) Workers.Add(worker);
                    relocs.Add(worker < vanillaWorkers ? new Reloc(at, RelocKind.Worker, worker) : new Reloc(at, RelocKind.NewWorker, (uint)(worker - vanillaWorkers)));
                    operand = 0;
                }
                new Instr(0, op.Code, operand).Encode(code);
            }
            // The fragment's labels, numbered densely in the order of their numbers.
            List<int> numbers = [.. labels.Keys.Order()];
            Dictionary<int, int> index = numbers.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
            foreach ((int at, int label, Location? from) in jumps) {
                if (!index.TryGetValue(label, out int i)) throw CompileException.At(from ?? where, $"a jump to l{label}, which is not in this method");
                relocs.Add(new Reloc(at, RelocKind.Label, (uint)i));
            }
            relocs.Sort((a, b) => a.At.CompareTo(b.At));
            return (new Fragment { Bytes = [.. code], Labels = [.. numbers.Select(n => labels[n])], Relocs = relocs }, entries);
        }
    }
}
