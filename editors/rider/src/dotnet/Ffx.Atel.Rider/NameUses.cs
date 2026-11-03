// SPDX-License-Identifier: MIT

using JetBrains.ReSharper.Psi.CSharp.Tree;

namespace Ffx.Atel.Rider
{
    /// <summary>The typed values with a name table in C# PSI, as the VS Code extension's regex finds them: a member
    /// <c>Target.Biran</c> (also <c>Ffx.Target.Biran</c>) and a cast of a constant <c>(Command)0x3000</c>. By their
    /// text, without resolving, like <see cref="TextUses" />.</summary>
    public static class NameUses
    {
        /// <summary><c>Type.Member</c> with a type of <see cref="NameResolver.Tables" />.</summary>
        public static bool TryGetMember(IReferenceExpression reference, out string type, out string member)
        {
            type = member = null;
            if (!(reference.QualifierExpression is IReferenceExpression qualifier)) return false;
            var name = qualifier.NameIdentifier?.Name;
            if (name == null || !NameResolver.Tables.ContainsKey(name)) return false;
            // Nothing but Ffx in front of the type: `x.Target.Biran` is something else.
            if (qualifier.QualifierExpression != null
                && !(qualifier.QualifierExpression is IReferenceExpression ns && ns.QualifierExpression == null && ns.NameIdentifier?.Name == "Ffx"))
                return false;
            member = reference.NameIdentifier?.Name;
            if (member == null) return false;
            type = name;
            return true;
        }

        /// <summary><c>(Type)constant</c> with a type of <see cref="NameResolver.Tables" />.</summary>
        public static bool TryGetCast(ICastExpression cast, out string type, out int id)
        {
            type = null;
            id = 0;
            var typeName = cast.TargetType?.GetText()?.Trim();
            if (typeName == null) return false;
            if (typeName.StartsWith("Ffx.", System.StringComparison.Ordinal)) typeName = typeName.Substring(4);
            if (!NameResolver.Tables.ContainsKey(typeName)) return false;
            if (!(cast.Op is ICSharpLiteralExpression literal)) return false;
            var value = NameResolver.ParseInteger(literal.Literal?.GetText());
            if (value == null) return false;
            type = typeName;
            id = value.Value;
            return true;
        }
    }
}
