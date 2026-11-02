// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler.Naming;

namespace Ffx.Atel.Decompiler;

/// <summary>How a request is written (docs/plan.md, "Requests between workers").</summary>
internal enum RequestForm {
    /// <summary><c>Request(1, 3, 5);</c>, <c>RequestWaitEnd(…)</c>, …: the form without names.</summary>
    Fallback,

    /// <summary><c>_ = Lulu.E5Async();</c> (<c>req0</c>).</summary>
    Queue,

    /// <summary><c>await Lulu.E5Async();</c> (<c>reqe0</c>).</summary>
    AwaitEnd,

    /// <summary><c>await Lulu.E5Async().Started;</c> (<c>reqw0</c>).</summary>
    AwaitStart,
}

internal sealed partial class StatementPrinter {
    /// <summary>The level a request without a <see cref="Priority"/> argument runs at.</summary>
    public const int DefaultPriority = 1;

    /// <summary>The worker whose statements are printed: a request to it keeps the fallback form.</summary>
    public int Self { get; init; } = -1;

    /// <summary>
    ///     The method a request to (worker, entry) calls, <c>Lulu.E5Async</c>, where that entry is declared
    ///     <c>async Task</c> (<see cref="Naming.RequestNaming"/>); null keeps the fallback form.
    /// </summary>
    public Func<int, int, string?>? RequestTarget { get; set; }

    private static readonly Domain Priorities = GameNames.Enum(typeof(Priority))!;

    /// <summary>
    ///     Whether a request is written as a call of the entry's method: <c>req0</c>, <c>reqw0</c> or
    ///     <c>reqe0</c> with a constant level, worker and entry, to another worker's entry that has an
    ///     <c>async Task</c> method of its own. Requests with a computed operand, to the worker's own
    ///     entries, the variants (<c>req2</c>) and party requests keep <see cref="RequestForm.Fallback"/>.
    /// </summary>
    public RequestForm FormOf(RequestStmt r, out string target, out int level) {
        target = "";
        level = 0;
        RequestForm form = r.Op switch {
            Opcodes.Req0 => RequestForm.Queue,
            Opcodes.ReqW0 => RequestForm.AwaitStart,
            Opcodes.ReqE0 => RequestForm.AwaitEnd,
            _ => RequestForm.Fallback,
        };
        if (form == RequestForm.Fallback || RequestTarget is null) return RequestForm.Fallback;
        if (r is not { Level: IntLit { Value: >= 0 and <= 8 } l, Worker: IntLit w, Entry: IntLit e } || w.Value == Self) return RequestForm.Fallback;
        if (RequestTarget(w.Value, e.Value) is not string t) return RequestForm.Fallback;
        target = t;
        level = l.Value;
        return form;
    }

    /// <summary>Why a request keeps the fallback form, for the statistics.</summary>
    private string FallbackReason(RequestStmt r) => r switch {
        { Op: Opcodes.Op39 } => "party",
        { Op: not (Opcodes.Req0 or Opcodes.ReqW0 or Opcodes.ReqE0) } => "variant",
        { Worker: not IntLit } or { Entry: not IntLit } or { Level: not IntLit } => "computed",
        { Worker: IntLit w } when w.Value == Self => "self",
        _ => "no-method",
    };

    /// <summary>The request as a call of the entry's method, or null for the fallback form.</summary>
    private string? AsyncRequest(RequestStmt r) {
        RequestForm form = FormOf(r, out string target, out int level);
        if (form == RequestForm.Fallback) {
            Count("request-fallback-" + FallbackReason(r));
            return null;
        }
        string call = $"{target}({(level == DefaultPriority ? "" : Priorities.Member(level))})";
        switch (form) {
            case RequestForm.Queue:
                Count("request-queue");
                return $"_ = {call};";
            case RequestForm.AwaitEnd:
                Count("request-await");
                return $"await {call};";
            default:
                Count("request-started");
                return $"await {call}.Started;";
        }
    }
}
