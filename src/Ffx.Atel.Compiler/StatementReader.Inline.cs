// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

// Inline methods (docs/encoding.md, "Inline methods"): a call statement of one is the method's template
// with the arguments substituted, in place of the call.
internal sealed partial class StatementReader {
    /// <summary>The inline methods calls may name; the reference assembly's when not set.</summary>
    public InlineMethods? Inlines { get; init; }

    /// <summary>Reading an inline method's body: its locals that live in a register (<c>[InlineLocal]</c>), by name.</summary>
    public IReadOnlyDictionary<string, int>? RegisterLocals { get; set; }

    /// <summary>The <see cref="RegisterLocals"/> the body declared.</summary>
    public HashSet<string> RegisterLocalsDeclared { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     The inline method a call names: <c>Dialogue.Say(…)</c>, or <c>Say(…)</c> in its own class, the script class
    ///     or <c>Worker</c> (the reference assembly's helpers on every worker, written with its members).
    /// </summary>
    private InlineMacro? InlineOf(InvocationExpressionSyntax call) {
        InlineMethods table = Inlines ?? InlineMethods.Game;
        return call.Expression switch {
            IdentifierNameSyntax id => (Class is null ? null : table.Find(Class, id.Identifier.ValueText))
                ?? (ScriptClass is null ? null : table.Find(ScriptClass, id.Identifier.ValueText))
                ?? table.Find(nameof(Worker), id.Identifier.ValueText),
            // A component's (Turn.To): on this worker's component only, never another worker's (Tidus.Turn).
            MemberAccessExpressionSyntax { Expression: not IdentifierNameSyntax, Name: IdentifierNameSyntax } access when InlineMethods.IsComponent(ClassName(access.Expression)) => null,
            MemberAccessExpressionSyntax { Name: IdentifierNameSyntax m } access => table.Find(ClassName(access.Expression), m.Identifier.ValueText),
            _ => null,
        };
    }

    private static string ClassName(ExpressionSyntax e) => e switch {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        _ => "",
    };

    /// <summary>An inline method's body as its template: each parameter is a <see cref="ParamExpr"/>.</summary>
    public List<Stmt> Template(BlockSyntax body, IEnumerable<string> parameters) {
        PushScope();
        try {
            int k = 0;
            foreach (string name in parameters) _scopes[^1][name] = new ParamExpr(k++);
            return Block(body.Statements);
        }
        finally {
            PopScope();
        }
    }

    /// <summary>
    ///     A call of an inline method: its template with the arguments in place of the parameters (by
    ///     position, by name, or the parameter's default). An argument is substituted where its parameter is
    ///     used, so it must be a value without effects (<see cref="Inline.IsArgument"/>).
    /// </summary>
    private List<Stmt> ExpandInline(InlineMacro macro, InvocationExpressionSyntax call) {
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;
        Expr?[] values = new Expr?[macro.Params.Count];
        bool named = false;
        for (int k = 0; k < args.Count; k++) {
            ArgumentSyntax a = args[k];
            if (!a.RefKindKeyword.IsKind(SyntaxKind.None)) throw Error(a, "an inline method takes values (no ref or out)");
            int p = k;
            if (a.NameColon is not null) {
                string param = a.NameColon.Name.Identifier.ValueText;
                p = macro.Params.ToList().FindIndex(x => x.Name == param);
                if (p < 0) throw Error(a.NameColon, $"{macro.Name} has no parameter {param}");
                named |= p != k;
            }
            else if (named) {
                throw Error(a, "a positional argument after a named one out of place");
            }
            if (p >= values.Length) throw Error(a, $"{macro.Name} takes {macro.Params.Count} arguments");
            if (values[p] is not null) throw Error(a, $"{macro.Name}'s parameter {macro.Params[p].Name} is given twice");
            (List<Stmt> pre, Expr value) = Apart(() => ReadExpr(a.Expression));
            if (pre.Count > 0 || !(Inline.IsArgument(value) || (macro.Params[p].Eager && Inline.IsEagerArgument(value)))) {
                throw Error(a.Expression, macro.Params[p].Eager
                    ? $"an argument of inline method {macro.Name} is evaluated at the start, so it is a value or a native's call (no ++, ?:, && or LastResult)"
                    : $"an argument of inline method {macro.Name} is used where its parameter is, so it is a constant, a variable or an expression of them (no call, ++, ?: or &&)");
            }
            values[p] = value;
        }
        for (int k = 0; k < values.Length; k++) {
            values[k] ??= macro.Params[k].Default ?? throw Error(call.ArgumentList, $"{macro.Name} needs its {macro.Params[k].Name}");
        }
        return Inline.Expand(macro, [.. values.Select(v => v!)]);
    }
}
