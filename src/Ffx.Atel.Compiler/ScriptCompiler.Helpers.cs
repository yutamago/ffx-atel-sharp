// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

/// <summary>The helpers of a compilation, by class and name.</summary>
internal sealed class HelperTable(string scriptClass) {
    private readonly Dictionary<(string, string), Helper> _byName = [];
    private readonly HashSet<MethodDeclarationSyntax> _methods = [];

    public string ScriptClass { get; } = scriptClass;

    public List<Helper> All { get; } = [];

    public Helper? Find(string cls, string name) => _byName.GetValueOrDefault((cls, name));

    public bool Contains(MethodDeclarationSyntax method) => _methods.Contains(method);

    public void Add(Helper h) {
        if (!_byName.TryAdd((h.Class, h.Method.Identifier.ValueText), h)) {
            throw CompileException.At(h.Method.Identifier.GetLocation(), $"{h.Name} is declared twice (helpers have no overloads)");
        }
        _methods.Add(h.Method);
        All.Add(h);
    }

    /// <summary>The helper whose number is <paramref name="worker"/>.</summary>
    public Helper? ByWorker(int worker) => All.FirstOrDefault(h => h.Worker == worker);
}

public static partial class ScriptCompiler {
    /// <summary>
    ///     A helper method (docs/plan.md, "Three kinds of code"): a plain method, neither an entry
    ///     (<c>[Entry]</c>, <c>Init</c>/<c>Main</c>, <c>Run</c> in a function worker, <c>async</c>) nor a
    ///     patch (any attribute).
    /// </summary>
    private static bool IsHelper(MethodDeclarationSyntax m, bool functionWorker) =>
        m.AttributeLists.Count == 0 && !m.Modifiers.Any(SyntaxKind.AsyncKeyword) && !m.Modifiers.Any(SyntaxKind.OverrideKeyword)
        && !(functionWorker && m.Identifier.ValueText == "Run" && m.Modifiers.Any(SyntaxKind.StaticKeyword));

    /// <summary>The helpers among a class's methods.</summary>
    private static void AddHelpers(HelperTable table, string cls, IReadOnlyList<ClassDeclarationSyntax> parts, bool functionWorker, bool registers = true) {
        foreach (MethodDeclarationSyntax m in parts.SelectMany(p => p.Members).OfType<MethodDeclarationSyntax>()) {
            if (IsHelper(m, functionWorker)) table.Add(new Helper(cls, m, registers ? parts : []));
        }
    }

    /// <summary>Reads and lowers a helper's body: entry 0 of a function worker, ending in <c>ret</c>.</summary>
    private static void CompileHelper(
        Helper h, HelperTable table, IReadOnlyDictionary<string, VariableSymbol> variables, IReadOnlyDictionary<string, int> workers,
        IReadOnlyDictionary<string, Dictionary<string, int>> entryMethods, Func<InvocationExpressionSyntax, Expr?>? links,
        Func<string, string, (Expr, Expr)?>? requestTargets = null, DebugCalls? debug = null) {
        Dictionary<string, RegExpr> registers = h.Parts.Count == 0 ? [] : Registers(h.Parts);
        StatementReader reader = new(variables, registers, NativesByName.Value, workers, functionWorker: true, links) {
            EntryMethods = entryMethods, RequestTargets = requestTargets, DebugCalls = debug ?? new(DebugMode.Natives),
            Class = h.Class, Helpers = table.Find, ScriptClass = table.ScriptClass, Result = h.Result,
        };
        FlowEmitter emitter = new() { TagOf = reader.LocationOf };
        try {
            List<Stmt> body = h.Method.Body is BlockSyntax block
                ? reader.Method(block, h.ParameterNames.Zip(h.Parameters))
                : reader.Method(h.Method.ExpressionBody!, h.ParameterNames.Zip(h.Parameters));
            emitter.BeginMethod();
            emitter.Entry(0);
            emitter.Block(body);
            if (body.LastOrDefault() is not OpStmt { Op: Opcodes.Ret }) emitter.Statement(new OpStmt(Opcodes.Ret));
            emitter.Number();
        }
        catch (FlowException ex) {
            throw CompileException.At(ex.Tag as Location ?? h.Method.Identifier.GetLocation(), ex.Message);
        }
        h.Emitter = emitter;
        h.Calls.UnionWith(reader.Called);
    }

    /// <summary>
    ///     Places the helpers' slots, callees first (so a caller knows the registers each callee may change):
    ///     a helper avoids the registers its callers' workers name (<paramref name="workerRegisters"/>) and what
    ///     its callees change. Recursion and calls deeper than 3 are errors.
    /// </summary>
    private static void PlaceHelpers(HelperTable table, Func<int, RegisterSet> workerRegisters, Func<Slot, int> hide) {
        // The workers each helper runs on: those that call it, directly or through other helpers.
        Dictionary<Helper, HashSet<int>> runsOn = table.All.ToDictionary(h => h, h => new HashSet<int>(h.CalledBy));
        for (bool changed = true; changed;) {
            changed = false;
            foreach (Helper g in table.All) {
                foreach (Helper h in g.Calls) {
                    int before = runsOn[h].Count;
                    runsOn[h].UnionWith(runsOn[g]);
                    changed |= runsOn[h].Count != before;
                }
            }
        }
        Dictionary<Helper, int> depth = [];
        int Depth(Helper h, List<Helper> path) {
            if (depth.TryGetValue(h, out int d)) return d;
            if (path.Contains(h)) {
                throw CompileException.At(h.Method.Identifier.GetLocation(), $"recursion: {string.Join(" → ", path.SkipWhile(x => x != h).Append(h).Select(x => x.Name))} (call keeps no frames)");
            }
            path.Add(h);
            bool callsWorkers = CallsIn(h.Emitter!.Items).Any(w => table.ByWorker(w) is null);
            d = 1 + Math.Max(callsWorkers ? 1 : 0, h.Calls.Select(c => Depth(c, path)).DefaultIfEmpty(0).Max());
            path.RemoveAt(path.Count - 1);
            return depth[h] = d;
        }
        foreach (Helper h in table.All) {
            if (Depth(h, []) > 3 && (h.CalledBy.Count > 0 || table.All.Any(g => g.Calls.Contains(h)))) {
                throw CompileException.At(h.Method.Identifier.GetLocation(), $"{h.Name} calls {Depth(h, []) - 1} deep; a worker keeps 3 return addresses (call)");
            }
        }

        foreach (Helper h in table.All.OrderBy(h => depth[h])) {
            RegisterSet forbidden = h.Parts.Count == 0 ? RegisterSet.None : RegisterSet.Of(Registers(h.Parts).Values);
            foreach (int w in runsOn[h]) forbidden |= workerRegisters(w);
            SlotAllocator.Placed placed = PlaceSlots(h.Emitter!, forbidden, hide, w => table.ByWorker(w)?.Waits ?? true, h.Parameters,
                w => table.ByWorker(w)?.Total ?? workerRegisters(w));
            h.Total = placed.Registers | h.Calls.Aggregate(RegisterSet.None, (s, c) => s | c.Total);
            foreach (int w in CallsIn(h.Emitter!.Items)) {
                if (table.ByWorker(w) is null) h.Total |= workerRegisters(w);
            }
            h.Waits = placed.Waits;
            h.Placed = true;
        }
    }

}
