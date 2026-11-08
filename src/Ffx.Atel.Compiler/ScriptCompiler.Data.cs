// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Globalization;
using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Linker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ffx.Atel.Compiler;

public static partial class ScriptCompiler {
    /// <summary>
    ///     The fields of <c>Ffx.MonsterStats</c> and <c>Ffx.MonsterRewards</c> (src/Ffx.Atel.Game/Monster.cs): their offset in
    ///     the stat record or the rewards, and their variable type (0 <c>u8</c>, 2 <c>u16</c>, 4 <c>u32</c>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, (int Offset, int Type)>> MonsterFields =
        new Dictionary<string, IReadOnlyDictionary<string, (int, int)>>(StringComparer.Ordinal) {
            ["MonsterStats"] = new Dictionary<string, (int, int)>(StringComparer.Ordinal) {
                ["Hp"] = (0x14, 4), ["Mp"] = (0x18, 4), ["OverkillThreshold"] = (0x1c, 4),
                ["Strength"] = (0x20, 0), ["Defense"] = (0x21, 0), ["Magic"] = (0x22, 0), ["MagicDefense"] = (0x23, 0),
                ["Agility"] = (0x24, 0), ["Luck"] = (0x25, 0), ["Evasion"] = (0x26, 0), ["Accuracy"] = (0x27, 0),
            },
            ["MonsterRewards"] = new Dictionary<string, (int, int)>(StringComparer.Ordinal) {
                ["Gil"] = (0x00, 2), ["Ap"] = (0x02, 2), ["OverkillAp"] = (0x04, 2),
                ["DropChance1"] = (0x08, 0), ["DropChance2"] = (0x09, 0), ["StealChance"] = (0x0a, 0), ["EquipmentDropChance"] = (0x0b, 0),
            },
        };

    /// <summary>
    ///     A mod's data edits (docs/language.md, "Initial values and data edits"): the assignments of its static
    ///     constructors, which become <see cref="ScriptPackage.Data"/>, and its new variables' initializers.
    /// </summary>
    private sealed class DataEdits(ScriptPackage package, ScriptImage vanilla, ScriptCategory category, int vanillaVariables, IReadOnlyList<string> variableNames) {
        private readonly Dictionary<(DataEditArea, int?, int), string> _set = [];

        /// <summary>
        ///     Reads a static constructor of the script class (<paramref name="worker"/> null) or of vanilla worker
        ///     <paramref name="worker"/>: each statement sets one field to a constant.
        /// </summary>
        public void Read(ConstructorDeclarationSyntax ctor, string className, int? worker, StatementReader reader) {
            if (!ctor.Modifiers.Any(SyntaxKind.StaticKeyword)) {
                throw CompileException.At(ctor.Identifier.GetLocation(), $"a mod's constructor is static and sets initial values: static {className}() {{ … }}");
            }
            IEnumerable<StatementSyntax> body = ctor.Body?.Statements
                ?? (ctor.ExpressionBody is ArrowExpressionClauseSyntax arrow ? [SyntaxFactory.ExpressionStatement(arrow.Expression)] : []);
            foreach (StatementSyntax s in body) {
                if (s is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax asg } || !asg.IsKind(SyntaxKind.SimpleAssignmentExpression)) {
                    throw CompileException.At(s, "a static constructor holds initial values only: `Field = constant;`, `Table[3] = constant;`, `MonsterStats.Hp = constant;`");
                }
                Expr value = reader.ConstantValue(asg.Right) ?? throw CompileException.At(asg.Right, "an initial value is a constant (a literal, a const, a game constant or an enum member)");
                (DataEditArea area, int offset, int type, string field) = Target(asg.Left, worker, reader);
                Add(area, area == DataEditArea.Private ? worker : null, offset, Encode(value, type, asg.Right), field, asg);
            }
        }

        /// <summary>The initial value of a mod's new variable, from its initializer; null when it has none or it is all zeros.</summary>
        public byte[]? Initial(FieldDeclarationSyntax field, VarEntry entry, StatementReader reader) {
            ExpressionSyntax? init = field.Declaration.Variables[0].Initializer?.Value;
            if (init is null) return null;
            List<ExpressionSyntax> values = [];
            InitializerExpressionSyntax? list = ArrayInitializer(init);
            if (list is not null) Flatten(list, values);
            else if (init is ArrayCreationExpressionSyntax) return null;
            else values.Add(init);
            if (values.Count > entry.Length) throw CompileException.At(init, $"{values.Count} values for {entry.Length} elements");
            List<byte> bytes = [];
            foreach (ExpressionSyntax v in values) {
                Expr value = reader.ConstantValue(v) ?? throw CompileException.At(v, "an initial value is a constant (a literal, a const, a game constant or an enum member)");
                bytes.AddRange(Encode(value, entry.Type, v));
            }
            return bytes.All(b => b == 0) ? null : [.. bytes];

            static void Flatten(InitializerExpressionSyntax l, List<ExpressionSyntax> into) {
                foreach (ExpressionSyntax e in l.Expressions) {
                    if (e is InitializerExpressionSyntax row) Flatten(row, into);
                    else into.Add(e);
                }
            }
        }

        private (DataEditArea Area, int Offset, int Type, string Field) Target(ExpressionSyntax left, int? worker, StatementReader reader) {
            // MonsterStats.Hp, MonsterRewards.Gil.
            if (left is MemberAccessExpressionSyntax m && LastName(m.Expression) is string owner && MonsterFields.TryGetValue(owner, out var fields)) {
                if (!fields.TryGetValue(m.Name.Identifier.ValueText, out var f)) throw CompileException.At(m.Name, $"{owner} has no {m.Name.Identifier.ValueText}");
                if (category != ScriptCategory.Monster) throw CompileException.At(left, $"{owner} is a monster's data: only a monster script (battle/mon/) has it");
                if (worker is not null) throw CompileException.At(left, $"{owner} is set in the script class's static constructor");
                return (owner == "MonsterStats" ? DataEditArea.MonsterStats : DataEditArea.MonsterRewards, f.Offset, f.Type, $"{owner}.{m.Name.Identifier.ValueText}");
            }
            // Data.At<T>(DataArea.Shared, 0x64).
            if (left is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "At", TypeArgumentList.Arguments: [TypeSyntax t] } } call } at
                && LastName(call.Expression) == "Data") {
                if (at.ArgumentList.Arguments is not [ArgumentSyntax areaArg, ArgumentSyntax offsetArg]) throw CompileException.At(at, "Data.At<T>(DataArea.<area>, offset)");
                int type = t is PredefinedTypeSyntax p ? p.Keyword.Kind() switch {
                    SyntaxKind.ByteKeyword => 0, SyntaxKind.SByteKeyword => 1, SyntaxKind.UShortKeyword => 2, SyntaxKind.ShortKeyword => 3,
                    SyntaxKind.UIntKeyword => 4, SyntaxKind.IntKeyword => 5, SyntaxKind.FloatKeyword => 6, _ => -1,
                } : -1;
                if (type < 0) throw CompileException.At(t, "Data.At<T> takes byte, sbyte, ushort, short, uint, int or float");
                DataEditArea area = (areaArg.Expression is MemberAccessExpressionSyntax am ? am.Name.Identifier.ValueText : areaArg.Expression.ToString()) switch {
                    "Shared" => DataEditArea.Shared, "Other" => DataEditArea.Other, "Event" => DataEditArea.Event, "Area" => DataEditArea.Area, "Private" => DataEditArea.Private,
                    _ => throw CompileException.At(areaArg, "the area is DataArea.Shared, Other, Event, Area or Private"),
                };
                if (reader.ConstantValue(offsetArg.Expression) is not IntLit { Value: >= 0 } o) throw CompileException.At(offsetArg, "the offset is a constant, 0 or more");
                string field = string.Create(CultureInfo.InvariantCulture, $"Data.At<{t}>({area}, 0x{o.Value:x})");
                CheckPlace(area, worker, o.Value, Size(type), left);
                return (area, o.Value, type, field);
            }
            // A vanilla variable or an element of one.
            (int var, int index, string name) = reader.ReadExpr(left) switch {
                VarExpr v => (v.Var, 0, VariableName(v.Var)),
                ElemExpr { Index: IntLit i } e => (e.Var, i.Value, $"{VariableName(e.Var)}[{i.Value}]"),
                Elem2Expr { Row: IntLit r, Col: IntLit c } e when c.Value >= 0 && c.Value < e.RowLength => (e.Var, r.Value * e.RowLength + c.Value, $"{VariableName(e.Var)}[{r.Value}, {c.Value}]"),
                ElemExpr or Elem2Expr => throw CompileException.At(left, "an element by constant indices, inside the array"),
                _ => throw CompileException.At(left, "a static constructor sets a variable of the script, an element of one, a field of MonsterStats or MonsterRewards, or Data.At<T>(…)"),
            };
            if (var >= vanillaVariables) throw CompileException.At(left, $"{VariableName(var)} is the mod's own variable: give it its initial value as an initializer (public static int {VariableName(var)} = 5;)");
            VarEntry entry = vanilla.Variables[var];
            if (index < 0 || index >= entry.Length) throw CompileException.At(left, $"{name}: {VariableName(var)} has {entry.Length} element(s)");
            DataEditArea where = entry.Storage switch {
                (int)Storage.Shared => DataEditArea.Shared,
                (int)Storage.Event => DataEditArea.Event,
                (int)Storage.Private => DataEditArea.Private,
                _ => throw CompileException.At(left, $"{VariableName(var)} is in storage {(Storage)entry.Storage}, which is not the script's data: its value is the save's or the game's"),
            };
            int offset = entry.Offset + index * entry.ElementSize;
            CheckPlace(where, worker, offset, entry.ElementSize, left);
            return (where, offset, entry.Type, name);
        }

        /// <summary>Whether an edit of <paramref name="size"/> bytes at <paramref name="offset"/> of an area is set where it belongs and lies inside it.</summary>
        private void CheckPlace(DataEditArea area, int? worker, int offset, int size, SyntaxNode at) {
            if (area == DataEditArea.Private) {
                if (worker is not int w) throw CompileException.At(at, "private data (storage 3) is each worker's own: set it in the worker's static constructor (static Ai() { … })");
                if (category != ScriptCategory.Event) {
                    throw CompileException.At(at, "in battle the game clears a worker's private data when it sets the worker up (work +0xb8), so an initial value there has no effect");
                }
                uint length = vanilla.Workers[w].PrivateLength;
                if (vanilla.Workers[w].Private is null || offset + size > length) {
                    throw CompileException.At(at, string.Create(CultureInfo.InvariantCulture, $"worker {w}'s private data is 0x{length:x} bytes; this is past it"));
                }
                return;
            }
            if (worker is not null) throw CompileException.At(at, "a worker's static constructor sets its private data; the script's data is set in the script class's");
            byte[]? bytes = area switch {
                DataEditArea.Shared => vanilla.Shared, DataEditArea.Other => vanilla.Other, DataEditArea.Event => vanilla.Event, DataEditArea.Area => vanilla.Area, _ => null,
            };
            if (bytes is null || offset + size > bytes.Length) {
                throw CompileException.At(at, string.Create(CultureInfo.InvariantCulture, $"{DataAreas.Describe(area, null)} is 0x{bytes?.Length ?? 0:x} bytes; this is past it"));
            }
        }

        private void Add(DataEditArea area, int? worker, int offset, byte[] bytes, string field, SyntaxNode at) {
            for (int i = 0; i < bytes.Length; i++) {
                if (_set.TryGetValue((area, worker, offset + i), out string? earlier)) throw CompileException.At(at, earlier == field ? $"{field} is set twice" : $"{field} overlaps {earlier}, set before");
            }
            for (int i = 0; i < bytes.Length; i++) _set[(area, worker, offset + i)] = field;
            (package.Data ??= []).Add(new DataEdit { Area = area, Worker = worker, Offset = offset, Bytes = bytes, Field = field });
        }

        private string VariableName(int var) => var < variableNames.Count ? variableNames[var] : $"variable {var}";
    }

    private static string? LastName(ExpressionSyntax e) => e switch {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        AliasQualifiedNameSyntax q => q.Name.Identifier.ValueText,
        _ => null,
    };

    private static int Size(int type) => type switch { 0 or 1 => 1, 2 or 3 => 2, _ => 4 };

    /// <summary>The array initializer of a field's initializer (<c>new int[] { … }</c>, <c>{ … }</c>); null when it has none.</summary>
    private static InitializerExpressionSyntax? ArrayInitializer(ExpressionSyntax? e) => e switch {
        ArrayCreationExpressionSyntax a => a.Initializer,
        ImplicitArrayCreationExpressionSyntax i => i.Initializer,
        InitializerExpressionSyntax i => i,
        _ => null,
    };

    /// <summary>A constant as the little-endian bytes of a variable type (0 <c>u8</c> … 6 <c>f32</c>); an integer fits the type's width.</summary>
    private static byte[] Encode(Expr value, int type, SyntaxNode at) {
        if (type == 6) {
            uint bits = value switch {
                FloatLit f => f.Bits,
                IntLit i => BitConverter.SingleToUInt32Bits(i.Value),
                _ => throw CompileException.At(at, "a number"),
            };
            byte[] b = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, bits);
            return b;
        }
        if (value is not IntLit lit) throw CompileException.At(at, "an integer variable takes an integer");
        int size = Size(type);
        long v = lit.Value;
        if (type == 4 && v < 0) v += 1L << 32;    // u32 from an int's bits
        long min = -(1L << (8 * size - 1)), max = (1L << (8 * size)) - 1;
        if (v < min || v > max) throw CompileException.At(at, string.Create(CultureInfo.InvariantCulture, $"{lit.Value} does not fit {size} byte(s)"));
        byte[] bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[i] = (byte)(v >> (8 * i));
        return bytes;
    }
}
