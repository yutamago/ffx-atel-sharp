// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Atel;

// The declarations of a script: which file it is, its workers and the parts of the script block
// the language has no construct for yet (docs/plan.md, "The language"; docs/formats/script-block.md).
// Hex data in these attributes is in the form of Ffx.Atel.Bytecode.Script.HexText: lines of
// `offset: bytes`, zero bytes left out.

/// <summary>The script class of an event (<c>event/obj/**/name.ebp</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventAttribute(string name) : Attribute {
    public string Name { get; } = name;
}

/// <summary>The script class of a monster (<c>battle/mon/_mNNN/mNNN.bin</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MonsterAttribute(string name) : Attribute {
    public string Name { get; } = name;
}

/// <summary>The script class of a battle script (<c>battle/btl/*/name.bin</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class BattleAttribute(string name) : Attribute {
    public string Name { get; } = name;
}

/// <summary>The script's workers in order: a worker's position is its number.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class WorkersAttribute(params Type[] workers) : Attribute {
    public Type[] Workers { get; } = workers;
}

/// <summary>
///     The script block header's strings and the words not yet understood (block <c>+0x14</c>,
///     <c>+0x18</c>, <c>+0x1c</c>, <c>+0x24</c>; Fahrenheit: <c>+0x18</c> main script, <c>+0x1e</c> zone bytes).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class HeaderAttribute : Attribute {
    /// <summary>The author string (<c>+0x08</c>), Latin-1.</summary>
    public string Author { get; set; } = "";

    /// <summary>The name string (<c>+0x0c</c>), Latin-1.</summary>
    public string Name { get; set; } = "";

    public uint Word14 { get; set; }
    public uint Word18 { get; set; }
    public uint Word1C { get; set; }
    public uint Word24 { get; set; }
}

/// <summary>
///     Places a region of the script block at an offset other than the layout rules give
///     (<c>"shared"</c>: the 32-byte code reserve some blocks have).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class PinAttribute(string region, int offset) : Attribute {
    public string Region { get; } = region;
    public int Offset { get; } = offset;
}

/// <summary>Bytes of the script block the layout does not produce, at a block offset (hex digits).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class PatchAttribute(int offset, string hex) : Attribute {
    public int Offset { get; } = offset;
    public string Hex { get; } = hex;
}

/// <summary>The integer constant pool (<c>pushc cN</c>), in pool order.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IntsAttribute(params int[] values) : Attribute {
    public int[] Values { get; } = values;
}

/// <summary>The float constant pool (<c>pushfc fN</c>), in pool order.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FloatsAttribute(params float[] values) : Attribute {
    public float[] Values { get; } = values;
}

/// <summary>The float constant pool as bit patterns, for pools a float literal cannot give exactly (NaN payloads).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FloatBitsAttribute(params uint[] values) : Attribute {
    public uint[] Values { get; } = values;
}

/// <summary>The storage area of a variable (location bits 25–27; names after Fahrenheit's <c>AtelScriptVarLocation</c>).</summary>
public enum Storage {
    /// <summary>0: the save game.</summary>
    Save = 0,
    /// <summary>1: variables common to all scripts.</summary>
    Common = 1,
    /// <summary>2: the worker's data (unused by the shipped scripts).</summary>
    Data = 2,
    /// <summary>3: the worker's private data.</summary>
    Private = 3,
    /// <summary>4: the script's shared data.</summary>
    Shared = 4,
    /// <summary>5: the integer registers (unused by the shipped scripts).</summary>
    Registers = 5,
    /// <summary>6: the event data.</summary>
    Event = 6,
    /// <summary>7: not used by the shipped scripts.</summary>
    Storage7 = 7,
}

/// <summary>
///     An entry of the variable table, in declaration order; the field's type gives the variable's
///     (<c>byte</c> u8, <c>sbyte</c> i8, <c>ushort</c> u16, <c>short</c> i16, <c>uint</c> u32, <c>int</c> i32,
///     <c>float</c> f32; an array for a length other than 1, its size the length:
///     <c>[Var(Storage.Event, 0x40)] public static float[] V3 = new float[4];</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class VarAttribute(Storage storage, int offset) : Attribute {
    public Storage Storage { get; } = storage;

    /// <summary>Offset in the storage area (location bits 0–23).</summary>
    public int Offset { get; } = offset;

    /// <summary>Array length (table <c>+4</c>), for an array field without <c>= new T[n]</c>.</summary>
    public int Length { get; set; } = 1;

    /// <summary>The table's <c>u16</c> at <c>+6</c>, not yet understood.</summary>
    public int Extra { get; set; }

    /// <summary>Location bit 24, not yet understood.</summary>
    public bool Bit24 { get; set; }
}

/// <summary>Worker header words not yet understood: <c>+0x0c</c>, and <c>+0x10</c> (Fahrenheit's <c>priv_data_len</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class WorkerAttribute : Attribute {
    public uint Word0C { get; set; }
    public uint PrivateLength { get; set; }
}

/// <summary>A worker method that is entry point <paramref name="number"/>; several when entries share code.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class EntryAttribute(int number) : Attribute {
    public int Number { get; } = number;
}

/// <summary>Raw bytes of a region of the script block, <paramref name="length"/> long, as hex lines.</summary>
public abstract class DataAttribute(int length, string hex) : Attribute {
    public int Length { get; } = length;
    public string Hex { get; } = hex;
}

/// <summary>Map entrances (block <c>+0x04</c>), 0x20 bytes each.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MapDataAttribute(int length, string hex) : DataAttribute(length, hex);

/// <summary>Shared data (storage 4), with what no variable explains yet.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SharedDataAttribute(int length, string hex) : DataAttribute(length, hex);

/// <summary>The 0x40-byte area at block <c>+0x2c</c> (Fahrenheit's <c>offset_other</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class OtherDataAttribute(int length, string hex) : DataAttribute(length, hex);

/// <summary>Event data (storage 6, block <c>+0x20</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventDataAttribute(int length, string hex) : DataAttribute(length, hex);

/// <summary>The area at block <c>+0x28</c> (Fahrenheit's <c>offset_area</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AreaDataAttribute(int length, string hex) : DataAttribute(length, hex);

/// <summary>A worker's private data block (storage 3, worker <c>+0x2c</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PrivateDataAttribute(int length, string hex) : DataAttribute(length, hex);
