// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

/// <summary>What the compiler does with <c>Debug.*</c> calls (docs/formats/debug.md).</summary>
public enum DebugMode {
    /// <summary>Compiles them to the loader's debug natives (group <c>F</c>); the script then needs the loader.</summary>
    Natives,
    /// <summary>Leaves them out, their arguments included, with a warning for each.</summary>
    Drop,
    /// <summary>Refuses them: each is an error.</summary>
    Error,
}

/// <summary>Options of a compilation.</summary>
public sealed record CompileOptions {
    public static CompileOptions Default { get; } = new();

    /// <summary>What <c>Debug.*</c> calls compile to.</summary>
    public DebugMode Debug { get; init; } = DebugMode.Natives;

    /// <summary>Parses <c>natives</c>, <c>drop</c> or <c>error</c> (also <c>true</c>/<c>false</c>, as an MSBuild property gives them).</summary>
    public static DebugMode? ParseDebug(string value) => value.Trim().ToLowerInvariant() switch {
        "natives" or "true" or "on" => DebugMode.Natives,
        "drop" or "false" or "off" => DebugMode.Drop,
        "error" or "refuse" => DebugMode.Error,
        _ => null,
    };
}

/// <summary>
///     Reads <c>Debug.Print(…)</c> and <c>Debug.Break()</c> for one compilation and collects their texts and
///     the calls it left out.
/// </summary>
internal sealed class DebugCalls(DebugMode mode) {
    private readonly List<string> _strings = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public DebugMode Mode => mode;

    /// <summary>The texts of the calls compiled, in first-use order.</summary>
    public IReadOnlyList<string> Strings => _strings;

    /// <summary>Warnings: the calls left out (<see cref="DebugMode.Drop"/>).</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Whether the call is <c>Debug.Print</c>/<c>Debug.Break</c> (also <c>Ffx.Debug.…</c>); the method's name, or null.</summary>
    public static string? MethodOf(InvocationExpressionSyntax call) => call.Expression switch {
        MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Debug" } } m => m.Name.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Debug", Expression: IdentifierNameSyntax { Identifier.ValueText: "Ffx" } } } m
            => m.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    ///     The statement a <c>Debug</c> call compiles to, or null when it is left out.
    ///     <paramref name="readExpr"/> reads a hole's expression.
    /// </summary>
    /// <exception cref="CompileException">The call is refused, or not a form Atel compiles.</exception>
    public Stmt? Read(InvocationExpressionSyntax call, string method, Func<ExpressionSyntax, Expr> readExpr) {
        switch (mode) {
            case DebugMode.Error:
                throw CompileException.At(call, $"Debug.{method}: debug natives are off for this build (--debug error); they need the Atel loader");
            case DebugMode.Drop:
                // Checked as if compiled, so a broken call is an error in every mode.
                _ = Build(call, method, readExpr);
                FileLinePositionSpan at = call.GetLocation().GetLineSpan();
                Warnings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{at.Path}({at.StartLinePosition.Line + 1},{at.StartLinePosition.Character + 1}): Debug.{method} left out (--debug drop)"));
                return null;
            default: {
                (Stmt s, string? text) = Build(call, method, readExpr);
                if (text is not null && _seen.Add(text)) _strings.Add(text);
                return s;
            }
        }
    }

    private static (Stmt Stmt, string? Text) Build(InvocationExpressionSyntax call, string method, Func<ExpressionSyntax, Expr> readExpr) {
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;
        switch (method) {
            case "Break" when args.Count == 0:
                return (new CallStmt(DebugText.Break, []), null);
            case "Print" when args.Count == 1 && args[0].NameColon is null && args[0].RefKindKeyword.IsKind(SyntaxKind.None): {
                StringBuilder format = new();
                List<Expr> values = [];
                Text(args[0].Expression, format, values, readExpr);
                if (values.Count > DebugText.MaxValues) throw CompileException.At(args[0], $"Debug.Print takes at most {DebugText.MaxValues} values");
                string text = format.ToString();
                return (new CallStmt(DebugText.Print, [.. values, new IntLit(values.Count), new IntLit(DebugText.Id(text))]), text);
            }
            default:
                throw CompileException.At(call, "Debug.Print(text) with a string literal or an interpolated string, or Debug.Break()");
        }
    }

    /// <summary>A string literal, an interpolated string, or a <c>+</c> of those, as a composite format and its values.</summary>
    private static void Text(ExpressionSyntax e, StringBuilder format, List<Expr> values, Func<ExpressionSyntax, Expr> readExpr) {
        switch (e) {
            case ParenthesizedExpressionSyntax p:
                Text(p.Expression, format, values, readExpr);
                return;
            case LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression):
                Escaped(l.Token.ValueText, format);
                return;
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression):
                Text(b.Left, format, values, readExpr);
                Text(b.Right, format, values, readExpr);
                return;
            case InterpolatedStringExpressionSyntax s: {
                // The text of $"…" keeps its braces doubled, as a composite format has them; in a raw string
                // with several $ ($$"""…""") a single brace is text.
                bool singleBraces = s.StringStartToken.Text.TakeWhile(c => c == '$').Count() > 1;
                foreach (InterpolatedStringContentSyntax part in s.Contents) {
                    switch (part) {
                        case InterpolatedStringTextSyntax t when singleBraces:
                            Escaped(t.TextToken.ValueText, format);
                            break;
                        case InterpolatedStringTextSyntax t:
                            format.Append(t.TextToken.ValueText);
                            break;
                        case InterpolationSyntax h: {
                            if (h.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } or InterpolatedStringExpressionSyntax) {
                                throw CompileException.At(h.Expression, "a hole of Debug.Print holds a value (int, float, bool); write text outside the braces");
                            }
                            format.Append('{').Append(values.Count.ToString(CultureInfo.InvariantCulture));
                            if (h.AlignmentClause is { } a) format.Append(',').Append(a.Value.ToString().Replace(" ", "", StringComparison.Ordinal));
                            if (h.FormatClause is { } f) format.Append(':').Append(f.FormatStringToken.ValueText);
                            format.Append('}');
                            values.Add(readExpr(h.Expression));
                            break;
                        }
                    }
                }
                return;
            }
            default:
                throw CompileException.At(e, "Debug.Print takes a string literal or an interpolated string ($\"hp = {Hp}\")");
        }
    }

    /// <summary>Literal text in a composite format: braces doubled.</summary>
    private static void Escaped(string text, StringBuilder format) =>
        format.Append(text.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
}
