// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

// Hidden storage in the workers' private data (storage 3; docs/encoding.md, "Locals"). Storage 3 is
// resolved through the worker that runs the code (AtelGetRealVarAdrs: the work's header +0x2c, or in
// battle the work's own buffer at +0xb8), also inside a function worker it calls (`call` keeps the
// caller's work and header). So a hidden local is a private variable of every worker that runs its code,
// at the same offset in each, and an entry's parameter field can be private only when the entry's own
// worker is the one that writes it.

public static partial class ScriptCompiler {
    /// <summary>
    ///     Whether every request of <paramref name="worker"/>'s <paramref name="method"/> in the code is made by
    ///     that worker itself, from an entry or a patch (not a helper, which runs on whichever worker calls
    ///     it): then the entry's parameter and result fields can live in the worker's private data. A
    ///     request names the worker (<c>W01.FooAsync(…)</c>); <c>Link.Entry</c> and <c>Request(…)</c> pass no
    ///     parameters.
    /// </summary>
    private static bool RequestedOnlyByItself(string worker, string method, IEnumerable<SyntaxTree> trees) {
        foreach (SyntaxTree tree in trees) {
            foreach (InvocationExpressionSyntax call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()) {
                if (call.Expression is not MemberAccessExpressionSyntax { Name: IdentifierNameSyntax name } m || name.Identifier.ValueText != method) continue;
                string? target = m.Expression switch {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                    MemberAccessExpressionSyntax inner => inner.Name.Identifier.ValueText,
                    _ => null,
                };
                if (target != worker) continue;
                ClassDeclarationSyntax? from = call.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                MethodDeclarationSyntax? body = call.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                if (from?.Identifier.ValueText != worker || body is null || IsHelper(body, functionWorker: false)) return false;
            }
        }
        return true;
    }

    /// <summary>
    ///     Hidden private variables of a script compiled whole: each at an offset in a new area that every
    ///     worker running its code gets after its own private data (<see cref="Lay"/>).
    /// </summary>
    private sealed class PrivateArea(ScriptImage image) {
        private readonly List<(int Var, int Offset)> _vars = [];
        private int _size;

        /// <summary>A new <c>i32</c> (<c>f32</c>) private variable of <paramref name="length"/> elements; its index.</summary>
        public int Add(bool isFloat, int length) {
            _vars.Add((image.Variables.Count, _size));
            _size += 4 * length;
            uint location = (uint)(isFloat ? 6 : 5) << 28 | (uint)Storage.Private << 25 | (length > 1 ? VarEntry.ArrayBit : 0);
            image.Variables.Add(new VarEntry(location, (ushort)length, 0));
            return image.Variables.Count - 1;
        }

        /// <summary>
        ///     Once the code is assembled: the workers that run code using the area (their own, or a function
        ///     worker's they call) and are not function workers themselves get it after their private data,
        ///     all at one offset (past the longest of theirs), and the variables get their offsets.
        /// </summary>
        public void Lay() {
            if (_vars.Count == 0) return;
            HashSet<int> vars = [.. _vars.Select(v => v.Var)];
            int n = image.Workers.Count;
            bool[] uses = new bool[n];
            List<int>[] calls = new List<int>[n];
            for (int w = 0; w < n; w++) {
                byte[] code = image.Workers[w].Code;
                calls[w] = [];
                for (int pc = 0; pc < code.Length;) {
                    byte b = code[pc];
                    if ((b & 0x80) != 0 && pc + 2 < code.Length) {
                        int op = b & 0x7f, operand = code[pc + 1] | code[pc + 2] << 8;
                        if (op == Opcodes.Call) calls[w].Add(operand);
                        else if (Opcodes.All[op].Operand == OperandKind.Var && vars.Contains(operand)) uses[w] = true;
                    }
                    pc += (b & 0x80) != 0 ? 3 : 1;
                }
            }
            for (bool changed = true; changed;) {
                changed = false;
                for (int w = 0; w < n; w++) {
                    if (!uses[w] && calls[w].Any(c => c < n && uses[c])) changed = uses[w] = true;
                }
            }
            List<WorkerImage> grown = [.. Enumerable.Range(0, n).Where(w => uses[w] && image.Workers[w].Type != 0).Select(w => image.Workers[w])];
            int start = grown.Count == 0 ? 0 : (int)((grown.Max(w => w.PrivateLength) + 3) & ~3u);
            foreach ((int v, int offset) in _vars) {
                VarEntry e = image.Variables[v];
                image.Variables[v] = e with { Location = (e.Location & ~0xff_ffffu) | (uint)(start + offset) };
            }
            foreach (WorkerImage w in grown) {
                w.PrivateLength = (uint)(start + _size);
                byte[] data = new byte[ScriptImage.PrivateSize(w.PrivateLength)];
                w.Private?.AsSpan(0, Math.Min(w.Private.Length, data.Length)).CopyTo(data);
                w.Private = data;
            }
        }
    }
}
