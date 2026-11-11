// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Events.Test.Test0600.Test0600;

namespace Events.Test.Test0600;

[Event("test0600")]
[Workers(typeof(Rikku), typeof(Tidus))]
[SharedData(0x10, "")]
public static partial class Test0600
{
    [Var(Storage.Shared, 0x0000)] public static float Height;
}

public sealed partial class Rikku : Actor
{
    [Register(3)] public static int I3;

    protected override void Init()
    {
        return;
    }

    protected override void Main()
    {
        Wait.Forever();
        return;
    }

    [Entry(2)]
    public static async Task ArriveAsync()
    {
        // region natives
        Common.SetPos(4.6f, -19.1f, 587.2f);
        Common.SetYaw(-0.3f);
        Common.WaitFrames(30);
        // endregion

        // region members
        Pos = new Vector3(4.6f, -19.1f, 587.2f);
        Yaw = -0.3f;
        Wait.Frames(30);
        // endregion

        // region components
        Turn.To(1.57f);                         // turn, then wait until done
        Move.Target = new Vector3(10f, -19.1f, 600f);
        Motion.Play(0x71050, 0, 1, 0, -1);
        Motion.Wait();
        Neck.Target = 1;                        // look at worker 1
        // endregion

        // region other-worker
        Height = Tidus.Pos.Y;                   // read another worker's position
        Tidus.Move.Stop();
        Workers[1].Turn.Stop();                 // the same worker by number
        // endregion

        // region engine-wide
        Camera.Eye.Shake(ShakeKind.SineFadeOut, 7, 0.5f, 30, 4, 2);
        Sound.Play((Sfx)0x80000017);
        // endregion

        // region bools
        Hidden = true;
        Wait.Frames(10);
        Hidden = false;
        // endregion
        return;
    }
}

public sealed partial class Tidus : Actor
{
    protected override void Init()
    {
        return;
    }

    protected override void Main()
    {
        Wait.Forever();
        return;
    }
}
