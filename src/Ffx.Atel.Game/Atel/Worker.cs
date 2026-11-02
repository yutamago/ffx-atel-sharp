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
    protected int RegA { get => throw Runtime.NotRunnable(); set => throw Runtime.NotRunnable(); }

    /// <summary>Register B (<c>pushb</c>, <c>popb</c>): the selector of a <c>switch</c>.</summary>
    protected int RegB { get => throw Runtime.NotRunnable(); set => throw Runtime.NotRunnable(); }

    /// <summary>
    ///     The float accumulator (<c>pushfacc</c>): the result of the last native called as a statement
    ///     (a choice box's answer, after the call that waits for it).
    /// </summary>
    protected int LastResult => throw Runtime.NotRunnable();

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

    /// <summary>Opcode <c>op_77</c>: pops two values, a worker operation (<c>0x00863570</c>) not yet understood.</summary>
    protected static void Op77(int a, int b) => throw Runtime.NotRunnable();

    /// <summary>Opcode <c>op_78</c>: as <c>op_77</c> (unused by the shipped scripts).</summary>
    protected static void Op78(int a, int b) => throw Runtime.NotRunnable();

    /// <summary>Opcode <c>op_79</c>: pops three values (<c>0x00870d30</c>), the last an address (<see cref="Addr{T}"/>); not yet understood.</summary>
    protected static void Op79(int a, int b, int c) => throw Runtime.NotRunnable();

    /// <summary>Ends the entry with a value (<c>endp</c>).</summary>
    protected static void End(int value) => throw Runtime.NotRunnable();
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
