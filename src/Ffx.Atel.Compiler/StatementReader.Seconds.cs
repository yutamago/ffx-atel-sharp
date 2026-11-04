// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

// Wait.Seconds(s) (docs/language.md, "Waiting in seconds"; docs/research/frame-timing.md): written out as
// WaitFrames(n), n = s × 30 rounded, which the compiler counts from the constant s.
internal sealed partial class StatementReader {
    /// <summary>WaitFrames (<c>0x0000</c>).</summary>
    internal const ushort WaitFramesNative = 0x0000;

    /// <summary>Whether a call is <c>Wait.Seconds(…)</c> (also <c>Ffx.Wait.Seconds(…)</c>).</summary>
    private bool IsWaitSeconds(InvocationExpressionSyntax call) => call.Expression switch {
        MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Wait" }, Name: IdentifierNameSyntax { Identifier.ValueText: "Seconds" } } => !IsDataName("Wait"),
        MemberAccessExpressionSyntax {
            Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Ffx" }, Name.Identifier.ValueText: "Wait" },
            Name: IdentifierNameSyntax { Identifier.ValueText: "Seconds" },
        } => true,
        _ => false,
    };

    /// <summary><c>Wait.Seconds(s)</c>: <c>WaitFrames(n)</c>, n from the constant s (<see cref="FramesOf"/>).</summary>
    private CallStmt WaitSeconds(InvocationExpressionSyntax call) {
        if (call.ArgumentList.Arguments is not [ArgumentSyntax a] || a.NameColon is not null && a.NameColon.Name.Identifier.ValueText != "seconds") {
            throw Error(call.ArgumentList, "Wait.Seconds(seconds) takes one argument, the seconds");
        }
        double seconds = ConstantNumber(a.Expression)
            ?? throw Error(a.Expression, "Wait.Seconds takes a constant number of seconds (a literal, a const, or arithmetic of them): the compiler counts its frames (ATL3005)");
        int frames = FramesOf(seconds)
            ?? throw Error(a.Expression, $"Wait.Seconds({seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}) is {Describe(seconds)}; the shortest wait is one frame, 1/{Wait.FramesPerSecond} s, the longest {int.MaxValue} frames (ATL3006)");
        return new CallStmt(WaitFramesNative, [new IntLit(frames)]);
    }

    private static string Describe(double seconds) => double.IsFinite(seconds)
        ? $"{(seconds * Wait.FramesPerSecond).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} frames"
        : "no number of frames";

    /// <summary>
    ///     The frames <c>Wait.Seconds(seconds)</c> waits: seconds × <see cref="Wait.FramesPerSecond"/> (in double
    ///     precision, from the float the source gives), rounded to the nearest whole frame with halves away from zero;
    ///     null when that is below one frame, above <see cref="int.MaxValue"/>, or not a number.
    /// </summary>
    internal static int? FramesOf(double seconds) {
        double frames = Math.Round(seconds * Wait.FramesPerSecond, MidpointRounding.AwayFromZero);
        return double.IsFinite(frames) && frames >= 1 && frames <= int.MaxValue ? (int)frames : null;
    }

    /// <summary>
    ///     The value of a constant number expression as C# computes it: a literal, a <c>const</c> local, a game
    ///     constant, and <c>+ - * /</c> and unary minus of them (<c>int</c> arithmetic stays <c>int</c>, a float
    ///     operand makes it <c>float</c>); null for anything else.
    /// </summary>
    private double? ConstantNumber(ExpressionSyntax syntax) => Fold(syntax) switch {
        (double v, _) => v,
        _ => null,
    };

    private (double Value, bool IsFloat)? Fold(ExpressionSyntax syntax) {
        ExpressionSyntax e = Strip(syntax, conversions: false);
        switch (e) {
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.UnaryMinusExpression):
                return Fold(u.Operand) is (double v, bool f) ? (f ? -(float)v : -(long)v, f) : null;
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.UnaryPlusExpression):
                return Fold(u.Operand);
            case BinaryExpressionSyntax b when b.Kind() is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression or SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression: {
                if (Fold(b.Left) is not (double l, bool lf) || Fold(b.Right) is not (double r, bool rf)) return null;
                if (lf || rf) {
                    float x = (float)l, y = (float)r;
                    return (b.Kind() switch {
                        SyntaxKind.AddExpression => x + y,
                        SyntaxKind.SubtractExpression => x - y,
                        SyntaxKind.MultiplyExpression => x * y,
                        _ => x / y,
                    }, true);
                }
                long i = (long)l, j = (long)r;
                if (b.IsKind(SyntaxKind.DivideExpression) && j == 0) return null;
                return (b.Kind() switch {
                    SyntaxKind.AddExpression => i + j,
                    SyntaxKind.SubtractExpression => i - j,
                    SyntaxKind.MultiplyExpression => i * j,
                    _ => i / j,
                }, false);
            }
            case LiteralExpressionSyntax or IdentifierNameSyntax or MemberAccessExpressionSyntax: {
                // A literal, a const local (bound to its literal) or a game constant; anything else is no constant.
                if (e is not LiteralExpressionSyntax && !IsConstantName(e)) return null;
                return ReadExpr(e) switch {
                    IntLit lit => ((double)lit.Value, false),
                    FloatLit lit => ((double)BitConverter.UInt32BitsToSingle(lit.Bits), true),
                    _ => ((double, bool)?)null,
                };
            }
            default:
                return null;
        }
    }

    /// <summary>A name that reads as a constant: a <c>const</c> local, or a game constant (<c>Wait.FramesPerSecond</c>).</summary>
    private bool IsConstantName(ExpressionSyntax e) => e switch {
        IdentifierNameSyntax id => Local(id.Identifier.ValueText) is IntLit or FloatLit,
        MemberAccessExpressionSyntax m => GameConstants.Lookup(m, enums: true) is not null,
        _ => false,
    };
}
