// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Events.Test.Test0800.Test0800;

namespace Events.Test.Test0800;

[Event("test0800")]
[Workers(typeof(Boy))]
public static partial class Test0800
{
    [Var(Storage.Private, 0x0000)] public static short Steps;
}

[Worker(PrivateLength = 0x10)]
[PrivateData(0x10, "")]
public sealed partial class Boy : Actor
{
    protected override void Init()
    {
        return;
    }

    protected override void Main()
    {
        Steps += 1;
        return;
    }
}
