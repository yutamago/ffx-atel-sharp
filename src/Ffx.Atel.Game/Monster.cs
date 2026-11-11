// SPDX-License-Identifier: MIT

using Ffx.Atel;

namespace Ffx;

// A monster's data in its file (battle/mon/_mNNN/mNNN.bin), which a mod changes field by field in the
// script class's static constructor: `static M209() { MonsterStats.Hp = 4321; }` (docs/language.md,
// "Initial values and data edits"). The battle reads these from the monster's file, not from the kernel
// `monster` tables (ffx-decomp, docs/formats/monster.md: `Mon_AttachData` points the actor at the file's
// sections, `MsSetRamChr` copies the stat record into it). Only constant assignments compile; the
// properties are not readable in code.

/// <summary>
///     The monster's stats: HP, MP, strength and the rest. Set them in the script class's static constructor:
///     <c>static M209() { MonsterStats.Hp = 4321; }</c>. Only constant values work, and code cannot read them.
/// </summary>
/// <remarks>
///     The stat record is section 2 of the monster's file, with a copy in section 6, which the linker keeps
///     equal. Offsets and the actor fields they become: ffx-decomp's <c>docs/formats/monster.md</c>. The names of
///     <see cref="Agility"/>, <see cref="Luck"/>, <see cref="Evasion"/> and <see cref="Accuracy"/> are
///     Fahrenheit's (<c>MonStats</c>); FFX.exe's code for them is not read yet.
/// </remarks>
public static class MonsterStats {
    /// <summary>The monster's maximum HP.</summary>
    /// <remarks>In the file: <c>+0x14</c>, <c>u32</c>; the actor's <c>+0x594</c>.</remarks>
    public static int Hp { set => throw Runtime.NotRunnable(); }

    /// <summary>The monster's maximum MP.</summary>
    /// <remarks>In the file: <c>+0x18</c>, <c>u32</c>; the actor's <c>+0x598</c>.</remarks>
    public static int Mp { set => throw Runtime.NotRunnable(); }

    /// <summary>How much damage past 0 HP counts as an overkill.</summary>
    /// <remarks>In the file: <c>+0x1c</c>, <c>u32</c>.</remarks>
    public static int OverkillThreshold { set => throw Runtime.NotRunnable(); }

    /// <summary>Strength: how hard its physical attacks hit.</summary>
    /// <remarks>In the file: <c>+0x20</c>; cubed for physical damage (<c>MsCalcDamageCommand</c>).</remarks>
    public static byte Strength { set => throw Runtime.NotRunnable(); }

    /// <summary>Defense: how much it reduces physical damage.</summary>
    /// <remarks>In the file: <c>+0x21</c>.</remarks>
    public static byte Defense { set => throw Runtime.NotRunnable(); }

    /// <summary>Magic: how hard its spells hit.</summary>
    /// <remarks>In the file: <c>+0x22</c>.</remarks>
    public static byte Magic { set => throw Runtime.NotRunnable(); }

    /// <summary>Magic defense: how much it reduces magic damage.</summary>
    /// <remarks>In the file: <c>+0x23</c>.</remarks>
    public static byte MagicDefense { set => throw Runtime.NotRunnable(); }

    /// <summary>Agility: how soon its turns come. The game keeps it between 1 and 255.</summary>
    /// <remarks>In the file: <c>+0x24</c>; Fahrenheit's name.</remarks>
    public static byte Agility { set => throw Runtime.NotRunnable(); }

    /// <summary>Luck, 1 to 255.</summary>
    /// <remarks>In the file: <c>+0x25</c>; Fahrenheit's name; the game keeps it between 1 and 255.</remarks>
    public static byte Luck { set => throw Runtime.NotRunnable(); }

    /// <summary>Evasion, 0 to 255.</summary>
    /// <remarks>In the file: <c>+0x26</c>; Fahrenheit's name.</remarks>
    public static byte Evasion { set => throw Runtime.NotRunnable(); }

    /// <summary>Accuracy, 1 to 255.</summary>
    /// <remarks>In the file: <c>+0x27</c>; Fahrenheit's name; the game keeps it between 1 and 255.</remarks>
    public static byte Accuracy { set => throw Runtime.NotRunnable(); }
}

/// <summary>
///     What the party gets for beating the monster: gil, AP, and the chances of drops and steals. Set them in the
///     script class's static constructor: <c>static M209() { MonsterRewards.Gil = 999; }</c>.
/// </summary>
/// <remarks>Section 4 of the monster's file (<c>MsGetItem</c>, <c>Battle_Steal</c>).</remarks>
public static class MonsterRewards {
    /// <summary>The gil the party gets.</summary>
    /// <remarks>In the file: <c>+0x00</c>, <c>u16</c>.</remarks>
    public static ushort Gil { set => throw Runtime.NotRunnable(); }

    /// <summary>The AP the party's characters get.</summary>
    /// <remarks>In the file: <c>+0x02</c>, <c>u16</c>.</remarks>
    public static ushort Ap { set => throw Runtime.NotRunnable(); }

    /// <summary>The AP after an overkill.</summary>
    /// <remarks>In the file: <c>+0x04</c>, <c>u16</c>.</remarks>
    public static ushort OverkillAp { set => throw Runtime.NotRunnable(); }

    /// <summary>The chance of the first item drop, out of 255.</summary>
    /// <remarks>In the file: <c>+0x08</c>.</remarks>
    public static byte DropChance1 { set => throw Runtime.NotRunnable(); }

    /// <summary>The chance of the second item drop, out of 255.</summary>
    /// <remarks>In the file: <c>+0x09</c>.</remarks>
    public static byte DropChance2 { set => throw Runtime.NotRunnable(); }

    /// <summary>The chance that a steal works, out of 255. The game halves it after each successful steal.</summary>
    /// <remarks>In the file: <c>+0x0a</c>.</remarks>
    public static byte StealChance { set => throw Runtime.NotRunnable(); }

    /// <summary>The chance of an equipment drop, out of 255.</summary>
    /// <remarks>In the file: <c>+0x0b</c>.</remarks>
    public static byte EquipmentDropChance { set => throw Runtime.NotRunnable(); }
}
