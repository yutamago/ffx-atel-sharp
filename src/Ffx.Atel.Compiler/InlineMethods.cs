// SPDX-License-Identifier: MIT

using System.Reflection;
using Ffx.Atel.Bytecode.Analysis;
using Ffx.Atel.Bytecode.Code;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

/// <summary>
///     The inline methods a compilation can call (docs/encoding.md, "Inline methods"): those of the reference
///     assembly (<c>Ffx.Dialogue</c>, whose source the assembly embeds) and methods with <c>[Inline]</c> in the
///     compiled source. Each is read once into a template (<see cref="InlineMacro"/>): its body's statements
///     with a <see cref="ParamExpr"/> wherever a parameter is used.
/// </summary>
public sealed class InlineMethods {
    private readonly Dictionary<(string Class, string Method), MethodDeclarationSyntax> _declared = [];
    private readonly Dictionary<(string Class, string Method), InlineMacro> _read = [];
    private readonly List<(string, string)> _reading = [];
    private readonly InlineMethods? _outer;

    private InlineMethods(InlineMethods? outer) => _outer = outer;

    /// <summary>The reference assembly's inline methods.</summary>
    public static InlineMethods Game => s_game.Value;

    private static readonly Lazy<InlineMethods> s_game = new(() => {
        InlineMethods table = new(null);
        Assembly game = typeof(InlineAttribute).Assembly;
        foreach (string resource in game.GetManifestResourceNames().Where(n => n.StartsWith("Ffx.Atel.Game.Inline.", StringComparison.Ordinal)).Order(StringComparer.Ordinal)) {
            using Stream stream = game.GetManifestResourceStream(resource)!;
            using StreamReader text = new(stream);
            table.Add(ScriptCompiler.Parse(text.ReadToEnd(), resource));
        }
        foreach ((string cls, string method) in table._declared.Keys.ToList()) table.Find(cls, method);
        return table;
    });

    /// <summary>The reference assembly's inline methods and those declared in <paramref name="trees"/>.</summary>
    public static InlineMethods With(IEnumerable<SyntaxTree> trees) {
        InlineMethods table = new(Game);
        foreach (SyntaxTree t in trees) table.Add(t);
        return table;
    }

    /// <summary>
    ///     The script the methods declared in the compiled source belong to: its variables, workers and, per
    ///     class, its registers, which their bodies may use (an inline method of a script reads and assigns
    ///     the script's variables). Without it a body uses only parameters and constants.
    /// </summary>
    internal ScriptContext? Script { get; set; }

    /// <summary>What a script's inline methods may name: the variables, the workers and a class's <c>[Register]</c> fields.</summary>
    internal sealed record ScriptContext(IReadOnlyDictionary<string, VariableSymbol> Variables, IReadOnlyDictionary<string, int> Workers, Func<string, IReadOnlyDictionary<string, RegExpr>> Registers);

    /// <summary>
    ///     The class a component's inline methods are declared in, by the component's name: <c>Turn.To(…)</c> is
    ///     <c>TurnControl.To</c>, written on the worker's <c>Turn</c>.
    /// </summary>
    private static readonly Dictionary<string, string> s_components = new(StringComparer.Ordinal) {
        ["TurnControl"] = "Turn", ["MoveControl"] = "Move", ["MotionControl"] = "Motion", ["NeckControl"] = "Neck",
    };

    /// <summary>Whether <paramref name="name"/> is a component of the worker whose inline methods are called through it (<c>Turn</c>).</summary>
    internal static bool IsComponent(string name) => s_components.ContainsValue(name);

    /// <summary>Every inline method the table has read, largest template first (the order the decompiler tries them in).</summary>
    public IReadOnlyList<InlineMacro> All => [.. _read.Values.Concat(_outer?.All ?? []).OrderByDescending(m => m.Size).ThenBy(m => m.Name, StringComparer.Ordinal)];

    /// <summary>Whether a method is an inline method: it has <c>[Inline]</c>.</summary>
    public static bool IsInline(MethodDeclarationSyntax m) => m.AttributeLists.SelectMany(l => l.Attributes).Any(a => ScriptCompiler.NameOf(a) == "Inline");

    private void Add(SyntaxTree tree) {
        foreach (MethodDeclarationSyntax m in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(IsInline)) {
            string cls = m.FirstAncestorOrSelf<TypeDeclarationSyntax>()?.Identifier.ValueText ?? "";
            cls = s_components.GetValueOrDefault(cls, cls);
            if (!_declared.TryAdd((cls, m.Identifier.ValueText), m)) {
                throw CompileException.At(m.Identifier.GetLocation(), $"{cls}.{m.Identifier.ValueText} is declared twice (inline methods have no overloads)");
            }
        }
    }

    /// <summary>The inline method <c>cls.method</c>, read on first use; null if there is none.</summary>
    internal InlineMacro? Find(string cls, string method) {
        if (!_declared.TryGetValue((cls, method), out MethodDeclarationSyntax? m)) return _outer?.Find(cls, method);
        if (_read.TryGetValue((cls, method), out InlineMacro? done)) return done;
        if (_reading.Contains((cls, method))) {
            throw CompileException.At(m.Identifier.GetLocation(), $"inline method {cls}.{method} calls itself ({string.Join(" → ", _reading.Append((cls, method)).Select(x => $"{x.Item1}.{x.Item2}"))}); an inline method is written out in place, so it cannot recurse");
        }
        _reading.Add((cls, method));
        try {
            return _read[(cls, method)] = Read(cls, m);
        }
        finally {
            _reading.RemoveAt(_reading.Count - 1);
        }
    }

    private InlineMacro Read(string cls, MethodDeclarationSyntax m) {
        string name = $"{cls}.{m.Identifier.ValueText}";
        if (m.ReturnType is not PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword }) throw CompileException.At(m.ReturnType, $"inline method {name} returns nothing (void)");
        if (m.TypeParameterList is not null) throw CompileException.At(m.TypeParameterList, "an inline method is not generic");
        if (m.Modifiers.Any(SyntaxKind.AsyncKeyword)) throw CompileException.At(m, "an inline method is not async");
        List<InlineParam> parameters = [];
        StatementReader reader = Reader(cls);
        foreach (ParameterSyntax p in m.ParameterList.Parameters) {
            if (p.Modifiers.Count > 0 || p.Type is null) throw CompileException.At(p, "an inline method's parameter is a value (no ref, out, in or params)");
            bool isFloat = p.Type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.FloatKeyword };
            if (!isFloat && p.Type is not (PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.IntKeyword or (int)SyntaxKind.BoolKeyword } or IdentifierNameSyntax { Identifier.ValueText: "TextId" })) {
                throw CompileException.At(p.Type, "an inline method's parameter is int, float, bool or TextId");
            }
            Expr? def = null;
            if (p.Default is not null) {
                def = Reader(cls).ReadExpr(p.Default.Value);
                if (def is not (IntLit or FloatLit)) throw CompileException.At(p.Default, "a default is a number");
            }
            bool eager = p.AttributeLists.SelectMany(l => l.Attributes).Any(a => ScriptCompiler.NameOf(a) == "Eager");
            parameters.Add(new InlineParam(p.Identifier.ValueText, isFloat, def, eager));
        }
        if (m.Body is null) throw CompileException.At(m, "an inline method has a block body");
        // [InlineLocal("kind", 3)]: the local `kind` lives in register 3 of the calling worker.
        Dictionary<string, int> pinned = new(StringComparer.Ordinal);
        foreach (AttributeSyntax a in m.AttributeLists.SelectMany(l => l.Attributes).Where(a => ScriptCompiler.NameOf(a) == "InlineLocal")) {
            if (a.ArgumentList?.Arguments is not [{ Expression: LiteralExpressionSyntax { Token.Value: string localName } }, { Expression: LiteralExpressionSyntax { Token.Value: int register } }]) {
                throw CompileException.At(a, "[InlineLocal(name, register)] takes the local's name and its register's number");
            }
            if (!pinned.TryAdd(localName, register)) throw CompileException.At(a, $"{localName} is pinned twice");
        }
        reader.RegisterLocals = pinned;
        List<Stmt> body = reader.Template(m.Body, parameters.Select(p => p.Name));
        foreach (string unused in pinned.Keys.Where(k => !reader.RegisterLocalsDeclared.Contains(k))) {
            throw CompileException.At(m.Identifier.GetLocation(), $"[InlineLocal] names {unused}, which inline method {name} does not declare");
        }
        for (int k = 0; k < parameters.Count; k++) {
            if (parameters[k].Eager && !Inline.CanBeEager(body, k)) {
                throw CompileException.At(m.ParameterList.Parameters[k], $"[Eager] parameter {parameters[k].Name} of {name} is used once, in the first statement, and nothing there calls a native before it: else its argument would not be evaluated once, first");
            }
        }
        bool waits = false;
        Inline.Walk(body, e => {
            if (e is CallExpr c) waits |= ScriptCompiler.Natives[c.Native]?.Polls ?? true;
        }, s => {
            switch (s) {
                case CallStmt c: waits |= ScriptCompiler.Natives[c.Native]?.Polls ?? true; break;
                case OpStmt or EndWithStmt: throw CompileException.At(m, $"inline method {name} ends nothing (no return): its code runs on after the call");
                case GotoStmt or CondGotoStmt or LabelStmt or AsmStmt or EntryStmt or CallWorkerStmt or RequestStmt or OpArgsStmt:
                    throw CompileException.At(m, $"inline method {name} holds {s.GetType().Name[..^4]}: an inline method calls natives and inline methods, with if, loops and switch");
            }
        });
        bool local = false;
        Inline.Walk(body, e => local |= e is LocalExpr, _ => { });
        if (local) {
            throw CompileException.At(m.Identifier.GetLocation(), $"inline method {name} has a local (or an expression that needs a temporary): an inline method's local lives in a register of the caller, [InlineLocal(name, register)]");
        }
        bool declared = m.AttributeLists.SelectMany(l => l.Attributes).Where(a => ScriptCompiler.NameOf(a) == "Inline")
            .SelectMany(a => a.ArgumentList?.Arguments ?? [])
            .Any(a => a.NameEquals?.Name.Identifier.ValueText == "Waits" && a.Expression.IsKind(SyntaxKind.TrueLiteralExpression));
        if (declared != waits) {
            throw CompileException.At(m.Identifier.GetLocation(), waits
                ? $"inline method {name} calls a native that waits: mark it [Inline(Waits = true)]"
                : $"inline method {name} does not wait: drop Waits = true");
        }
        return new InlineMacro(name, parameters, body, waits);
    }

    // A template of the reference assembly uses only its parameters, constants, natives and other inline
    // methods; one of a script also the script's variables, its workers and its class's registers.
    private StatementReader Reader(string cls) => Script is { } s
        ? new(s.Variables, s.Registers(cls), ScriptCompiler.NativesByName.Value, s.Workers, false) { Class = cls, Inlines = this }
        : new(new Dictionary<string, VariableSymbol>(), new Dictionary<string, RegExpr>(), ScriptCompiler.NativesByName.Value, new Dictionary<string, int>(), false) {
            Class = cls, Inlines = this,
        };
}
