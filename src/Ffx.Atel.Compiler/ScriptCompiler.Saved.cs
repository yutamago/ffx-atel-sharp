// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

/// <summary>
///     One <c>[Saved]</c> field of a mod (docs/language.md, "Saved fields"): the mod's own, or with <see cref="Mod"/> another
///     mod's that it reads.
/// </summary>
/// <param name="Key">The key: <c>&lt;namespace&gt;.&lt;class&gt;.&lt;field&gt;</c>, or the attribute's <c>Key</c>.</param>
/// <param name="Mod">The other mod's id (<c>[Saved(Mod = …)]</c>); null for the mod's own field.</param>
/// <param name="Entry">Its variable: storage 0, offset 0 (the loader gives the offset).</param>
/// <param name="Initial">Its initializer's bytes; null for zeros (and for another mod's field).</param>
public sealed record SavedDeclaration(string Key, string? Mod, string Namespace, string Class, string Field, VarEntry Entry, byte[]? Initial) {
    /// <summary>The declaring class.</summary>
    internal ClassDeclarationSyntax? Declaring { get; init; }

    /// <summary><c>Namespace.Class.Field</c>, as C# names it from anywhere.</summary>
    public string FullName => Namespace.Length == 0 ? $"{Class}.{Field}" : $"{Namespace}.{Class}.{Field}";

    /// <summary>What a package's variable names it by (<see cref="NewVariable.Saved"/>): the key, another mod's with its id.</summary>
    public string PackageKey => Mod is null ? Key : SavedField.FullKey(Mod, Key);

    /// <summary>The field for the mod's <c>saved.json</c>.</summary>
    public SavedField ToField() => new() {
        Key = Key, Type = Entry.Type, Length = Entry.Length, RowLength = Entry.RowLength, Array = Entry.IsArray, Initial = Initial,
    };
}

/// <summary>Every <c>[Saved]</c> field of a mod, from all its files (<see cref="ScriptCompiler.ReadSaved"/>).</summary>
public sealed class SavedDeclarations(IReadOnlyList<SavedDeclaration> fields) {
    public IReadOnlyList<SavedDeclaration> Fields { get; } = fields;

    public static SavedDeclarations Empty { get; } = new([]);

    /// <summary>The mod's own fields as its <c>atel/saved.json</c>; null when it declares none.</summary>
    public SavedFile? ToFile() => Fields.Any(f => f.Mod is null) ? new SavedFile { Fields = [.. Fields.Where(f => f.Mod is null).Select(f => f.ToField())] } : null;

    /// <summary>The mods whose fields it reads; its <c>mod.toml</c> must require each.</summary>
    public IEnumerable<string> ReadMods => Fields.Where(f => f.Mod is not null).Select(f => f.Mod!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    /// <summary>Whether a class only holds saved fields: a mod's plain static class of them, which is no worker.</summary>
    public static bool IsSavedClass(ClassDeclarationSyntax c) =>
        c.Modifiers.Any(SyntaxKind.StaticKeyword) && !c.Modifiers.Any(SyntaxKind.PartialKeyword) && c.BaseList is null
        && c.Members.OfType<FieldDeclarationSyntax>().Any(ScriptCompiler.IsSaved);
}

public static partial class ScriptCompiler {
    /// <summary>Whether a field has <c>[Saved]</c>.</summary>
    public static bool IsSaved(FieldDeclarationSyntax f) => Attributes(f).Any(a => NameOf(a) == "Saved");

    /// <summary>
    ///     A mod's <c>[Saved]</c> fields, from every file of the mod: those of its plain static classes (in any namespace) and
    ///     of its parts of script classes.
    /// </summary>
    /// <exception cref="CompileException">A field that cannot be saved, or two with one key.</exception>
    public static SavedDeclarations ReadSaved(IEnumerable<SyntaxTree> modTrees) {
        List<SavedDeclaration> fields = [];
        StatementReader constants = new(new Dictionary<string, VariableSymbol>(StringComparer.Ordinal), new Dictionary<string, RegExpr>(StringComparer.Ordinal),
            NativesByName.Value, new Dictionary<string, int>(StringComparer.Ordinal), false);
        foreach (SyntaxTree tree in modTrees.OrderBy(t => t.FilePath, SourceOrder.Comparer)) {
            foreach (FieldDeclarationSyntax field in tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>().Where(IsSaved)) {
                fields.Add(ReadSavedField(field, constants));
            }
            foreach (ClassDeclarationSyntax c in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Where(SavedDeclarations.IsSavedClass)) {
                foreach (MemberDeclarationSyntax m in c.Members) {
                    if (m is FieldDeclarationSyntax f && IsSaved(f)) continue;
                    throw CompileException.At(m, $"{c.Identifier.ValueText} is a class of [Saved] fields: it holds those only");
                }
            }
        }
        foreach (IGrouping<string, SavedDeclaration> g in fields.GroupBy(f => f.PackageKey, StringComparer.Ordinal).Where(g => g.Count() > 1)) {
            SavedDeclaration second = g.Skip(1).First();
            throw CompileException.At(second.Declaring!.Identifier.GetLocation(), second.Mod is null
                ? $"two [Saved] fields have the key {g.Key}; give one its own Key"
                : $"{second.Mod}'s saved field {second.Key} is declared twice");
        }
        return new SavedDeclarations(fields);
    }

    private static SavedDeclaration ReadSavedField(FieldDeclarationSyntax field, StatementReader constants) {
        AttributeSyntax a = Attributes(field).First(x => NameOf(x) == "Saved");
        if (field.Parent is not ClassDeclarationSyntax cls || cls.Parent is ClassDeclarationSyntax) {
            throw CompileException.At(field, "a [Saved] field is in a mod's static class or in its part of the script class, not in a nested class");
        }
        if (!field.Modifiers.Any(SyntaxKind.StaticKeyword) || field.Modifiers.Any(SyntaxKind.ConstKeyword) || field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)) {
            throw CompileException.At(field, "a [Saved] field is static, and neither const nor readonly");
        }
        if (Attributes(field).FirstOrDefault(x => NameOf(x) is "Var" or "Register") is AttributeSyntax other) {
            throw CompileException.At(other, $"a [Saved] field has no [{NameOf(other)}]: the loader gives it its place");
        }
        if (field.Declaration.Variables.Count != 1) throw CompileException.At(field, "one variable per [Saved] field");
        string? key = null, mod = null;
        foreach ((string name, object value) in Named(a)) {
            string text = value as string ?? throw CompileException.At(a, $"[Saved] takes {name} as a string");
            switch (name) {
                case "Key": key = text; break;
                case "Mod": mod = text; break;
                default: throw CompileException.At(a, $"[Saved] has no {name}");
            }
        }
        string ns = cls.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>()?.Name.ToString() ?? "";
        string className = cls.Identifier.ValueText, fieldName = field.Declaration.Variables[0].Identifier.ValueText;
        if (mod is not null && key is null) throw CompileException.At(a, "another mod's field is named by its Key: [Saved(Mod = \"…\", Key = \"Namespace.Class.Field\")]");
        key ??= ns.Length == 0 ? $"{className}.{fieldName}" : $"{ns}.{className}.{fieldName}";
        if (SavedField.KeyProblem(key) is string bad) throw CompileException.At(a, bad);
        if (key.Contains('/', StringComparison.Ordinal)) throw CompileException.At(a, "a key has no /: the mod is named by Mod");
        if (mod is not null && (mod.Length == 0 || mod.Contains('/', StringComparison.Ordinal))) throw CompileException.At(a, "Mod is a mod's id");

        VarEntry shared = Variable(field, allocated: true);
        VarEntry entry = new((uint)shared.Type << 28 | (shared.IsArray ? VarEntry.ArrayBit : 0), (ushort)shared.Length, (ushort)shared.RowLength);
        if (entry.ElementSize * entry.Length > SaveArea.Length) {
            throw CompileException.At(field, $"{fieldName} is {entry.ElementSize * entry.Length} bytes; the mods' saved fields share {SaveArea.Length}");
        }
        byte[]? initial = null;
        ExpressionSyntax? init = field.Declaration.Variables[0].Initializer?.Value;
        if (mod is not null) {
            if (init is not null && (ArrayInitializer(init) is not null || init is not ArrayCreationExpressionSyntax)) {
                throw CompileException.At(init, $"{fieldName} is {mod}'s field: its initial value is that mod's");
            }
        }
        else {
            initial = DataEdits.InitialValue(field, entry, constants);
        }
        return new SavedDeclaration(key, mod, ns, className, fieldName, entry, initial) { Declaring = cls };
    }
}
