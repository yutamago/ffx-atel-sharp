// SPDX-License-Identifier: MIT

namespace Ffx.Atel;

/// <summary>
///     A worker: one object of a script, such as a villager, a trigger on the ground or a monster's battle AI.
///     Its methods are its entries, the code the game runs when the worker starts (<see cref="Init"/>), when the
///     player talks to it or when another worker asks. The members on this page are what a worker's code can use
///     without a class name: its position, turn, move, motion, the script's messages and more.
/// </summary>
/// <remarks>
///     The base class gives the worker's type (worker header <c>+0x00</c>): <see cref="Actor"/>,
///     <see cref="LineTrigger"/>, <see cref="BattleWorker"/> and the rest. The members below are what Phase 3 of the plan writes for the machine's own parts: the
///     registers the original kept outside variables, requests, <c>call</c>, and the conversions C#
///     needs where ATEL has none. None of them runs on .NET; each compiles to fixed bytecode
///     (docs/plan.md, "Phase 3: statements").
/// </remarks>
public abstract partial class Worker {
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

    /// <summary>Runs when the worker starts (entry 0): set the worker up here, its model, position and motion.</summary>
    /// <remarks>The engine runs it at priority 7 (docs/plan.md, "Workers and entries").</remarks>
    protected virtual void Init() {
    }

    /// <summary>The worker's main code (entry 1).</summary>
    /// <remarks>It runs at priority 0, the lowest.</remarks>
    protected virtual void Main() {
    }

    /// <summary>The script's messages: <c>Text[12]</c> is message 12, for <see cref="Dialogue.Say"/>.</summary>
    /// <remarks>docs/plan.md, "Text".</remarks>
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

    /// <summary>
    ///     Sets the entry that trigger <paramref name="kind"/> requests of this worker (<c>op_79</c>):
    ///     <c>SetTriggerEntry(Addr(ref V38[0]), 0, 6);</c> makes talking to it (kind 0) run entry 6 in place of
    ///     entry 2. The worker's table of eight entries, one per kind, is kept in the variable
    ///     <paramref name="table"/> points at; the first call fills it with the defaults of the worker's type.
    /// </summary>
    /// <param name="table">The table's variable, <c>Addr(ref V38[0])</c>: eight 16-bit entries.</param>
    /// <param name="kind">The kind of trigger, 0-7 (above 7 is 7): 0 talking to the worker, 3 touching it, 4 crossing its line.</param>
    /// <param name="entry">The entry it requests; <c>0xffff</c> for none.</param>
    /// <remarks>
    ///     <c>op_79</c> (<c>0x00870d30</c>) pops entry, kind and the variable; when the worker's table pointer
    ///     (<c>+0x44</c>) is 0 it copies the eight defaults of its type (<c>0x00876390</c>: types 2/3, 5, 6, else
    ///     <c>2 3 4 5 - - - 1</c>) into the variable; then <c>table[kind] = entry</c> and <c>+0x44</c> points at the
    ///     variable. A trigger of kind <c>k</c> (talk, touch <c>0x00866980</c>, line cross <c>0x008680a0</c>)
    ///     requests entry <c>table[k]</c> (<c>0x008763e0</c>, the defaults without a table) at the level of its
    ///     kind, when the worker has that kind on (<c>+0xab</c> bit <c>k</c>, natives <c>0x0034</c>/<c>0x0035</c>):
    ///     <c>0x008764f0</c>. ffx-rust <c>signal::script_op79</c>, where the table is still called the
    ///     "vibration curve" (<c>TkGetVibrateCurve</c>).
    /// </remarks>
    protected static void SetTriggerEntry(int table, int kind, int entry) => throw Runtime.NotRunnable();

    /// <summary>The older name of <see cref="SetTriggerEntry"/> (<c>op_79</c>).</summary>
    protected static void Op79(int table, int kind, int entry) => throw Runtime.NotRunnable();

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

// The worker types by number (worker header +0x00). The decompiler writes the names below, which
// derive from these; both compile to the same type. What FFX.exe does per type is in
// docs/language.md, "Workers" (Atel_WorkerFrame_008666e0's dispatch on the type byte, the trigger
// tables by type, AtelGetPosWorkAdrs).

/// <summary>Worker type 0: a function worker, the target of <c>call</c>; <c>return;</c> is <c>ret</c>. Also <see cref="FunctionWorker"/>.</summary>
public abstract class Worker0 : Worker;

/// <summary>Worker type 1: an actor. Also <see cref="Actor"/>.</summary>
public abstract class Worker1 : Worker;

/// <summary>Worker type 2: a line trigger in events, and every monster and battle worker. Also <see cref="LineTrigger"/>, <see cref="BattleWorker"/>.</summary>
public abstract class Worker2 : Worker;

/// <summary>Worker type 3: a box trigger. Also <see cref="BoxTrigger"/>.</summary>
public abstract class Worker3 : Worker;

/// <summary>Worker type 4: no position block. Also <see cref="LogicWorker"/>.</summary>
public abstract class Worker4 : Worker;

/// <summary>Worker type 5: a line that watches another worker crossing it. Also <see cref="LineSensor"/>.</summary>
public abstract class Worker5 : Worker;

/// <summary>Worker type 6: a box that watches another worker touching it. Also <see cref="BoxSensor"/>.</summary>
public abstract class Worker6 : Worker;

/// <summary>
///     Worker type 0, a function worker: the target of <c>call</c> (<c>W63.Run()</c>), which runs on the
///     caller's worker; <c>return;</c> is <c>ret</c>. Not counted in the block header's workers at
///     <c>+0x36</c>.
/// </summary>
public abstract class FunctionWorker : Worker0;

/// <summary>
///     Worker type 1, an actor: the type FFX.exe steps each frame as a character (move, turn, its
///     direction and rotation synced to the model it is bound to, then the talk and touch checks against
///     the player, <c>Atel_CheckTouchTalk_00866980</c>), the only type that places a character model
///     (<c>Atel_WorkerPlaceChr_00866800</c>) and binds effects (<c>AtelBindFp</c>). By default talking
///     runs entry 2 (level 7) and touching entry 5 (level 5). Party members, NPCs, and also workers that
///     only drive the camera or the scene.
/// </summary>
public abstract class Actor : Worker1;

/// <summary>
///     Worker type 2 in an event, a line trigger: a line between two points (<c>TriggerSetLine</c>) that
///     FFX.exe checks each frame against the player (<c>Atel_CheckLineTouch_008684b0</c>). By default
///     talking to it runs entry 2, crossing it entry 4, touching it entry 5 (each frame), a touch that
///     begins entry 6 and one that ends entry 7 (<c>SetTriggerEntry</c> changes them).
/// </summary>
public abstract class LineTrigger : Worker2;

/// <summary>
///     Worker type 2 in a monster or battle script, where every worker has that type: the battle system
///     runs it through requests; the trigger requests of <see cref="LineTrigger"/> go to the event's
///     workers (ctrl work 0, <c>AtelRequestTrigger_008764f0</c>).
/// </summary>
public abstract class BattleWorker : Worker2;

/// <summary>
///     Worker type 3, a box trigger: a box around its position (half width, depth and height, turned by
///     its yaw) that FFX.exe checks each frame against the player (<c>Atel_CheckBoxTrigger_00866cc0</c>),
///     with the entries of <see cref="LineTrigger"/>: talk 2, crossing 4, touch 5, touch begins 6, ends 7.
/// </summary>
public abstract class BoxTrigger : Worker3;

/// <summary>
///     Worker type 4: a worker without a position block (<c>AtelGetPosWorkAdrs</c> gives none), which
///     FFX.exe does not step or trigger; it only runs its entries. Directors, the map's screen parts
///     (minimap, lights, pad input).
/// </summary>
public abstract class LogicWorker : Worker4;

/// <summary>
///     Worker type 5, a line sensor: a line that FFX.exe checks each frame against another worker's
///     movement (its target, and up to eight tracked workers, <c>Atel_CheckLineCrosses_00868020</c>),
///     not the player's talk or touch; crossing runs entry 2, at level 1. It has two levels, not nine.
/// </summary>
public abstract class LineSensor : Worker5;

/// <summary>
///     Worker type 6, a box sensor: a box that FFX.exe checks each frame against its target worker, or the
///     player (<c>Atel_CheckBoxTouch_00867c10</c>): a touch that begins runs entry 2, one that ends entry 3,
///     crossing it both, at level 1. It has two levels, not nine.
/// </summary>
public abstract class BoxSensor : Worker6;
