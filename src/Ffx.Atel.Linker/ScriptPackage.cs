// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ffx.Atel.Linker;

// The compiled form of what one mod does to one script (docs/formats/mod-package.md). It holds the
// mod's own code and data only: vanilla code and data are referred to by worker, entry and
// variable, each with a hash of the vanilla part the mod was compiled against.

/// <summary>What one mod does to one script: <c>atel/&lt;archive path&gt;.json</c> in the mod's folder.</summary>
public sealed class ScriptPackage {
    /// <summary>The newest format this linker reads; it refuses a newer one.</summary>
    public const int CurrentFormat = 3;

    /// <summary>The format of a package with <see cref="Cuts"/>: a linker that does not know them must refuse it, not drop them.</summary>
    public const int CutsFormat = 2;

    /// <summary>
    ///     The format of a package with <see cref="Data"/>, an <see cref="PatchKind.Inject"/> patch or a new variable's
    ///     <see cref="NewVariable.Initial"/> value: an older linker would drop them.
    /// </summary>
    public const int DataFormat = 3;

    /// <summary>The format of a package without what later formats added.</summary>
    public const int BaseFormat = 1;

    /// <summary>The format this file is in; a linker refuses a newer one.</summary>
    public int Format { get; set; } = BaseFormat;

    /// <summary>The file the script is in, as an archive path (<c>ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin</c>).</summary>
    public string File { get; set; } = "";

    /// <summary>The script's name (<c>m209</c>), for messages.</summary>
    public string Script { get; set; } = "";

    /// <summary>The vanilla parts the package attaches to or uses, with their hashes.</summary>
    public VanillaRefs Vanilla { get; set; } = new();

    /// <summary>New variables, in the order the mod declares them (<see cref="RelocKind.NewVar"/> indexes this list).</summary>
    public List<NewVariable> Variables { get; set; } = [];

    /// <summary>New workers (<see cref="RelocKind.NewWorker"/> indexes this list).</summary>
    public List<NewWorker> Workers { get; set; } = [];

    /// <summary>New entries and patches on vanilla entries (<see cref="RelocKind.NewEntry"/> indexes this list).</summary>
    public List<EntryPatch> Patches { get; set; } = [];

    /// <summary>
    ///     The texts of the package's <c>Debug.Print</c> calls (docs/formats/debug.md); the code pushes each
    ///     one's <see cref="DebugText.Id"/>. Null when there are none.
    /// </summary>
    public List<string>? DebugStrings { get; set; }

    /// <summary>
    ///     Messages for the script's message tables. Without an index, a new message (<c>Text.New</c>): the
    ///     linker gives it the next free index and the loader adds it to every language's table
    ///     (<see cref="RelocKind.NewMessage"/> indexes this list). With one, for runtime injection
    ///     (docs/formats/debug.md, "Runtime injection"): the loader sets it in the running event's table.
    ///     Null when there are none.
    /// </summary>
    public List<PackageMessage>? Messages { get; set; }

    /// <summary>
    ///     Dead blocks the mod switches on (<c>[assembly: EnableCut(nameof(Cut.…))]</c>; docs/research/dead-code.md,
    ///     "Switching a block on"): the linker turns each block's <c>jmp</c> into <c>nop_76</c> with the same
    ///     operand, so the block runs. Null when there are none; a package with cuts has format
    ///     <see cref="CutsFormat"/>.
    /// </summary>
    public List<CutSwitch>? Cuts { get; set; }

    /// <summary>
    ///     Per-field edits of the script's data (initial values in its data areas) and of the monster's data in its file
    ///     (stats, rewards), from the mod's static constructors; the linker writes them in place. Null when there are
    ///     none; a package with them has format <see cref="DataFormat"/>.
    /// </summary>
    public List<DataEdit>? Data { get; set; }

    /// <summary>The format this package needs: the lowest that holds everything in it.</summary>
    public int NeededFormat() =>
        Data is { Count: > 0 } || Patches.Any(p => p.Kind == PatchKind.Inject) || Variables.Any(v => v.Initial is not null) ? DataFormat
        : Cuts is { Count: > 0 } ? CutsFormat
        : BaseFormat;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web) {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <exception cref="JsonException">Not a package.</exception>
    public static ScriptPackage FromJson(string json) =>
        JsonSerializer.Deserialize<ScriptPackage>(json, JsonOptions) ?? throw new JsonException("an empty package");
}

/// <summary>The vanilla parts a package refers to.</summary>
public sealed class VanillaRefs {
    public List<WorkerRef> Workers { get; set; } = [];
    public List<EntryRef> Entries { get; set; } = [];
    public List<VariableRef> Variables { get; set; } = [];

    /// <summary>The data areas the package's <see cref="ScriptPackage.Data"/> edits, each with a hash of its vanilla bytes; null when there are none.</summary>
    public List<DataRef>? Data { get; set; }
}

/// <summary>A data area of the script (<see cref="Worker"/> for a worker's private data) and the hash of its vanilla bytes (<see cref="VanillaHash.Data"/>).</summary>
public sealed record DataRef(DataEditArea Area, int? Worker, string Hash);

/// <summary>Where a <see cref="DataEdit"/> writes (docs/formats/mod-package.md, "Data edits").</summary>
public enum DataEditArea {
    /// <summary>The shared data (storage 4); offsets from its start, as a shared variable's.</summary>
    Shared,
    /// <summary>The "other" area (block <c>+0x2c</c>).</summary>
    Other,
    /// <summary>The event data (storage 6).</summary>
    Event,
    /// <summary>The area data (block <c>+0x28</c>).</summary>
    Area,
    /// <summary>A worker's private data block (storage 3, header <c>+0x2c</c>); the edit names the worker.</summary>
    Private,
    /// <summary>A monster file's stat record: section 2, and its copy in section 6, which get the same bytes.</summary>
    MonsterStats,
    /// <summary>A monster file's rewards (section 4).</summary>
    MonsterRewards,
}

/// <summary>
///     Bytes a mod writes at <see cref="Offset"/> of a data area, in place: a variable's initial value, an element of
///     a table, a monster's stat. <see cref="Field"/> names it for the report (<c>V7</c>, <c>Table[3]</c>,
///     <c>MonsterStats.Hp</c>).
/// </summary>
public sealed class DataEdit {
    public DataEditArea Area { get; set; }

    /// <summary>The vanilla worker whose private data it is (<see cref="DataEditArea.Private"/>); else null.</summary>
    public int? Worker { get; set; }

    public int Offset { get; set; }
    public byte[] Bytes { get; set; } = [];
    public string Field { get; set; } = "";
}

/// <summary>
///     A named dead block a mod switches on in this script: its name (a constant of <c>Ffx.Cut</c>), the signatures
///     of its code (<c>data/cut-names.toml</c>, <see cref="Bytecode.Script.DeadSignature"/>) and how many blocks
///     with them the script has. The linker finds the blocks by signature and refuses the package when it finds
///     another number.
/// </summary>
public sealed record CutSwitch(string Name, string[] Signatures, int Blocks);

/// <summary>A vanilla worker by number; <see cref="Hash"/> covers its type, code, entries and labels (<see cref="VanillaHash.Worker"/>).</summary>
public sealed record WorkerRef(int Index, string Name, string Hash);

/// <summary>
///     A vanilla entry method: the entries it starts (<see cref="Entries"/>, those its <c>[Entry]</c>
///     attributes or its <c>Init</c>/<c>Main</c> override give) and its code, <see cref="Start"/> to
///     <see cref="End"/> in the worker's code, which the hash covers (<see cref="VanillaHash.Range"/>).
/// </summary>
public sealed record EntryRef(int Worker, string Name, int[] Entries, uint Start, uint End, string Hash);

/// <summary>A vanilla variable by its table index; the hash covers its table entry (<see cref="VanillaHash.Variable"/>).</summary>
public sealed record VariableRef(int Index, string Name, string Hash);

/// <summary>A variable the mod adds.</summary>
public sealed class NewVariable {
    public string Name { get; set; } = "";

    /// <summary>The variable type (location bits 28-31): 0 <c>u8</c>, 1 <c>i8</c>, 2 <c>u16</c>, 3 <c>i16</c>, 4 <c>u32</c>, 5 <c>i32</c>, 6 <c>f32</c>.</summary>
    public int Type { get; set; }

    /// <summary>The storage area (location bits 25-27).</summary>
    public int Storage { get; set; } = 4;

    /// <summary>
    ///     A fixed offset in the storage area (a save or common variable the mod names); null when
    ///     the linker allocates it (storage 4, shared, or 6, event).
    /// </summary>
    public int? Offset { get; set; }

    public int Length { get; set; } = 1;
    public int RowLength { get; set; }

    /// <summary>Declared as an array (location bit 24).</summary>
    public bool Array { get; set; }

    /// <summary>
    ///     The initial value (the field's initializer), little-endian, at most <c>Length × ElementSize</c> bytes; the rest
    ///     are zeros. Null for zeros. Only for a variable the linker allocates in the shared or event data.
    /// </summary>
    public byte[]? Initial { get; set; }

    /// <summary>Bytes per element.</summary>
    [JsonIgnore]
    public int ElementSize => Type switch { 0 or 1 => 1, 2 or 3 => 2, _ => 4 };
}

/// <summary>
///     A message for the script's table, its text written as <c>atel text export</c> writes them (control codes
///     in braces). <see cref="Index"/> null: a new message (<c>Text.New</c>) in the mod's source language,
///     whose index the linker gives. With an index: the text, in the game's language, for that index
///     (runtime injection).
/// </summary>
public sealed record PackageMessage(int? Index, string Text);

/// <summary>A worker the mod adds; its entry numbers are its own, its number is given when linking.</summary>
public sealed class NewWorker {
    public string Name { get; set; } = "";
    public ushort Type { get; set; }
    public uint Word0C { get; set; }

    /// <summary>The code with its labels and relocations; <see cref="Entries"/> are offsets into it.</summary>
    public Fragment Code { get; set; } = new();

    public uint[] Entries { get; set; } = [];
}

/// <summary>What an <see cref="EntryPatch"/> does.</summary>
public enum PatchKind {
    /// <summary>A new entry of a vanilla worker; it gets the next free number.</summary>
    Entry,
    /// <summary>Replaces the target's code (<c>[Replace]</c>).</summary>
    Replace,
    /// <summary>Runs before the target (<c>[Prefix]</c>); a <c>return;</c> in it ends the entry, so the target does not run.</summary>
    Prefix,
    /// <summary>Runs after the target, wherever it ends the entry (<c>[Postfix]</c>).</summary>
    Postfix,
    /// <summary>Runs at a point inside the target (<c>[Inject]</c>, <see cref="EntryPatch.At"/>); it runs off its end into what follows.</summary>
    Inject,
}

/// <summary>Before or after the anchor statement of an <see cref="InjectPoint"/>.</summary>
public enum InjectPosition {
    Before,
    After,
}

/// <summary>
///     Where an <c>[Inject]</c> patch runs: before or after the statement whose code is <see cref="Start"/> to
///     <see cref="End"/> of the target method (offsets from the method's start, inside the code its hash covers).
///     <see cref="Anchor"/> says which statement in words, for the report (<c>before the 2nd call to Btl.UseCommand</c>).
/// </summary>
/// <remarks>
///     Jumps from inside the statement to its start or end stay inside it: before-code runs as the statement is
///     entered from outside, after-code as it is left by its end (falling off it, or a jump from inside to its end).
/// </remarks>
public sealed record InjectPoint(InjectPosition Position, uint Start, uint End, string Anchor);

/// <summary>New code on a vanilla worker: a new entry, or a patch on one of its entry methods.</summary>
public sealed class EntryPatch {
    public PatchKind Kind { get; set; }

    /// <summary>The vanilla worker's number (one of <see cref="VanillaRefs.Workers"/>).</summary>
    public int Worker { get; set; }

    /// <summary>The mod's method.</summary>
    public string Name { get; set; } = "";

    /// <summary>The vanilla method it patches (one of <see cref="VanillaRefs.Entries"/>), null for <see cref="PatchKind.Entry"/>.</summary>
    public string? Target { get; set; }

    /// <summary>Where an <see cref="PatchKind.Inject"/> patch runs; null for the other kinds.</summary>
    public InjectPoint? At { get; set; }

    /// <summary>
    ///     The code. Entries and replacements end in <c>end</c> (<c>ret</c> in a function worker);
    ///     prefixes and postfixes run off their end into what follows.
    /// </summary>
    public Fragment Code { get; set; } = new();
}

/// <summary>Code with its own labels, and the operands the linker fills in.</summary>
public sealed class Fragment {
    /// <summary>The instructions; relocated operands are 0.</summary>
    public byte[] Bytes { get; set; } = [];

    /// <summary>The fragment's jump labels, as offsets into <see cref="Bytes"/>; jumps use their index.</summary>
    public uint[] Labels { get; set; } = [];

    public List<Reloc> Relocs { get; set; } = [];
}

/// <summary>An operand the linker fills in: the <c>u16</c> after the opcode at <see cref="At"/>.</summary>
public sealed record Reloc(int At, RelocKind Kind, uint Value);

public enum RelocKind {
    /// <summary>A jump to the fragment's label <c>Value</c>.</summary>
    Label,
    /// <summary><c>pushc</c> of the integer <c>Value</c>: its index in the integer pool, added if missing.</summary>
    Int,
    /// <summary><c>pushfc</c> of the float with bits <c>Value</c>: its index in the float pool.</summary>
    Float,
    /// <summary>Vanilla variable <c>Value</c> (table index).</summary>
    Var,
    /// <summary>The package's variable <c>Value</c>.</summary>
    NewVar,
    /// <summary>Vanilla worker <c>Value</c>: <c>call</c>, or <c>pushimm</c> of its number.</summary>
    Worker,
    /// <summary>The package's worker <c>Value</c>: <c>call</c>, or <c>pushimm</c> of its number.</summary>
    NewWorker,
    /// <summary><c>pushimm</c> of the number of the package's patch <c>Value</c> (a <see cref="PatchKind.Entry"/>).</summary>
    NewEntry,
    /// <summary><c>pushimm</c> of the index the package's message <c>Value</c> (one without an index) gets in the script's tables.</summary>
    NewMessage,
}
