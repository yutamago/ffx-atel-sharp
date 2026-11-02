// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ffx.Atel.Linker;

// The compiled form of what one mod does to one script (docs/formats/mod-package.md). It holds the
// mod's own code and data only: vanilla code and data are referred to by worker, entry and
// variable, each with a hash of the vanilla part the mod was compiled against.

/// <summary>What one mod does to one script: <c>atel/&lt;archive path&gt;.json</c> in the mod's folder.</summary>
public sealed class ScriptPackage {
    /// <summary>The format this file is in; a linker refuses a newer one.</summary>
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;

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
    ///     Messages by index in the script's message table, for runtime injection only (docs/formats/debug.md,
    ///     "Runtime injection"): the loader sets them in the running event's table. The linker does not
    ///     read them; <c>Text.New</c> in mods comes later. Null when there are none.
    /// </summary>
    public List<PackageMessage>? Messages { get; set; }

    // Later: Data (per-field edits of data areas and tables).

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
}

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

    /// <summary>Bytes per element.</summary>
    [JsonIgnore]
    public int ElementSize => Type switch { 0 or 1 => 1, 2 or 3 => 2, _ => 4 };
}

/// <summary>A message for the script's table: <see cref="Index"/> in it, the text in the game's language (control codes as <c>atel text export</c> writes them).</summary>
public sealed record PackageMessage(int Index, string Text);

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
}

/// <summary>New code on a vanilla worker: a new entry, or a patch on one of its entry methods.</summary>
public sealed class EntryPatch {
    public PatchKind Kind { get; set; }

    /// <summary>The vanilla worker's number (one of <see cref="VanillaRefs.Workers"/>).</summary>
    public int Worker { get; set; }

    /// <summary>The mod's method.</summary>
    public string Name { get; set; } = "";

    /// <summary>The vanilla method it patches (one of <see cref="VanillaRefs.Entries"/>), null for <see cref="PatchKind.Entry"/>.</summary>
    public string? Target { get; set; }

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
}
