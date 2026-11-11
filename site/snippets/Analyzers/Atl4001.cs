// SPDX-License-Identifier: MIT
#pragma warning disable ATL4001 // the bad example: SnippetTests checks that it raises ATL4001
// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

namespace Snippets.Atl4001.Bad
{
    [Monster("m400")]
    [Workers(typeof(Ai))]
    public static partial class M400
    {
        [Var(Storage.Shared, 0x0000)] public static int Rage;
        public static int Turns = 3;
    }

    public sealed partial class Ai : BattleWorker
    {
    }

    // region bad
    public static partial class M400
    {
        static M400()
        {
            MonsterStats.Hp = 4321;
            Rage = Turns;                                   // ATL4001: not a constant
            Btl.SetActorProperty(ActorProperty.Hp, 1);      // ATL4001: a call, not an initial value
        }
    }
    // endregion
}

namespace Snippets.Atl4001.Fix
{
    [Monster("m401")]
    [Workers(typeof(Ai))]
    public static partial class M401
    {
        [Var(Storage.Shared, 0x0000)] public static int Rage;
    }

    // region fix
    public static partial class M401
    {
        static M401()
        {
            MonsterStats.Hp = 4321;     // the monster's HP when the battle starts
            Rage = 3;                   // a constant
        }
    }

    public sealed partial class Ai : BattleWorker
    {
        [Postfix(nameof(Init))]
        void Weaker()
        {
            Btl.SetActorProperty(ActorProperty.Hp, 1);      // code runs in a patch
        }
    }
    // endregion
}
