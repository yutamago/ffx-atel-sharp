// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;

namespace Ffx.Atel.Decompiler;

// Function workers (type 0, docs/plan.md "Three kinds of code"): a function worker with one method,
// entry 0, writes it as `public static void Run()`, and `call` to it is `W48.Run();`.
internal sealed partial class WorkerDecompiler {
    /// <summary>Whether this is a function worker written as <c>public static void Run()</c>: its code is one method holding only entry 0.</summary>
    public bool IsRunFunction => _functionWorker && _methods is [{ Entries: [0] } m] && !m.Units.Any(u => u.Entries.Count > 0);

    /// <summary>The script's workers written with <c>Run()</c>; a <c>call</c> to one is <c>W48.Run();</c>.</summary>
    public IReadOnlySet<int> RunFunctions {
        get => _printer.RunFunctions;
        set => _printer.RunFunctions = value;
    }
}

internal sealed partial class StatementPrinter {
    /// <summary>Workers whose entry is <c>Run()</c>; other <c>call</c> targets stay <c>Call&lt;W48&gt;();</c>.</summary>
    public IReadOnlySet<int> RunFunctions { get; set; } = new HashSet<int>();

    private string CallWorker(int worker, string name) {
        if (!RunFunctions.Contains(worker)) {
            Count("call-generic");
            return $"Call<{name}>();";
        }
        Count("call-run");
        return $"{name}.Run();";
    }
}
