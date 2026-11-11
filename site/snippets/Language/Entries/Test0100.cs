// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;
using static Events.Test.Test0100.Test0100;

namespace Events.Test.Test0100;

[Event("test0100")]
[Workers(typeof(Villager), typeof(Director), typeof(SetDefaults))]
[SharedData(0x10, "")]
public static partial class Test0100
{
    [Var(Storage.Shared, 0x0000)] public static int Waves;
}

// region entries
public sealed partial class Villager : Actor
{
    // Entry 0: the engine runs it once, when the worker starts.
    protected override void Init()
    {
        Model = Model.Wakk;
        return;
    }

    // Entry 1: the worker's main entry.
    protected override void Main()
    {
        Wait.Forever();
        return;
    }

    // Entry 2: an actor runs it when the player talks to it.
    [Entry(2)]
    void OnTalk()
    {
        Dialogue.Say(Text[3]);
        return;
    }

    // Entry 3: other workers request it.
    [Entry(3)]
    public static async Task WaveAsync()
    {
        Waves = Waves + 1;
        Motion.Play(0x71050, 0, 1, 0, -1);
        Motion.Wait();
        return;
    }
}
// endregion

public sealed partial class Director : LogicWorker
{
    protected override void Init()
    {
        return;
    }

    // region async-main
    protected override async void Main()
    {
        await Villager.WaveAsync();
        Wait.Forever();
        return;
    }
    // endregion

    // region two-entries
    [Entry(2)]
    [Entry(3)]
    void Reset()
    {
        Waves = 0;
        return;
    }
    // endregion

    // region end-entry
    [Entry(4)]
    void Count()
    {
        if (Waves > 3)
        {
            return;     // the entry ends here
        }
        Waves = Waves + 1;
        // region call-function-worker
        SetDefaults.Run();
        // endregion
        return;
    }
    // endregion
}

// region function-worker
public sealed partial class SetDefaults : FunctionWorker
{
    public static void Run()
    {
        Waves = 0;
        return;
    }
}
// endregion
