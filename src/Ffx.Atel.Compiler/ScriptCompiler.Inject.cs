// SPDX-License-Identifier: MIT

using System.Globalization;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

public static partial class ScriptCompiler {
    /// <summary>An <c>[Inject]</c> attribute read: the method, before or after, the call's name and which of them.</summary>
    internal sealed record InjectAnchor(string Method, InjectPosition Position, string Call, int Occurrence, AttributeSyntax Where);

    /// <summary>
    ///     Reads <c>[Inject(nameof(M), Before = nameof(Btl.UseCommand), Occurrence = 2)]</c>. The call is named as written
    ///     in the source: <c>nameof(Btl.UseCommand)</c> keeps its whole dotted name for the compiler, <c>"Btl.UseCommand"</c>
    ///     too.
    /// </summary>
    internal static InjectAnchor ReadInject(AttributeSyntax a) {
        List<AttributeArgumentSyntax> args = [.. Arguments(a)];
        AttributeArgumentSyntax? method = args.FirstOrDefault(x => x.NameEquals is null && x.NameColon is null);
        string target = method is not null && StatementReader.MethodName(method.Expression) is string m ? m
            : throw CompileException.At(a, "[Inject] names a vanilla method: [Inject(nameof(OnTurn), Before = nameof(Btl.UseCommand))]");
        string? before = null, after = null;
        int occurrence = 1;
        foreach (AttributeArgumentSyntax x in args.Where(x => x.NameEquals is not null)) {
            switch (x.NameEquals!.Name.Identifier.ValueText) {
                case "Before": before = CallName(x.Expression) ?? throw CompileException.At(x, "the call by nameof(…) or as a string: Before = nameof(Btl.UseCommand)"); break;
                case "After": after = CallName(x.Expression) ?? throw CompileException.At(x, "the call by nameof(…) or as a string: After = nameof(Btl.UseCommand)"); break;
                case "Occurrence":
                    occurrence = Eval(x.Expression) is int n and >= 1 ? n : throw CompileException.At(x, "Occurrence counts from 1");
                    break;
                default: throw CompileException.At(x, $"[Inject] has no {x.NameEquals.Name.Identifier.ValueText}");
            }
        }
        if ((before is null) == (after is null)) throw CompileException.At(a, "[Inject] runs Before = … or After = … a call, one of them");
        return new InjectAnchor(target, before is null ? InjectPosition.After : InjectPosition.Before, before ?? after!, occurrence, a);
    }

    /// <summary>The dotted name in <c>nameof(Btl.UseCommand)</c> or <c>"Btl.UseCommand"</c>.</summary>
    private static string? CallName(ExpressionSyntax e) => e switch {
        LiteralExpressionSyntax { Token.Value: string s } when s.Length > 0 => s,
        InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" }, ArgumentList.Arguments: [ArgumentSyntax arg] } => Dotted(arg.Expression),
        _ => null,
    };

    /// <summary><c>Btl.UseCommand</c> for <c>Btl.UseCommand</c>, <c>Motion.Play</c>, <c>W02.OpenAsync</c>; generic arguments left out.</summary>
    internal static string? Dotted(ExpressionSyntax e) => e switch {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        MemberAccessExpressionSyntax m => Dotted(m.Expression) is string left ? $"{left}.{m.Name.Identifier.ValueText}" : m.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText,
        ThisExpressionSyntax => "this",
        _ => null,
    };

    /// <summary>Whether a call as written (<c>Ffx.Btl.UseCommand(…)</c>) is the named one (<c>Btl.UseCommand</c> or <c>UseCommand</c>).</summary>
    internal static bool IsCallTo(InvocationExpressionSyntax call, string name) =>
        Dotted(call.Expression) is string text && (text == name || text.EndsWith("." + name, StringComparison.Ordinal) || name.EndsWith("." + text, StringComparison.Ordinal) && !text.Contains('.'));

    /// <summary>
    ///     The statement an <c>[Inject]</c> anchors to, in the vanilla method's code: the innermost statement holding the
    ///     <see cref="InjectAnchor.Occurrence"/>-th call, and its code range (offsets from the method's start).
    /// </summary>
    /// <param name="method">The vanilla method's declaration.</param>
    /// <param name="map">The vanilla worker's <see cref="CompiledScript.SourceMap"/>.</param>
    /// <param name="start">The method's code range in the worker.</param>
    internal static InjectPoint ResolveInject(InjectAnchor anchor, MethodDeclarationSyntax method, IReadOnlyList<SourceSpan> map, uint start, uint end) {
        List<InvocationExpressionSyntax> calls = [.. method.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => IsCallTo(c, anchor.Call))];
        if (calls.Count < anchor.Occurrence) {
            throw CompileException.At(anchor.Where, calls.Count == 0
                ? $"{anchor.Method} has no call to {anchor.Call}"
                : $"{anchor.Method} has {calls.Count} call(s) to {anchor.Call}, not {anchor.Occurrence}");
        }
        InvocationExpressionSyntax call = calls[anchor.Occurrence - 1];
        StatementSyntax statement = call.FirstAncestorOrSelf<StatementSyntax>(s => s is not BlockSyntax)
            ?? throw CompileException.At(anchor.Where, $"the call to {anchor.Call} is in no statement");
        if (map.Count == 0) throw CompileException.At(anchor.Where, "the vanilla worker's code has no source map");
        // The statement's instructions: those compiled from it or a statement inside it. Instructions from elsewhere in
        // the method between them would mean the statement's code is not in one piece; those from another file (an
        // inline method's body) belong to the statement that called it.
        bool Inside(SourceSpan s, SyntaxNode node) => s.Where is Location l && l.SourceTree == node.SyntaxTree && node.Span.Contains(l.SourceSpan);
        List<SourceSpan> own = [.. map.Where(s => s.Offset >= start && s.Offset < end && Inside(s, statement))];
        if (own.Count == 0) throw CompileException.At(anchor.Where, "the statement with that call compiles to no code");
        uint first = own.Min(s => s.Offset), last = own.Max(s => s.Offset + s.Length);
        if (map.FirstOrDefault(s => s.Offset >= first && s.Offset < last && Inside(s, method) && !Inside(s, statement)) is { Where: not null } stray) {
            throw CompileException.At(anchor.Where, $"the code of the statement with that call is not in one piece (line {stray.Where!.GetLineSpan().StartLinePosition.Line + 1} is inside it)");
        }
        int n = anchor.Occurrence;
        string suffix = n % 100 is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        string words = string.Create(CultureInfo.InvariantCulture,
            $"{(anchor.Position == InjectPosition.Before ? "before" : "after")} the statement with the {n}{suffix} call to {anchor.Call} in {anchor.Method}");
        return new InjectPoint(anchor.Position, first - start, last - start, words);
    }
}
