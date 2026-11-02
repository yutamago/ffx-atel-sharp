// SPDX-License-Identifier: MIT

using System.Linq;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;

namespace Ffx.Atel.Rider
{
    /// <summary>The uses of the message indexer in C# PSI: <c>Text[12]</c> (also <c>x.Text[12]</c>), with a decimal
    /// constant index, as the VS Code extension's regex finds them.</summary>
    public static class TextUses
    {
        public static bool TryGetIndex(IElementAccessExpression access, out int index)
        {
            index = -1;
            if (!(access.Operand is IReferenceExpression reference) || reference.NameIdentifier?.Name != "Text") return false;
            if (access.Arguments.Count != 1 || !(access.Arguments[0].Value is ICSharpLiteralExpression literal)) return false;
            var text = literal.Literal?.GetText();
            if (string.IsNullOrEmpty(text) || text.Length > 9 || !text.All(c => c >= '0' && c <= '9')) return false;
            index = int.Parse(text);
            return true;
        }

        /// <summary>The <c>Text[n]</c> a node is part of (the indexer, its name or its index), if any.</summary>
        public static IElementAccessExpression Containing(ITreeNode node, out int index)
        {
            index = -1;
            for (var n = node; n != null; n = n.Parent)
            {
                if (n is IElementAccessExpression access) return TryGetIndex(access, out index) ? access : null;
                if (n is IStatement || n is ICSharpTypeMemberDeclaration) return null;
            }

            return null;
        }
    }
}
