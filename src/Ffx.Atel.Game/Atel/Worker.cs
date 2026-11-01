// SPDX-License-Identifier: MIT

namespace Ffx.Atel;

/// <summary>
///     A worker: a script object with numbered entries. Its base class gives its type (worker
///     header <c>+0x00</c>); the names after the number come with Phase 5 of the plan.
/// </summary>
public abstract class Worker {
    /// <summary>
    ///     Code the decompiler cannot express yet, in the assembly syntax of <c>atel disasm</c>
    ///     (without <c>worker</c> lines; <c>lN:</c> places the worker's jump label N). A worker's
    ///     entry methods are laid out in declaration order, and code that does not end in a jump
    ///     or <c>end</c> runs on into the next.
    /// </summary>
    protected static void Asm(string code) => throw Runtime.NotRunnable();
}

/// <summary>Worker type 0: a function worker, the target of <c>call</c>.</summary>
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
