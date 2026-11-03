// SPDX-License-Identifier: MIT

namespace Ffx.Atel;

/// <summary>
///     A worker: a script object with numbered entries. Its base class gives its type (worker
///     header <c>+0x00</c>); the names after the number come with Phase 5 of the plan.
/// </summary>
/// <remarks>
///     The members below are what Phase 3 of the plan writes for the machine's own parts: the
///     registers the original kept outside variables, requests, <c>call</c>, and the conversions C#
///     needs where ATEL has none. None of them runs on .NET; each compiles to fixed bytecode
///     (docs/plan.md, "Phase 3: statements").
/// </remarks>
public abstract class Worker {
    /// <summary>
    ///     Code the decompiler cannot express yet, in the assembly syntax of <c>atel disasm</c>
    ///     (without <c>worker</c> lines; <c>lN:</c> places the worker's jump label N). A worker's
    ///     entry methods are laid out in declaration order, and code that does not end in a jump
    ///     or <c>end</c> runs on into the next.
    /// </summary>
    protected static void Asm(string code) => throw Runtime.NotRunnable();

    /// <summary>Register A (<c>pusha</c>, <c>popa</c>); the conditional jumps pop their condition into it.</summary>
    protected static int RegA { get => throw Runtime.NotRunnable(); set => throw Runtime.NotRunnable(); }

    /// <summary>Register B (<c>pushb</c>, <c>popb</c>): the selector of a <c>switch</c>.</summary>
    protected static int RegB { get => throw Runtime.NotRunnable(); set => throw Runtime.NotRunnable(); }

    /// <summary>
    ///     The float accumulator (<c>pushfacc</c>): the result of the last native called as a statement
    ///     (a choice box's answer, after the call that waits for it).
    /// </summary>
    protected static int LastResult => throw Runtime.NotRunnable();

    /// <summary>Entry 0, which the engine runs when the worker starts (at priority 7, docs/plan.md "Workers and entries").</summary>
    protected virtual void Init() {
    }

    /// <summary>Entry 1, the worker's main entry (priority 0).</summary>
    protected virtual void Main() {
    }

    /// <summary>The script's message table: <c>Text[12]</c> is message 12 (docs/plan.md, "Text").</summary>
    protected static TextTable Text => throw Runtime.NotRunnable();

    /// <summary>
    ///     An <c>int</c> used as a condition: no code, the machine tests it against 0 (C# needs a
    ///     <c>bool</c> where the original language took any value).
    /// </summary>
    protected static bool AsBool(int value) => throw Runtime.NotRunnable();

    /// <summary>A <c>float</c> used as a condition: no code.</summary>
    protected static bool AsBool(float value) => throw Runtime.NotRunnable();

    /// <summary>A condition used as a value, 1 or 0: no code (comparisons push 0 or 1).</summary>
    protected static int AsInt(bool value) => throw Runtime.NotRunnable();

    /// <summary>
    ///     An element of a 2-D array by its flat index (<c>row * columns + col</c>), for code that indexes
    ///     it as one row: <c>At(V3, i)</c>.
    /// </summary>
    protected static ref T At<T>(T[,] array, int index) => throw Runtime.NotRunnable();

    /// <summary>The address of a variable's element, for natives that take one (<c>pushvref</c>).</summary>
    protected static int Addr<T>(ref T element) => throw Runtime.NotRunnable();

    /// <summary>
    ///     Queues entry <paramref name="entry"/> of worker <paramref name="worker"/> at priority
    ///     <paramref name="level"/> and carries on (<c>req0</c>; <paramref name="variant"/> 1-5 for
    ///     <c>req1</c>…<c>req5</c>).
    /// </summary>
    /// <remarks>
    ///     The fallback form: a request to an entry declared <c>async Task</c> by constants is written
    ///     <c>_ = Lulu.E5Async();</c> (<see cref="Priority"/>; docs/plan.md, "Requests between workers"). This
    ///     form stays for a computed worker or entry, a request to the worker's own entries, to an entry
    ///     that has no method of its own, and the variants.
    /// </remarks>
    protected static void Request(int level, int worker, int entry, int variant = 0) => throw Runtime.NotRunnable();

    /// <summary>Requests an entry and waits until it has begun (<c>reqw0</c>; <paramref name="variant"/> for <c>reqw1</c>…).</summary>
    protected static void RequestWaitStart(int level, int worker, int entry, int variant = 0) => throw Runtime.NotRunnable();

    /// <summary>Requests an entry and waits until it has ended (<c>reqe0</c>; <paramref name="variant"/> for <c>reqe1</c>…).</summary>
    protected static void RequestWaitEnd(int level, int worker, int entry, int variant = 0) => throw Runtime.NotRunnable();

    /// <summary>Runs function worker <typeparamref name="T"/> on this worker, with this worker's registers (<c>call</c>).</summary>
    protected static void Call<T>() where T : Worker => throw Runtime.NotRunnable();

    /// <summary>Halts the worker (<c>op_54</c>).</summary>
    protected static void Halt() => throw Runtime.NotRunnable();

    /// <summary>A breakpoint for the original debugger (<c>op_40</c>).</summary>
    protected static void Breakpoint() => throw Runtime.NotRunnable();

    /// <summary>
    ///     Waits until worker <typeparamref name="T"/> has no request for <paramref name="entry"/> queued or
    ///     running (<c>op_77</c>): <c>_ = Lulu.E5Async(); WaitForEntry&lt;Lulu&gt;(5);</c> starts Lulu's entry 5 and
    ///     waits until it has ended. Unlike <c>await Lulu.E5Async()</c> it makes no request of its own, so it
    ///     also waits for requests other workers made. Goes on at once when there is none.
    /// </summary>
    /// <remarks>
    ///     <c>op_77</c> pops the entry and the worker; while <c>AtelIsSignalQueued(worker, entry)</c>
    ///     (<c>0x00863570</c>) finds a request (not a reply) for the entry in the worker's queue, the caller's
    ///     level waits (state 2, rechecked each frame). A running request stays in the queue until its entry
    ///     ends (<c>AtelExecReturn</c>, <c>0x008691d0</c>, frees it). ffx-rust
    ///     <c>crates/ffx-script/src/vm/bridge/run.rs</c>.
    /// </remarks>
    protected static void WaitForEntry<T>(int entry) where T : Worker => throw Runtime.NotRunnable();

    /// <summary>
    ///     <see cref="WaitForEntry{T}(int)"/> with the worker by number (<c>op_77</c>), for a computed worker.
    /// </summary>
    protected static void WaitForEntry(int worker, int entry) => throw Runtime.NotRunnable();

    /// <summary>
    ///     As <see cref="WaitForEntry(int, int)"/> for the worker of party slot <paramref name="slot"/> (0-2,
    ///     <c>AtelGetPartyWorker</c>, <c>0x0086a5e0</c>) (<c>op_78</c>); goes on at once for an empty slot.
    ///     Unused by the shipped scripts.
    /// </summary>
    protected static void WaitForPartyEntry(int slot, int entry) => throw Runtime.NotRunnable();

    /// <summary>The older name of <see cref="WaitForEntry(int, int)"/> (<c>op_77</c>).</summary>
    protected static void Op77(int worker, int entry) => throw Runtime.NotRunnable();

    /// <summary>The older name of <see cref="WaitForPartyEntry(int, int)"/> (<c>op_78</c>).</summary>
    protected static void Op78(int slot, int entry) => throw Runtime.NotRunnable();

    /// <summary>Opcode <c>op_79</c>: pops three values (<c>0x00870d30</c>), the last an address (<see cref="Addr{T}"/>); not yet understood.</summary>
    protected static void Op79(int a, int b, int c) => throw Runtime.NotRunnable();

    /// <summary>
    ///     Ends the entry with a value (<c>endp</c>). With <paramref name="reset"/> (<c>endp_1</c>) the
    ///     machine also resets the worker's level, which then starts again at entry 1's code.
    /// </summary>
    protected static void End(int value, bool reset = false) => throw Runtime.NotRunnable();

    /// <summary>
    ///     Requests entry <paramref name="entry"/> of the worker of party member <paramref name="slot"/> at
    ///     priority <paramref name="level"/> and carries on (<c>op_39</c>); nothing when the slot is empty
    ///     or the entry 0.
    /// </summary>
    protected static void RequestParty(int level, int slot, int entry) => throw Runtime.NotRunnable();

    /// <summary>
    ///     Stores a value clamped to the variable's range (store mode 1, <c>popv_1</c>, <c>popva_1</c>):
    ///     <c>StoreClamped(ref Count, Count + 1)</c> stops at the type's maximum instead of wrapping.
    /// </summary>
    protected static void StoreClamped<T>(ref T variable, int value) => throw Runtime.NotRunnable();

    /// <summary>As <see cref="StoreClamped{T}(ref T, int)"/>, for a float variable.</summary>
    protected static void StoreClamped(ref float variable, float value) => throw Runtime.NotRunnable();

    /// <summary>
    ///     Constant <paramref name="value"/> by its pool index (<c>pushc</c>): for a pinned pool that holds the
    ///     value twice, where the code uses the later copy (a literal takes the first).
    /// </summary>
    protected static int Pooled(int index, int value) => throw Runtime.NotRunnable();

    /// <summary>As <see cref="Pooled(int, int)"/>, for the float pool (<c>pushfc</c>).</summary>
    protected static float Pooled(int index, float value) => throw Runtime.NotRunnable();

    /// <summary>No operation (<c>nop_76</c>), with an operand the machine ignores: a marker the original compiler left.</summary>
    protected static void Nop(int operand) => throw Runtime.NotRunnable();

    /// <summary>
    ///     Entry point <paramref name="number"/> starts here: no code. For a method that holds the code of
    ///     several entries because one jumps into another's (C# has no <c>goto</c> between methods).
    /// </summary>
    protected static void Entry(int number) => throw Runtime.NotRunnable();
}

/// <summary>Worker type 0: a function worker, the target of <c>call</c>; <c>return;</c> is <c>ret</c>.</summary>
public abstract class Worker0 : Worker;

/// <summary>Worker type 1.</summary>
public abstract class Worker1 : Worker;

/// <summary>Worker type 2 (every monster worker).</summary>
public abstract class Worker2 : Worker;

/// <summary>Worker type 3.</summary>
public abstract class Worker3 : Worker;

/// <summary>Worker type 4: no position block.</summary>
public abstract class Worker4 : Worker;

/// <summary>Worker type 5: position block and walk-line table (characters).</summary>
public abstract class Worker5 : Worker;

/// <summary>Worker type 6: as type 5.</summary>
public abstract class Worker6 : Worker;
