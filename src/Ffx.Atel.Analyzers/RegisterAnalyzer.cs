// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Ffx.Atel.Analyzers;

/// <summary>
///     ATL0001 (docs/plan.md, "Variables"): a <c>[Register]</c> field whose value is carried across a
///     wait in one entry (written before the wait on some path, read after it before being written
///     again), while another entry of the same worker writes it. A worker has one set of registers;
///     while one entry waits, another can run on a higher level and overwrite them.
/// </summary>
/// <remarks>
///     Waits are the direct ones: <c>await</c>, natives with <c>Polls</c>, <c>RequestWaitStart</c> and
///     <c>RequestWaitEnd</c>. Function workers (<c>Worker0</c>) are left out: they run on their caller's
///     level and registers.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RegisterAnalyzer : DiagnosticAnalyzer {
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.RegisterAcrossWait];

    private sealed class Crossing(IFieldSymbol field, Location wait) {
        public IFieldSymbol Field { get; } = field;
        public Location Wait { get; } = wait;
    }

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start => {
            if (AtelModel.Create(start.Compilation) is not AtelModel model || model.RegisterAttribute is null) return;
            start.RegisterSymbolStartAction(symbolStart => {
                INamedTypeSymbol type = (INamedTypeSymbol)symbolStart.Symbol;
                if (!model.IsWorker(type) || model.IsFunctionWorker(type)) return;
                ImmutableArray<IFieldSymbol> registers = [
                    .. AllFields(type).Where(f => f.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, model.RegisterAttribute))),
                ];
                if (registers.IsEmpty) return;
                ConcurrentDictionary<IMethodSymbol, ImmutableHashSet<IFieldSymbol>> writes = new(SymbolEqualityComparer.Default);
                ConcurrentDictionary<IMethodSymbol, ImmutableArray<Crossing>> crossings = new(SymbolEqualityComparer.Default);
                symbolStart.RegisterOperationBlockAction(block => {
                    if (block.OwningSymbol is not IMethodSymbol method || method.MethodKind != MethodKind.Ordinary) return;
                    if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, type)) return;
                    IOperation? body = block.OperationBlocks.FirstOrDefault(o => o is IMethodBodyOperation or IBlockOperation);
                    if (body is null) return;
                    Analyze(model, registers, method, body, () => block.GetControlFlowGraph(body), writes, crossings);
                });
                symbolStart.RegisterSymbolEndAction(end => {
                    foreach (KeyValuePair<IMethodSymbol, ImmutableArray<Crossing>> m in crossings) {
                        foreach (Crossing x in m.Value) {
                            IMethodSymbol? writer = writes.Where(w => !SymbolEqualityComparer.Default.Equals(w.Key, m.Key) && model.IsEntry(w.Key) && w.Value.Contains(x.Field))
                                .Select(w => w.Key).OrderBy(w => w.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0).FirstOrDefault();
                            if (writer is not null) end.ReportDiagnostic(Diagnostic.Create(Rules.RegisterAcrossWait, x.Wait, x.Field.Name, writer.Name));
                        }
                    }
                });
            }, SymbolKind.NamedType);
        });
    }

    private static IEnumerable<IFieldSymbol> AllFields(INamedTypeSymbol type) {
        for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType) {
            foreach (IFieldSymbol f in t.GetMembers().OfType<IFieldSymbol>()) yield return f;
        }
    }

    // ----- One method ---------------------------------------------------------------------------

    private enum Kind { Read, Write, Wait }

    private readonly struct Event(Kind kind, int field, IOperation at) {
        public Kind Kind { get; } = kind;
        public int Field { get; } = field;
        public IOperation At { get; } = at;
    }

    private static void Analyze(AtelModel model, ImmutableArray<IFieldSymbol> registers, IMethodSymbol method, IOperation body, System.Func<ControlFlowGraph> graph,
        ConcurrentDictionary<IMethodSymbol, ImmutableHashSet<IFieldSymbol>> writes, ConcurrentDictionary<IMethodSymbol, ImmutableArray<Crossing>> crossings) {
        // A cheap pass first: most methods write no register, and most never wait.
        HashSet<int> written = [];
        bool waits = false, reads = false;
        foreach (IOperation o in body.Descendants()) {
            if (model.Waits(o)) waits = true;
            if (o is IFieldReferenceOperation f && Index(registers, f) is int k) {
                if (IsWrite(f)) written.Add(k);
                else reads = true;
                if (IsCompound(f)) reads = true;
            }
        }
        if (written.Count > 0) writes[method] = [.. written.Select(k => registers[k])];
        if (!waits || !reads || written.Count == 0) return;

        ControlFlowGraph cfg = graph();
        ImmutableArray<BasicBlock> blocks = cfg.Blocks;
        List<Event>[] events = new List<Event>[blocks.Length];
        for (int b = 0; b < blocks.Length; b++) {
            List<Event> list = events[b] = [];
            foreach (IOperation o in blocks[b].Operations) Collect(model, registers, o, list);
            if (blocks[b].BranchValue is IOperation v) Collect(model, registers, v, list);
        }
        int n = registers.Length;
        ulong all = n >= 64 ? ulong.MaxValue : (1UL << n) - 1;

        // Forward: registers this method may have written on some path to each point.
        ulong[] writtenIn = new ulong[blocks.Length], writtenOut = new ulong[blocks.Length];
        // Backward: registers read before written from each point (live).
        ulong[] liveIn = new ulong[blocks.Length], liveOut = new ulong[blocks.Length];
        for (bool changed = true; changed;) {
            changed = false;
            for (int b = 0; b < blocks.Length; b++) {
                ulong inW = 0;
                foreach (ControlFlowBranch p in blocks[b].Predecessors) inW |= writtenOut[p.Source.Ordinal];
                ulong outW = inW;
                foreach (Event e in events[b]) if (e.Kind == Kind.Write) outW |= 1UL << e.Field;
                if (inW != writtenIn[b] || outW != writtenOut[b]) {
                    writtenIn[b] = inW;
                    writtenOut[b] = outW;
                    changed = true;
                }
            }
            for (int b = blocks.Length - 1; b >= 0; b--) {
                ulong outL = 0;
                if (blocks[b].FallThroughSuccessor?.Destination is BasicBlock f) outL |= liveIn[f.Ordinal];
                if (blocks[b].ConditionalSuccessor?.Destination is BasicBlock t) outL |= liveIn[t.Ordinal];
                ulong inL = outL;
                for (int k = events[b].Count - 1; k >= 0; k--) inL = Step(inL, events[b][k]);
                if (inL != liveIn[b] || outL != liveOut[b]) {
                    liveIn[b] = inL;
                    liveOut[b] = outL;
                    changed = true;
                }
            }
        }

        List<Crossing> found = [];
        for (int b = 0; b < blocks.Length; b++) {
            // Live after each event, from the end of the block back.
            ulong[] liveAfter = new ulong[events[b].Count];
            ulong live = liveOut[b];
            for (int k = events[b].Count - 1; k >= 0; k--) {
                liveAfter[k] = live;
                live = Step(live, events[b][k]);
            }
            ulong w = writtenIn[b];
            for (int k = 0; k < events[b].Count; k++) {
                Event e = events[b][k];
                if (e.Kind == Kind.Write) w |= 1UL << e.Field;
                if (e.Kind != Kind.Wait) continue;
                ulong across = liveAfter[k] & w & all;
                for (int f = 0; f < n; f++) {
                    if ((across & (1UL << f)) != 0) found.Add(new Crossing(registers[f], e.At.Syntax.GetLocation()));
                }
            }
        }
        if (found.Count > 0) crossings[method] = [.. found];
    }

    private static ulong Step(ulong live, Event e) => e.Kind switch {
        Kind.Write => live & ~(1UL << e.Field),
        Kind.Read => live | (1UL << e.Field),
        _ => live,
    };

    private static int? Index(ImmutableArray<IFieldSymbol> registers, IFieldReferenceOperation f) {
        if (f.Instance is not (null or IInstanceReferenceOperation)) return null;
        for (int k = 0; k < registers.Length; k++) {
            if (SymbolEqualityComparer.Default.Equals(registers[k], f.Field.OriginalDefinition)) return k;
        }
        return null;
    }

    private static bool IsWrite(IOperation o) => o.Parent switch {
        ISimpleAssignmentOperation a => a.Target == o,
        ICompoundAssignmentOperation a => a.Target == o,
        IIncrementOrDecrementOperation => true,
        IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } => true,
        IDeconstructionAssignmentOperation => true,
        _ => false,
    };

    private static bool IsCompound(IOperation o) => o.Parent switch {
        ICompoundAssignmentOperation a => a.Target == o,
        IIncrementOrDecrementOperation => true,
        IArgumentOperation { Parameter.RefKind: RefKind.Ref } => true,
        _ => false,
    };

    /// <summary>The reads, writes and waits of one operation tree, in evaluation order.</summary>
    private static void Collect(AtelModel model, ImmutableArray<IFieldSymbol> registers, IOperation o, List<Event> into) {
        switch (o) {
            case ISimpleAssignmentOperation a when a.Target is IFieldReferenceOperation t && Index(registers, t) is int k:
                Collect(model, registers, a.Value, into);
                into.Add(new Event(Kind.Write, k, t));
                return;
            case IFieldReferenceOperation f when Index(registers, f) is int k:
                if (!IsWrite(f) || IsCompound(f)) into.Add(new Event(Kind.Read, k, f));
                if (IsWrite(f)) into.Add(new Event(Kind.Write, k, f));
                return;
            case ICompoundAssignmentOperation c when c.Target is IFieldReferenceOperation t && Index(registers, t) is int k:
                into.Add(new Event(Kind.Read, k, t));
                Collect(model, registers, c.Value, into);
                into.Add(new Event(Kind.Write, k, t));
                return;
        }
        foreach (IOperation child in o.ChildOperations) Collect(model, registers, child, into);
        if (model.Waits(o)) into.Add(new Event(Kind.Wait, -1, o));
    }
}
