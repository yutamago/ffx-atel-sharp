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
///     The monster's stat record (section 2 of its file, and its copy in section 6, which the linker keeps
///     equal). Set a field in the script class's static constructor: <c>static M209() { MonsterStats.Hp = 4321; }</c>.
/// </summary>
/// <remarks>
///     Offsets and the actor fields they become: ffx-decomp's <c>docs/formats/monster.md</c>. The names of
///     <see cref="Agility"/>, <see cref="Luck"/>, <see cref="Evasion"/> and <see cref="Accuracy"/> are
///     Fahrenheit's (<c>MonStats</c>); FFX.exe's code for them is not read yet.
/// </remarks>
public static class MonsterStats {
    /// <summary>Maximum HP (<c>+0x14</c>, <c>u32</c>; the actor's <c>+0x594</c>).</summary>
    public static int Hp { set => throw Runtime.NotRunnable(); }

    /// <summary>Maximum MP (<c>+0x18</c>, <c>u32</c>; the actor's <c>+0x598</c>).</summary>
    public static int Mp { set => throw Runtime.NotRunnable(); }

    /// <summary>The damage past 0 HP that counts as an overkill (<c>+0x1c</c>, <c>u32</c>).</summary>
    public static int OverkillThreshold { set => throw Runtime.NotRunnable(); }

    /// <summary>Strength (<c>+0x20</c>), cubed for physical damage (<c>MsCalcDamageCommand</c>).</summary>
    public static byte Strength { set => throw Runtime.NotRunnable(); }

    /// <summary>Defense (<c>+0x21</c>), physical damage reduction.</summary>
    public static byte Defense { set => throw Runtime.NotRunnable(); }

    /// <summary>Magic (<c>+0x22</c>), magic damage.</summary>
    public static byte Magic { set => throw Runtime.NotRunnable(); }

    /// <summary>Magic defense (<c>+0x23</c>), magic damage reduction.</summary>
    public static byte MagicDefense { set => throw Runtime.NotRunnable(); }

    /// <summary>Agility (<c>+0x24</c>; Fahrenheit's name), clamped to 1-255 by the game.</summary>
    public static byte Agility { set => throw Runtime.NotRunnable(); }

    /// <summary>Luck (<c>+0x25</c>; Fahrenheit's name), clamped to 1-255.</summary>
    public static byte Luck { set => throw Runtime.NotRunnable(); }

    /// <summary>Evasion (<c>+0x26</c>; Fahrenheit's name), 0-255.</summary>
    public static byte Evasion { set => throw Runtime.NotRunnable(); }

    /// <summary>Accuracy (<c>+0x27</c>; Fahrenheit's name), clamped to 1-255.</summary>
    public static byte Accuracy { set => throw Runtime.NotRunnable(); }
}

/// <summary>
///     The monster's rewards (section 4 of its file; <c>MsGetItem</c>, <c>Battle_Steal</c>). Set a field in the
///     script class's static constructor: <c>static M209() { MonsterRewards.Gil = 999; }</c>.
/// </summary>
public static class MonsterRewards {
    /// <summary>Gil (<c>+0x00</c>, <c>u16</c>).</summary>
    public static ushort Gil { set => throw Runtime.NotRunnable(); }

    /// <summary>AP (<c>+0x02</c>, <c>u16</c>).</summary>
    public static ushort Ap { set => throw Runtime.NotRunnable(); }

    /// <summary>AP on an overkill (<c>+0x04</c>, <c>u16</c>).</summary>
    public static ushort OverkillAp { set => throw Runtime.NotRunnable(); }

    /// <summary>The chance of drop slot 1, out of 255 (<c>+0x08</c>).</summary>
    public static byte DropChance1 { set => throw Runtime.NotRunnable(); }

    /// <summary>The chance of drop slot 2, out of 255 (<c>+0x09</c>).</summary>
    public static byte DropChance2 { set => throw Runtime.NotRunnable(); }

    /// <summary>The steal chance, out of 255 (<c>+0x0a</c>); the game halves it after each successful steal.</summary>
    public static byte StealChance { set => throw Runtime.NotRunnable(); }

    /// <summary>The equipment drop chance, out of 255 (<c>+0x0b</c>).</summary>
    public static byte EquipmentDropChance { set => throw Runtime.NotRunnable(); }
}
