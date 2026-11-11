// SPDX-License-Identifier: MIT
// region usings
using Ffx;
using Ffx.Atel;
using static Monsters.M999.M999;

namespace Monsters.M999;
// endregion

// region script-class
[Monster("m999")]
[Workers(typeof(Ai), typeof(Cameras))]
public static partial class M999
{
    [Var(Storage.Shared, 0x0000)] public static int Turns;
}
// endregion

// The data areas: in decompiled source, M999.Data.cs.
[SharedData(0x10, "")]
public static partial class M999
{
}
