// SPDX-License-Identifier: MIT

using static Ffx.Atel.Analyzers.Tests.Harness;

namespace Ffx.Atel.Analyzers.Tests;

/// <summary>ATL1012 and ATL1013: the object-oriented surface's members are natives, and what holds them no object.</summary>
public sealed class MemberTests {
    private const string Monster = """
        [Monster("m997")]
        [Workers(typeof(Ai))]
        public static partial class M997
        {
        }

        """;

    [Fact]
    public void MembersAreReadAndAssigned() => Verify(Monster + """
        public sealed partial class Ai : BattleWorker
        {
            [Register(0)] public static int I0;

            protected override void Init()
            {
                Actors[Target.AllPly].Hp = 0;
                Actors[Target.Own].Hp = Actors[Target.Own].Hp + 10;
                I0 = Self.Mp;
                Self.Hp = I0;
                if (Actors[(Target)I0].Alive == 1) Turn.Start(0, 4099, 0);
                Workers[I0].Turn.Stop();
                Motion = 0x71050;
            }
        }
        """);

    [Fact]
    public void CompoundAssignmentIsTwoNatives() => Verify(Monster + """
        public sealed partial class Ai : BattleWorker
        {
            protected override void Init()
            {
                {|ATL1012:Actors[Target.Own].Hp += 10|};
                {|ATL1012:Self.Mp++|};
                {|ATL1012:Yaw -= 1f|};
            }
        }
        """);

    [Fact]
    public void AHolderIsNoValue() => Verify(Monster + """
        public sealed partial class Ai : BattleWorker
        {
            protected override void Init()
            {
                var party = {|ATL1013:Actors[Target.AllPly]|};
                var own = {|ATL1013:Self|};
                var turn = {|ATL1013:Turn|};
            }
        }
        """);
}
