// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Analyzers.CodeFixes;

/// <summary>The obvious fixes: ATL1009 (float literal), ATL1010 (checked), ATL2001 (request).</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AtelCodeFixes)), Shared]
public sealed class AtelCodeFixes : CodeFixProvider {
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = [
        Rules.Type.Id, Rules.Checked.Id, Rules.RequestNotAwaited.Id,
    ];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;
        foreach (Diagnostic d in context.Diagnostics) {
            SyntaxNode node = root.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true);
            SyntaxToken token = root.FindToken(d.Location.SourceSpan.Start);
            switch (d.Id) {
                case "ATL1009" when node is LiteralExpressionSyntax { Token.Value: double } literal:
                    Register(context, d, root, "Make it a float literal", literal, FloatLiteral(literal));
                    break;
                case "ATL1010" when token.Parent is CheckedExpressionSyntax c && c.IsKind(SyntaxKind.CheckedExpression):
                    Register(context, d, root, "Remove 'checked'", c, SyntaxFactory.ParenthesizedExpression(c.Expression).WithTriviaFrom(c));
                    break;
                case "ATL1010" when token.Parent is CheckedStatementSyntax s && s.IsKind(SyntaxKind.CheckedStatement):
                    Register(context, d, root, "Remove 'checked'", s, s.Block.WithTriviaFrom(s));
                    break;
                case "ATL2001" when node.FirstAncestorOrSelf<InvocationExpressionSyntax>() is InvocationExpressionSyntax call && call.Parent is ExpressionStatementSyntax:
                    Register(context, d, root, "Queue it: '_ ='", call, SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName("_"), call.WithoutLeadingTrivia()).WithTriviaFrom(call));
                    if (call.FirstAncestorOrSelf<MethodDeclarationSyntax>()?.Modifiers.Any(SyntaxKind.AsyncKeyword) == true) {
                        Register(context, d, root, "Wait for it: 'await'", call, SyntaxFactory.AwaitExpression(call.WithoutLeadingTrivia()).WithTriviaFrom(call));
                    }
                    break;
            }
        }
    }

    private static LiteralExpressionSyntax FloatLiteral(LiteralExpressionSyntax literal) {
        string text = literal.Token.Text.TrimEnd('d', 'D');
        float value = (float)(double)literal.Token.Value!;
        return literal.WithToken(SyntaxFactory.Literal(text + "f", value).WithTriviaFrom(literal.Token));
    }

    private static void Register(CodeFixContext context, Diagnostic d, SyntaxNode root, string title, SyntaxNode old, SyntaxNode replacement) =>
        context.RegisterCodeFix(CodeAction.Create(title, (CancellationToken _) => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(old, replacement))), title), d);
}
