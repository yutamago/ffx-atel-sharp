// SPDX-License-Identifier: MIT

using System.Globalization;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

/// <summary>
///     A place in the source that compiles to native <paramref name="Id"/>: a call, a member's read, or the target of a
///     member's assignment; <paramref name="Constant"/> the argument a member picked by a constant passes
///     (<c>ActorProperty.Hp</c>).
/// </summary>
internal sealed record NativeSite(SyntaxNode Node, ushort Id, int? Constant);

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
    ///     The natives an anchor names (<see cref="NativeSite"/>'s keys): the native by its name (<c>Btl.SetActorProperty</c>),
    ///     or the natives of the member named in full (<c>Self.Hp</c>: <c>GetSelfProperty</c> and <c>SetActorProperty</c> with
    ///     <c>ActorProperty.Hp</c>; NativeMembers.Named). A null constant stands for any.
    /// </summary>
    internal static HashSet<(ushort Id, int? Constant)> AnchorNatives(string name) {
        HashSet<(ushort, int?)> keys = [];
        if (NativesByName.Value.TryGetValue(name, out Bytecode.Analysis.NativeSignature? sig)) keys.Add((sig.Id, null));
        foreach (MemberNative m in NativeMembers.Default.Named(name)) keys.Add((m.Id, m.Constant));
        return keys;
    }

    /// <summary>Whether the anchor names a property (<c>Self.Hp</c>, <c>Yaw</c>): its places are uses, not calls.</summary>
    internal static bool AnchorIsProperty(string name) =>
        !NativesByName.Value.ContainsKey(name) && NativeMembers.Default.Named(name).ToList() is { Count: > 0 } all && all.All(m => m.Use != MemberUse.Call);

    /// <summary>
    ///     The places in a method an anchor counts, in the order they stand in the source: the calls named so as written
    ///     (<see cref="IsCallTo"/>), the members written with that name (<c>Self.Hp</c> for <c>"Hp"</c>), and every call, read
    ///     or assignment that compiles to a native the anchor names (<see cref="AnchorNatives"/>), whichever way the source
    ///     writes it: <c>Self.Hp = 1;</c> and <c>Btl.SetActorProperty(ActorProperty.Hp, 1);</c> are both places of
    ///     <c>Self.Hp</c> and of <c>Btl.SetActorProperty</c>.
    /// </summary>
    internal static List<SyntaxNode> AnchorPlaces(string name, MethodDeclarationSyntax method, IReadOnlyList<NativeSite> sites) {
        HashSet<(ushort Id, int? Constant)> keys = AnchorNatives(name);
        bool Named(string? text) => text is not null && (text == name || text.EndsWith("." + name, StringComparison.Ordinal) || name.EndsWith("." + text, StringComparison.Ordinal) && !text.Contains('.'));
        HashSet<SyntaxNode> places = [.. method.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => IsCallTo(c, name))];
        foreach (NativeSite site in sites) {
            if (site.Node.SyntaxTree != method.SyntaxTree || !method.Span.Contains(site.Node.Span)) continue;
            if (keys.Contains((site.Id, site.Constant)) || keys.Contains((site.Id, null)) || site.Node is not InvocationExpressionSyntax && Named(Dotted((ExpressionSyntax)site.Node))) {
                places.Add(site.Node);
            }
        }
        return [.. places.OrderBy(n => n.SpanStart).ThenByDescending(n => n.Span.Length)];
    }

    /// <summary>
    ///     The statement an <c>[Inject]</c> anchors to, in the vanilla method's code: the innermost statement holding the
    ///     <see cref="InjectAnchor.Occurrence"/>-th place of the anchor (<see cref="AnchorPlaces"/>), and its code range
    ///     (offsets from the method's start).
    /// </summary>
    /// <param name="method">The vanilla method's declaration.</param>
    /// <param name="map">The vanilla worker's <see cref="CompiledScript.SourceMap"/>.</param>
    /// <param name="sites">The vanilla script's <see cref="CompiledScript.NativeSites"/>.</param>
    /// <param name="start">The method's code range in the worker.</param>
    internal static InjectPoint ResolveInject(InjectAnchor anchor, MethodDeclarationSyntax method, IReadOnlyList<SourceSpan> map, IReadOnlyList<NativeSite> sites, uint start, uint end) {
        List<SyntaxNode> calls = AnchorPlaces(anchor.Call, method, sites);
        string kind = AnchorIsProperty(anchor.Call) ? "use" : "call";
        string of = kind == "use" ? "of" : "to";
        if (calls.Count < anchor.Occurrence) {
            throw CompileException.At(anchor.Where, calls.Count == 0
                ? $"{anchor.Method} has no {kind} {of} {anchor.Call}"
                : $"{anchor.Method} has {calls.Count} {kind}(s) {of} {anchor.Call}, not {anchor.Occurrence}");
        }
        SyntaxNode call = calls[anchor.Occurrence - 1];
        StatementSyntax statement = call.FirstAncestorOrSelf<StatementSyntax>(s => s is not BlockSyntax)
            ?? throw CompileException.At(anchor.Where, $"the {kind} {of} {anchor.Call} is in no statement");
        if (map.Count == 0) throw CompileException.At(anchor.Where, "the vanilla worker's code has no source map");
        // The statement's instructions: those compiled from it or a statement inside it. Instructions from elsewhere in
        // the method between them would mean the statement's code is not in one piece; those from another file (an
        // inline method's body) belong to the statement that called it.
        bool Inside(SourceSpan s, SyntaxNode node) => s.Where is Location l && l.SourceTree == node.SyntaxTree && node.Span.Contains(l.SourceSpan);
        List<SourceSpan> own = [.. map.Where(s => s.Offset >= start && s.Offset < end && Inside(s, statement))];
        if (own.Count == 0) throw CompileException.At(anchor.Where, $"the statement with that {kind} compiles to no code");
        uint first = own.Min(s => s.Offset), last = own.Max(s => s.Offset + s.Length);
        if (map.FirstOrDefault(s => s.Offset >= first && s.Offset < last && Inside(s, method) && !Inside(s, statement)) is { Where: not null } stray) {
            throw CompileException.At(anchor.Where, $"the code of the statement with that {kind} is not in one piece (line {stray.Where!.GetLineSpan().StartLinePosition.Line + 1} is inside it)");
        }
        int n = anchor.Occurrence;
        string suffix = n % 100 is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        string words = string.Create(CultureInfo.InvariantCulture,
            $"{(anchor.Position == InjectPosition.Before ? "before" : "after")} the statement with the {n}{suffix} {kind} {of} {anchor.Call} in {anchor.Method}");
        return new InjectPoint(anchor.Position, first - start, last - start, words);
    }
}
