// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Compiler;

/// <summary>
///     A local of new code: a C# local, a parameter or result of a helper or an entry, or a temporary the
///     compiler needs (<c>?:</c>, <c>x++</c> as a value, an operand evaluated before a branch). The code
///     refers to it through <see cref="LocalExpr"/>; <see cref="SlotAllocator"/> gives it a
///     <see cref="Home"/>: a register where one is free and the value never crosses a wait, else a hidden
///     variable (docs/plan.md, "Variables": locals survive waits).
/// </summary>
internal sealed class Slot(string name, bool isFloat) {
    public string Name { get; } = name;

    /// <summary>A <c>float</c> (float registers, <c>f32</c>); else an integer.</summary>
    public bool Float { get; } = isFloat;

    /// <summary>A <c>bool</c> local: a condition when tested (<c>land</c>/<c>lor</c>, not <c>and</c>/<c>or</c>).</summary>
    public bool Bool { get; init; }

    /// <summary>A temporary the compiler made: written once, before the code that reads it.</summary>
    public bool Temp { get; init; }

    /// <summary>Where it lives: a <see cref="RegExpr"/> or a <see cref="VarExpr"/>; null until placed.</summary>
    public Expr? Home { get; set; }

    public override string ToString() => Name;
}

/// <summary>A set of the integer registers (bits 0-3) and float registers (bits 4-13).</summary>
internal readonly record struct RegisterSet(int Bits) {
    public static RegisterSet None => new(0);

    public static RegisterSet All => new((1 << 14) - 1);

    private static int Bit(RegKind kind, int number) => kind == RegKind.Int ? 1 << number : 1 << (4 + number);

    public bool Contains(RegKind kind, int number) => (Bits & Bit(kind, number)) != 0;

    public RegisterSet With(RegKind kind, int number) => kind is RegKind.Int or RegKind.Float ? new(Bits | Bit(kind, number)) : this;

    public RegisterSet With(RegExpr r) => With(r.Kind, r.Number);

    public static RegisterSet operator |(RegisterSet a, RegisterSet b) => new(a.Bits | b.Bits);

    public static RegisterSet Of(IEnumerable<RegExpr> registers) => registers.Aggregate(None, (s, r) => s.With(r));

    /// <summary>The register an instruction pushes or pops (<c>pushi0</c>…, <c>popf9</c>), if any.</summary>
    public static RegExpr? Of(byte code) => code switch {
        >= Opcodes.PushI0 and <= Opcodes.PushI0 + 3 => new RegExpr(RegKind.Int, code - Opcodes.PushI0),
        >= Opcodes.PopI0 and <= Opcodes.PopI0 + 3 => new RegExpr(RegKind.Int, code - Opcodes.PopI0),
        >= Opcodes.PushF0 and <= Opcodes.PushF0 + 9 => new RegExpr(RegKind.Float, code - Opcodes.PushF0),
        >= Opcodes.PopF0 and <= Opcodes.PopF0 + 9 => new RegExpr(RegKind.Float, code - Opcodes.PopF0),
        _ => null,
    };
}

/// <summary>What <see cref="SlotAllocator.Place"/> needs to know about the code around.</summary>
internal sealed class SlotContext {
    /// <summary>Registers that are not free: the worker's own (its <c>[Register]</c> fields), those of the functions and helpers it calls, and for a helper its callers'.</summary>
    public required RegisterSet Forbidden { get; init; }

    /// <summary>Whether a native waits (polls) before it returns.</summary>
    public required Func<ushort, bool> NativeWaits { get; init; }

    /// <summary>Whether a <c>call</c> of the worker may wait.</summary>
    public required Func<int, bool> CallWaits { get; init; }

    /// <summary>Makes a hidden variable for a slot and returns its index in the variable table.</summary>
    public required Func<Slot, int> Hide { get; init; }

    /// <summary>Slots the code starts with (a helper's parameters): live at its start, each in its own place, placed even when unused.</summary>
    public IReadOnlyList<Slot> Parameters { get; init; } = [];
}

/// <summary>
///     Places the slots of a worker's or a fragment's code and rewrites their placeholder instructions:
///     liveness over the code's jumps; a slot live across a wait (a native that polls, a request, a
///     <c>call</c> of something that may wait, the opcodes not understood) goes to a hidden variable, any
///     other to a register no other code can be using at that point (not in
///     <see cref="SlotContext.Forbidden"/>, not given to a slot live at the same time), else also to a
///     hidden variable. Between waits no other code of the worker runs, so a register that no code of the
///     worker names holds the value safely.
/// </summary>
internal static class SlotAllocator {
    /// <summary>The result: the registers the code uses (its own and those given to slots), and whether it may wait.</summary>
    public readonly record struct Placed(RegisterSet Registers, bool Waits);

    public static Placed Place(List<FlowItem> items, SlotContext ctx) {
        // The slots this code places: those without a home (a helper's or an entry's parameters are
        // placed with their own code, before the callers').
        List<Slot> slots = [];
        Dictionary<Slot, int> index = new(ReferenceEqualityComparer.Instance);
        void Add(Slot s) {
            if (s.Home is null && !index.ContainsKey(s)) {
                index[s] = slots.Count;
                slots.Add(s);
            }
        }
        foreach (Slot p in ctx.Parameters) Add(p);
        RegisterSet named = RegisterSet.None;
        bool waits = false;
        foreach (FlowItem it in items) {
            if (it.Kind != FlowItemKind.Instruction) continue;
            if (it.Op.Symbol is Slot s) Add(s);
            if (RegisterSet.Of(it.Op.Code) is RegExpr r) named = named.With(r);
            waits |= Waits(it, ctx);
        }
        if (slots.Count == 0) {
            Rewrite(items);
            return new Placed(named, waits);
        }

        int n = items.Count, k = slots.Count, words = (k + 63) / 64;
        // Successors: the next item, a jump's target, nothing after an end.
        Dictionary<FlowLabel, int> at = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < n; i++) {
            if (items[i].Kind == FlowItemKind.Label) at[items[i].Target!] = i;
        }
        List<int>[] succ = new List<int>[n];
        for (int i = 0; i < n; i++) {
            List<int> s = [];
            FlowItem it = items[i];
            byte code = it.Kind == FlowItemKind.Instruction ? it.Op.Code : Opcodes.Nop;
            bool falls = code is not (Opcodes.Jmp or Opcodes.End or Opcodes.EndP or Opcodes.End1 or Opcodes.EndP1 or Opcodes.Ret);
            if (falls && i + 1 < n) s.Add(i + 1);
            if (it.Kind == FlowItemKind.Instruction && it.Target is FlowLabel t && at.TryGetValue(t, out int target)) s.Add(target);
            succ[i] = s;
        }
        ulong[][] use = new ulong[n][], def = new ulong[n][], liveIn = new ulong[n][], liveOut = new ulong[n][];
        for (int i = 0; i < n; i++) {
            (use[i], def[i], liveIn[i], liveOut[i]) = (new ulong[words], new ulong[words], new ulong[words], new ulong[words]);
            if (items[i] is { Kind: FlowItemKind.Instruction, Op.Symbol: Slot s } it && index.TryGetValue(s, out int b)) {
                (it.Op.Code == Opcodes.PushV ? use[i] : def[i])[b / 64] |= 1UL << (b % 64);
            }
        }
        for (bool changed = true; changed;) {
            changed = false;
            for (int i = n - 1; i >= 0; i--) {
                for (int w = 0; w < words; w++) {
                    ulong o = 0;
                    foreach (int j in succ[i]) o |= liveIn[j][w];
                    ulong x = use[i][w] | (o & ~def[i][w]);
                    if (o != liveOut[i][w] || x != liveIn[i][w]) {
                        (liveOut[i][w], liveIn[i][w]) = (o, x);
                        changed = true;
                    }
                }
            }
        }

        // Live across a wait: hidden storage. Live at the same time: not the same register.
        bool[] crosses = new bool[k];
        bool[,] interferes = new bool[k, k];
        void Together(ulong[] set) {
            List<int> live = [];
            for (int b = 0; b < k; b++) {
                if ((set[b / 64] >> (b % 64) & 1) != 0) live.Add(b);
            }
            foreach (int a in live) {
                foreach (int b in live) interferes[a, b] = true;
            }
        }
        for (int i = 0; i < n; i++) {
            ulong[] together = new ulong[words];
            for (int w = 0; w < words; w++) together[w] = liveOut[i][w] | def[i][w];
            Together(together);
            if (i == 0) Together(liveIn[0]);
            if (!Waits(items[i], ctx)) continue;
            for (int b = 0; b < k; b++) {
                if ((liveOut[i][b / 64] >> (b % 64) & 1) != 0) crosses[b] = true;
            }
        }
        // A helper's parameters are written by the caller one after another: each its own place.
        foreach (Slot a in ctx.Parameters) {
            foreach (Slot b in ctx.Parameters) interferes[index[a], index[b]] = true;
        }

        RegisterSet given = RegisterSet.None;
        RegisterSet forbidden = ctx.Forbidden | named;
        for (int b = 0; b < k; b++) {
            Slot s = slots[b];
            if (!crosses[b]) {
                RegKind kind = s.Float ? RegKind.Float : RegKind.Int;
                // From the highest down: the original kept arguments and loop counters in the low ones.
                for (int r = (s.Float ? 9 : 3); r >= 0 && s.Home is null; r--) {
                    if (forbidden.Contains(kind, r)) continue;
                    bool taken = false;
                    for (int o = 0; o < b && !taken; o++) {
                        taken = interferes[b, o] && slots[o].Home is RegExpr { } h && h.Kind == kind && h.Number == r;
                    }
                    if (!taken) {
                        s.Home = new RegExpr(kind, r);
                        given = given.With(kind, r);
                    }
                }
            }
            s.Home ??= new VarExpr(ctx.Hide(s));
        }
        Rewrite(items);
        return new Placed(named | given, waits);
    }

    /// <summary>Whether an instruction may let other code of the worker run before the next one.</summary>
    private static bool Waits(FlowItem it, SlotContext ctx) => it.Kind == FlowItemKind.Instruction && it.Op.Code switch {
        Opcodes.Func or Opcodes.FuncFacc => it.Op.Operand is ushort id && ctx.NativeWaits(id),
        Opcodes.Call => it.Op.Operand is not ushort w || ctx.CallWaits(w),
        Opcodes.Req0 or Opcodes.ReqW0 or Opcodes.ReqE0 or Opcodes.Op39 => true,
        >= Opcodes.Req1 and < Opcodes.Op54 => true,
        Opcodes.Op54 or Opcodes.Op77 or Opcodes.Op78 or Opcodes.Op79 => true,
        _ => false,
    };

    /// <summary>Rewrites the placeholders (<c>pushv</c>/<c>popv</c> of a slot) to their homes.</summary>
    private static void Rewrite(List<FlowItem> items) {
        for (int i = 0; i < items.Count; i++) {
            FlowItem it = items[i];
            if (it.Kind != FlowItemKind.Instruction || it.Op.Symbol is not Slot s) continue;
            bool push = it.Op.Code == Opcodes.PushV;
            CodeOp op = s.Home switch {
                RegExpr r => CodeOp.Of(push ? Lowering.Push(r) : Lowering.Pop(r)),
                VarExpr v => CodeOp.Of(push ? Opcodes.PushV : Opcodes.PopV, v.Var),
                _ => throw new InvalidOperationException($"local {s.Name} has no place"),
            };
            items[i] = it with { Op = op };
        }
    }
}
