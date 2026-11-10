// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Microsoft.CodeAnalysis;
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

    /// <summary>What <c>Debug.*</c> calls compile to, and the texts they use (docs/formats/debug.md).</summary>
    public DebugCalls DebugCalls { get; init; } = new(DebugMode.Natives);

    /// <summary>
    ///     A mod's requests (<c>atel compile-mod</c>): the worker and entry numbers of a worker class and
    ///     method, which may be numbers only the linker gives; null where <see cref="EntryMethods"/> answers.
    /// </summary>
    public Func<string, string, (Expr Worker, Expr Entry)?>? RequestTargets { get; init; }

    /// <summary>An entry's parameters and result (<c>async Task&lt;int&gt; FooAsync(int x)</c>) by worker class and method; null for one without.</summary>
    public Func<string, string, EntrySignature?>? Signatures { get; init; }

    /// <summary>
    ///     A mod's new messages (<c>Text.New</c>): the number the linker gives a text (a
    ///     <see cref="SymbolExpr"/>); null outside a mod, where <c>Text.New</c> is an error.
    /// </summary>
    public Func<string, Expr>? NewTexts { get; init; }

    /// <summary><c>Text.New("Nice throw!")</c>: the new message's index, which the linker gives.</summary>
    private Expr NewText(InvocationExpressionSyntax call) {
        if (call.ArgumentList.Arguments is not [ArgumentSyntax { NameColon: null } arg] || arg.Expression is not LiteralExpressionSyntax { Token.Value: string text } literal
            || !literal.IsKind(SyntaxKind.StringLiteralExpression)) {
            throw Error(call, "Text.New takes one string literal: Text.New(\"Nice throw!\")");
        }
        if (text.Length == 0) throw Error(arg, "a new message has text");
        return NewTexts?.Invoke(text) ?? throw Error(call, "Text.New is for mods: the linker gives the index; in a script compiled whole, add the message to the tables and use Text[n]");
    }

    /// <summary><c>_ = Lulu.E5Async();</c>: queues the entry (<c>req0</c>).</summary>
    private RequestStmt Discarded(AssignmentExpressionSyntax a) =>
        Strip(a.Right, conversions: false) is InvocationExpressionSyntax call
            ? RequestCall(call, Opcodes.Req0).Request
            : throw Error(a, "`_ = …` is a request: `_ = Worker.EntryAsync();`");

    /// <summary><c>await Lulu.E5Async();</c> (<c>reqe0</c>) or <c>await Lulu.E5Async().Started;</c> (<c>reqw0</c>).</summary>
    private RequestStmt Awaited(AwaitExpressionSyntax a) => AwaitedRequest(a).Request;

    private (RequestStmt Request, EntrySignature? Signature) AwaitedRequest(AwaitExpressionSyntax a) =>
        Strip(a.Expression, conversions: false) switch {
            InvocationExpressionSyntax call => RequestCall(call, Opcodes.ReqE0),
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Started", Expression: InvocationExpressionSyntax call } => RequestCall(call, Opcodes.ReqW0),
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Finished", Expression: InvocationExpressionSyntax call } => RequestCall(call, Opcodes.ReqE0),
            _ => throw Error(a, "await takes a request: `await Worker.EntryAsync();`, `.Started` or `.Finished`"),
        };

    /// <summary>
    ///     <c>await Lulu.CountAsync(3)</c> as a value: the request waits until the entry has ended, then its
    ///     result (a hidden field of the entry) is copied into a temporary.
    /// </summary>
    private Expr AwaitValue(AwaitExpressionSyntax a) {
        (RequestStmt request, EntrySignature? signature) = AwaitedRequest(a);
        if (request.Op != Opcodes.ReqE0 || signature?.Result is not Slot result) throw Error(a, "only a request to an entry that returns a value (async Task<T>), awaited until it has finished, has a value");
        _pre.Add(request);
        Slot t = Temp(result.Float);
        _pre.Add(Set(t, new LocalExpr(result)));
        return new LocalExpr(t);
    }

    private (RequestStmt Request, EntrySignature? Signature) RequestCall(InvocationExpressionSyntax call, byte op) {
        if (call.Expression is not MemberAccessExpressionSyntax { Name: IdentifierNameSyntax method } m) {
            throw Error(call, "a request names the worker and the entry: `Worker.EntryAsync()`");
        }
        string worker = m.Expression switch {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            MemberAccessExpressionSyntax inner => inner.Name.Identifier.ValueText,
            _ => throw Error(m.Expression, "a request names the worker by its class"),
        };
        Expr workerNumber, entryNumber;
        if (RequestTargets?.Invoke(worker, method.Identifier.ValueText) is (Expr tw, Expr te)) {
            (workerNumber, entryNumber) = (tw, te);
        }
        else {
            if (!workers.TryGetValue(worker, out int w)) throw NoWorker(m.Expression, worker);
            if (!EntryMethods.TryGetValue(worker, out Dictionary<string, int>? methods) || !methods.TryGetValue(method.Identifier.ValueText, out int entry)) {
                throw Error(method, $"{worker} has no entry method {method.Identifier.ValueText}");
            }
            (workerNumber, entryNumber) = (new IntLit(w), new IntLit(entry));
        }
        Expr level = new IntLit(DefaultPriority);
        EntrySignature? signature = Signatures?.Invoke(worker, method.Identifier.ValueText);
        if (signature is null) {
            switch (call.ArgumentList.Arguments) {
                case []:
                    break;
                case [ArgumentSyntax p] when p.NameColon is null || p.NameColon.Name.Identifier.ValueText == "priority":
                    level = ReadExpr(p.Expression);
                    break;
                default:
                    throw Error(call.ArgumentList, "a request to an entry without parameters takes only its priority");
            }
            return (new RequestStmt(op, level, workerNumber, entryNumber), null);
        }
        // The arguments into the entry's hidden fields (in the order written), the priority as the level.
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;
        int[] position = new int[args.Count];
        for (int k = 0; k < args.Count; k++) {
            ArgumentSyntax a = args[k];
            position[k] = a.NameColon is null ? k : Array.IndexOf(signature.Names, a.NameColon.Name.Identifier.ValueText);
            if (position[k] < 0 || position[k] >= signature.Names.Length) throw Error(a, $"{method.Identifier.ValueText} has no such parameter");
        }
        for (int p = 0; p < signature.Names.Length; p++) {
            if (signature.Slots[p] is not null && !position.Contains(p)) throw Error(call.ArgumentList, $"{method.Identifier.ValueText} needs {signature.Names[p]}");
        }
        List<Expr> values = ReadArgs(args.Select(a => a.Expression));
        for (int k = 0; k < values.Count; k++) {
            if (signature.Slots[position[k]] is Slot field) _pre.Add(Set(field, values[k]));
            else level = values[k];
        }
        return (new RequestStmt(op, level, workerNumber, entryNumber), signature);
    }

    private static bool IsDiscard(ExpressionSyntax e) => e is IdentifierNameSyntax { Identifier.ValueText: "_" };
}

/// <summary>
///     An entry's parameters and result (docs/plan.md, "Parameters and return values"): hidden fields of the
///     receiving worker, which the requester writes before the request and reads after <c>.Finished</c>.
///     <see cref="Slots"/> follows the declared parameters; null for the <c>Priority</c> one (the level).
/// </summary>
internal sealed class EntrySignature(string[] names, Slot?[] slots, Slot? result) {
    public string[] Names { get; } = names;
    public Slot?[] Slots { get; } = slots;
    public Slot? Result { get; } = result;

    /// <summary>The parameters in scope in the entry's body.</summary>
    public IEnumerable<(string Name, Slot Slot)> Parameters => Names.Zip(Slots).Where(x => x.Second is not null).Select(x => (x.First, x.Second!));
}
