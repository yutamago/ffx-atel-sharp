// SPDX-License-Identifier: MIT

using static Ffx.Atel.Analyzers.Tests.Harness;

namespace Ffx.Atel.Analyzers.Tests;

/// <summary>What a mod writes on top of vanilla: ATL4001 (a static constructor that is not initial values).</summary>
public sealed class ModTests {
    private const string Monster = """
        [Monster("m997")]
        [Workers(typeof(Larva))]
        public static partial class M997
        {
            [Var(Storage.Shared, 0x0000)] public static int Hp;
            [Var(Storage.Shared, 0x0004)] public static byte[] Table = new byte[4];
            [Var(Storage.Private, 0x0000)] public static int Mine;
            [Var(Storage.Save, 0x0010)] public static ushort Progress;
            public static int Turns = 5;
        }

        public sealed partial class Larva : BattleWorker
        {
            protected override void Init() { return; }
        }
        """;

    [Fact]
    public void InitialValuesAreConstants() => Verify(Monster + """

        public static partial class M997
        {
            static M997()
            {
                Hp = 77;
                Table[2] = 9;
                MonsterStats.Hp = 4321;
                MonsterRewards.Gil = 999;
                Data.At<ushort>(DataArea.Other, 0x20) = 7;
                Hp = {|ATL4001:Turns|};
                {|ATL4001:Table[Turns]|} = 1;
                {|ATL4001:Turns|} = 6;
                {|ATL4001:Progress|} = 3;
                {|ATL4001:Mine|} = 1;
                {|ATL4001:Btl.SetActorProperty(ActorProperty.Hp, 1);|}
            }
        }
        """);

    [Fact]
    public void AMonstersWorkerHasNoInitialPrivateData() => Verify(Monster + """

        public sealed partial class Larva
        {
            static Larva()
            {
                {|ATL4001:Mine|} = 1;
                {|ATL4001:Hp|} = 2;
                {|ATL4001:MonsterStats.Hp|} = 2;
            }
        }
        """, "using static M997;\n");

    [Fact]
    public void AnEventWorkerSetsItsPrivateData() => Verify("""
        [Event("zz00")]
        [Workers(typeof(Boy))]
        public static partial class Zz00
        {
            [Var(Storage.Private, 0x0004)] public static short Steps;
            [Var(Storage.Event, 0x0000)] public static int Flag;
            static Zz00() { Flag = -2; {|ATL4001:MonsterStats.Hp|} = 1; }
        }

        public sealed partial class Boy : Actor
        {
            static Boy() { Steps = 12; Data.At<byte>(DataArea.Private, 0) = 1; }
            protected override void Init() { return; }
        }
        """, "using static Zz00;\n");
}
