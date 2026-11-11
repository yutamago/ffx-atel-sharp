// SPDX-License-Identifier: MIT
#pragma warning disable ATL4002 // the bad example: SnippetTests checks that it raises ATL4002
using Ffx;
using Ffx.Atel;
using static Snippets.Atl4002.Test4002;

namespace Snippets.Atl4002;

[Monster("m4002")]
[Workers(typeof(Ai))]
public static partial class Test4002
{
    public static int Rage;
}

// The vanilla method.
public sealed partial class Ai : BattleWorker
{
    [Entry(2)]
    void OnTurn()
    {
        Btl.UseCommand(Target.AllPly, Command.Attack);
        Btl.UseCommand(Target.AllPly, Command.Attack);
        return;
    }
}

public sealed partial class Ai
{
    // region bad
    [Inject(nameof(OnTurn), After = nameof(Btl.UseCommand), Occurrence = 3)]    // ATL4002
    void Tired()
    {
        Rage = 0;
    }
    // endregion

    // region fix
    [Inject(nameof(OnTurn), After = nameof(Btl.UseCommand), Occurrence = 2)]
    void TiredAfterTheSecond()
    {
        Rage = 0;
    }
    // endregion
}
