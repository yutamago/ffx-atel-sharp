// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

// Requests written as calls of the entry's method (docs/plan.md, "Requests between workers"):
//   _ = Lulu.E5Async();                  req0
//   await Lulu.E5Async();                reqe0 (also `.Finished`)
//   await Lulu.E5Async().Started;        reqw0
// with an optional Priority argument (level 1 without it).
internal sealed partial class StatementReader {
    /// <summary>The level of a request without a <c>Priority</c> argument.</summary>
    private const int DefaultPriority = 1;

    /// <summary>For each worker class, its methods by name with the first entry each places.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, int>> EntryMethods { get; init; } = new Dictionary<string, Dictionary<string, int>>();

    /// <summary><c>_ = Lulu.E5Async();</c>: queues the entry (<c>req0</c>).</summary>
    private RequestStmt Discarded(AssignmentExpressionSyntax a) =>
        Strip(a.Right, conversions: false) is InvocationExpressionSyntax call
            ? RequestCall(call, Opcodes.Req0)
            : throw Error(a, "`_ = …` is a request: `_ = Worker.EntryAsync();`");

    /// <summary><c>await Lulu.E5Async();</c> (<c>reqe0</c>) or <c>await Lulu.E5Async().Started;</c> (<c>reqw0</c>).</summary>
    private RequestStmt Awaited(AwaitExpressionSyntax a) =>
        Strip(a.Expression, conversions: false) switch {
            InvocationExpressionSyntax call => RequestCall(call, Opcodes.ReqE0),
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Started", Expression: InvocationExpressionSyntax call } => RequestCall(call, Opcodes.ReqW0),
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Finished", Expression: InvocationExpressionSyntax call } => RequestCall(call, Opcodes.ReqE0),
            _ => throw Error(a, "await takes a request: `await Worker.EntryAsync();`, `.Started` or `.Finished`"),
        };

    private RequestStmt RequestCall(InvocationExpressionSyntax call, byte op) {
        if (call.Expression is not MemberAccessExpressionSyntax { Name: IdentifierNameSyntax method } m) {
            throw Error(call, "a request names the worker and the entry: `Worker.EntryAsync()`");
        }
        string worker = m.Expression switch {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            MemberAccessExpressionSyntax inner => inner.Name.Identifier.ValueText,
            _ => throw Error(m.Expression, "a request names the worker by its class"),
        };
        if (!workers.TryGetValue(worker, out int w)) throw Error(m.Expression, $"no worker {worker} in [Workers]");
        if (!EntryMethods.TryGetValue(worker, out Dictionary<string, int>? methods) || !methods.TryGetValue(method.Identifier.ValueText, out int entry)) {
            throw Error(method, $"{worker} has no entry method {method.Identifier.ValueText}");
        }
        Expr level = new IntLit(DefaultPriority);
        switch (call.ArgumentList.Arguments) {
            case []:
                break;
            case [ArgumentSyntax p] when p.NameColon is null || p.NameColon.Name.Identifier.ValueText == "priority":
                level = ReadExpr(p.Expression);
                break;
            default:
                throw Error(call.ArgumentList, "a request takes only its priority (entries have no parameters yet)");
        }
        return new RequestStmt(op, level, new IntLit(w), new IntLit(entry));
    }

    private static bool IsDiscard(ExpressionSyntax e) => e is IdentifierNameSyntax { Identifier.ValueText: "_" };
}
