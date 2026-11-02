// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

// Helper methods (docs/plan.md, "Three kinds of code"): a plain method is a hidden function worker that
// the caller runs with `call`. Parameters and the result are slots of the helper (docs/plan.md,
// "Parameters and return values"): the caller writes the parameters, calls, and reads the result into a
// temporary of its own at once.
internal sealed partial class StatementReader {
    /// <summary>The class whose code is read, for calls of its helpers without a class name.</summary>
    public string? Class { get; init; }

    /// <summary>A helper by class and method name; null where there is none.</summary>
    public Func<string, string, Helper?>? Helpers { get; init; }

    /// <summary>The script class, whose static helpers every file names without a prefix (<c>using static</c>).</summary>
    public string? ScriptClass { get; init; }

    /// <summary>The helpers the code read calls.</summary>
    public HashSet<Helper> Called { get; } = [];

    /// <summary>Where <c>return value;</c> puts the value: the helper's (or entry's) result.</summary>
    public Slot? Result { get; set; }

    /// <summary>Reads a method body with its parameters in scope.</summary>
    public List<Stmt> Method(BlockSyntax body, IEnumerable<(string Name, Slot Slot)> parameters) {
        PushScope();
        try {
            foreach ((string name, Slot slot) in parameters) _scopes[^1][name] = new LocalExpr(slot);
            return Block(body.Statements);
        }
        finally {
            PopScope();
        }
    }

    /// <summary>An expression body (<c>=&gt; e</c>): <c>return e;</c>, or <c>e;</c> for a method without a result.</summary>
    public List<Stmt> Method(ArrowExpressionClauseSyntax body, IEnumerable<(string Name, Slot Slot)> parameters) {
        PushScope();
        try {
            foreach ((string name, Slot slot) in parameters) _scopes[^1][name] = new LocalExpr(slot);
            if (Result is null) return Statements(SyntaxFactory.ExpressionStatement(body.Expression));
            (List<Stmt> pre, Expr value) = Apart(() => ReadExpr(body.Expression));
            return [.. pre, Set(Result, value), new OpStmt(functionWorker ? Opcodes.Ret : Opcodes.End)];
        }
        finally {
            PopScope();
        }
    }

    /// <summary>The helper a call names: <c>Foo(…)</c> in its own class or the script class, or <c>W01.Foo(…)</c>.</summary>
    private Helper? HelperOf(InvocationExpressionSyntax call) {
        if (Helpers is null) return null;
        return call.Expression switch {
            IdentifierNameSyntax id => (Class is null ? null : Helpers(Class, id.Identifier.ValueText))
                ?? (ScriptClass is null ? null : Helpers(ScriptClass, id.Identifier.ValueText)),
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax cls, Name: IdentifierNameSyntax m } => Helpers(cls.Identifier.ValueText, m.Identifier.ValueText),
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax m } when Class is not null => Helpers(Class, m.Identifier.ValueText),
            _ => null,
        };
    }

    /// <summary>A helper call: the arguments into its parameters, then <c>call</c>.</summary>
    private CallWorkerStmt CallHelper(Helper h, InvocationExpressionSyntax call) {
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;
        if (args.Count != h.Parameters.Length) throw Error(call.ArgumentList, $"{h.Name} takes {h.Parameters.Length} arguments");
        int[] position = new int[args.Count];
        for (int k = 0; k < args.Count; k++) {
            ArgumentSyntax a = args[k];
            if (!a.RefKindKeyword.IsKind(SyntaxKind.None)) throw Error(a, "a helper takes values (no ref or out)");
            position[k] = a.NameColon is null ? k : Array.IndexOf(h.ParameterNames, a.NameColon.Name.Identifier.ValueText);
            if (position[k] < 0) throw Error(a.NameColon!, $"{h.Name} has no parameter {a.NameColon!.Name.Identifier.ValueText}");
        }
        // (ReadArgs evaluates an argument into a temporary when a later one needs statements, such as a
        // call: so no call runs between writing the parameters and the `call`.)
        List<Expr> values = ReadArgs(args.Select(a => a.Expression));
        for (int k = 0; k < values.Count; k++) _pre.Add(Set(h.Parameters[position[k]], values[k]));
        Called.Add(h);
        return new CallWorkerStmt(h.Worker);
    }

    /// <summary>A helper call as a statement: its result, if any, is dropped.</summary>
    private Stmt HelperStatement(Helper h, InvocationExpressionSyntax call) => CallHelper(h, call);

    /// <summary>A helper call as a value: the call first, then its result into a temporary.</summary>
    private Expr HelperValue(Helper h, InvocationExpressionSyntax call) {
        if (h.Result is null) throw Error(call, $"{h.Name} returns no value");
        _pre.Add(CallHelper(h, call));
        Slot t = Temp(h.Result.Float);
        _pre.Add(Set(t, new LocalExpr(h.Result)));
        return new LocalExpr(t);
    }

    /// <summary><c>return value;</c>: the value into the result, then the end (<c>ret</c> in a helper).</summary>
    private Stmt ReturnValue(ReturnStatementSyntax r) {
        if (Result is null) throw Error(r, "this method returns no value");
        _pre.Add(Set(Result, ReadExpr(r.Expression!)));
        return new OpStmt(functionWorker ? Opcodes.Ret : Opcodes.End);
    }
}

/// <summary>
///     A helper method: a plain method of a worker or of the script class, which the compiler makes a hidden
///     function worker (<see cref="Worker"/>, numbered after the script's workers) and runs with <c>call</c>.
/// </summary>
internal sealed class Helper {
    public Helper(string cls, MethodDeclarationSyntax method, IReadOnlyList<ClassDeclarationSyntax> parts) {
        Class = cls;
        Method = method;
        Parts = parts;
        foreach (ParameterSyntax p in method.ParameterList.Parameters) {
            if (p.Modifiers.Count > 0) throw CompileException.At(p, "a helper's parameter is a value (no ref, out, in or params)");
            if (p.Default is not null) throw CompileException.At(p.Default, "a helper's parameter has no default (yet)");
            if (p.Type is null) throw CompileException.At(p, "a parameter needs its type");
        }
        ParameterNames = [.. method.ParameterList.Parameters.Select(p => p.Identifier.ValueText)];
        Parameters = [.. method.ParameterList.Parameters.Select(p => new Slot($"{Name}.{p.Identifier.ValueText}", ParameterIsFloat(p.Type!)))];
        Result = method.ReturnType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword }
            ? null
            : new Slot($"{Name}.result", ParameterIsFloat(method.ReturnType));
        if (method.TypeParameterList is not null) throw CompileException.At(method.TypeParameterList, "a helper is not generic");
        if (method.Body is null && method.ExpressionBody is null) throw CompileException.At(method, "a helper has a body");
    }

    private static bool ParameterIsFloat(TypeSyntax type) => type switch {
        PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.FloatKeyword } => true,
        PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.IntKeyword or (int)SyntaxKind.UIntKeyword or (int)SyntaxKind.ShortKeyword
            or (int)SyntaxKind.UShortKeyword or (int)SyntaxKind.ByteKeyword or (int)SyntaxKind.SByteKeyword or (int)SyntaxKind.BoolKeyword } => false,
        PredefinedTypeSyntax p => throw CompileException.At(type, $"a helper's parameter or result cannot be {p.Keyword.ValueText}"),
        ArrayTypeSyntax => throw CompileException.At(type, "a helper's parameter or result cannot be an array"),
        _ => false,
    };

    public string Class { get; }
    public MethodDeclarationSyntax Method { get; }
    public string Name => $"{Class}.{Method.Identifier.ValueText}";

    /// <summary>The declaring class's parts, whose <c>[Register]</c> fields its code may name.</summary>
    public IReadOnlyList<ClassDeclarationSyntax> Parts { get; }

    public string[] ParameterNames { get; }
    public Slot[] Parameters { get; }
    public Slot? Result { get; }

    /// <summary>Its number as a worker of the script (or of the linked script, in a mod).</summary>
    public int Worker { get; set; }

    /// <summary>Its code, before and after its slots are placed.</summary>
    public FlowEmitter? Emitter { get; set; }

    /// <summary>The helpers it calls.</summary>
    public HashSet<Helper> Calls { get; } = [];

    /// <summary>The workers whose own code (entries, patches) calls it.</summary>
    public HashSet<int> CalledBy { get; } = [];

    /// <summary>Once placed: every register it and what it calls may change, and whether it may wait.</summary>
    public RegisterSet Total { get; set; }

    public bool Waits { get; set; }

    public bool Placed { get; set; }

    public override string ToString() => Name;
}
