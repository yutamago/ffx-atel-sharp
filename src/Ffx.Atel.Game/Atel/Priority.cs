// SPDX-License-Identifier: MIT

namespace Ffx.Atel;

/// <summary>
///     The level a request runs its entry at (the first operand of <c>req</c>, <c>reqw</c>, <c>reqe</c>):
///     <c>_ = Lulu.E5Async(Priority.P3);</c>. A higher level interrupts a lower one. Scripts push levels 1-7
///     only, level 1 in 78 % of requests, so a request without the argument is level 1
///     (docs/plan.md, "Requests between workers"; docs/corpus.md, "Requests").
/// </summary>
public enum Priority {
    /// <summary>Level 0: the level entry 1 (<c>Main</c>) runs at.</summary>
    P0_Main = 0,

    /// <summary>Level 1, the default: most requests between workers.</summary>
    P1 = 1,

    /// <summary>Level 2.</summary>
    P2 = 2,

    /// <summary>Level 3 (14 % of requests).</summary>
    P3 = 3,

    /// <summary>Level 4.</summary>
    P4 = 4,

    /// <summary>Level 5.</summary>
    P5 = 5,

    /// <summary>Level 6.</summary>
    P6 = 6,

    /// <summary>Level 7: the level entry 0 (<c>Init</c>) runs at, and the battle system's.</summary>
    P7_Init = 7,

    /// <summary>Level 8: no shipped script uses it.</summary>
    P8 = 8,
}
