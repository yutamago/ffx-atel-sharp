// SPDX-License-Identifier: MIT

using static Ffx.Atel.Analyzers.Tests.Harness;

namespace Ffx.Atel.Analyzers.Tests;

/// <summary>A mod's <c>[Saved]</c> fields: ATL4003 (one that cannot be saved) and ATL4004 (a write to another mod's).</summary>
public sealed class SavedTests {
    private const string Monster = """
        [Monster("m997")]
        [Workers(typeof(Larva))]
        public static partial class M997
        {
            [Var(Storage.Shared, 0x0000)] public static int Hp;
        }

        public sealed partial class Larva : BattleWorker
        {
            protected override void Init() { return; }
        }
        """;

    [Fact]
    public void SavedFieldsAreUsedAsVariables() => Verify(Monster + """

        public static class Progress
        {
            [Saved] public static ushort LarvasMet;
            [Saved] public static byte Tier = 1;
            [Saved] public static byte[] Flags = new byte[8];
            [Saved] public static int[,] Grid = new int[2, 3];
            [Saved(Key = "Met")] public static int Seen;
        }

        public static class Others
        {
            [Saved(Mod = "yuta.other", Key = "Other.Stats.Kills")] public static int Kills;
            [Saved(Mod = "yuta.other", Key = "Other.Stats.Seen")] public static byte[] Seen = new byte[4];
        }

        public static partial class M997
        {
            [Saved] public static short Wins = 3;
        }

        public sealed partial class Larva
        {
            [Postfix(nameof(Init))]
            void Count()
            {
                Progress.LarvasMet += 1;
                if (Progress.LarvasMet >= 10) Progress.Flags[0] |= 1;
                M997.Wins = (short)(M997.Wins + 1);
                M997.Hp = Others.Kills + Others.Seen[2];
            }
        }
        """);

    [Fact]
    public void AFieldThatCannotBeSavedIsReported() => Verify(Monster + """

        public static class Progress
        {
            [Saved] public static ushort A;
            [Saved] public static {|ATL1009:long|} {|ATL4003:Text|};
            [Saved] public static readonly int {|ATL4003:Fixed|};
            [Saved] public static byte[] {|ATL4003:NoSize|};
            [Saved, Var(Storage.Save, 0x10)] public static byte {|ATL4003:Placed|};
            [Saved(Key = "Progress.A")] public static ushort {|ATL4003:SameKey|};
            [Saved(Key = "a/b")] public static ushort {|ATL4003:Slash|};
            [Saved(Mod = "yuta.other")] public static ushort {|ATL4003:NoKey|};
            [Saved(Mod = "yuta.other", Key = "X.Y")] public static ushort {|ATL4003:Initialized|} = 4;
        }

        public class NotStatic
        {
            [Saved] public static ushort {|ATL4003:B|};
        }

        public sealed partial class Larva
        {
            [Saved] public static ushort {|ATL4003:InAWorker|};
        }
        """);

    [Fact]
    public void AnotherModsFieldIsOnlyRead() => Verify(Monster + """

        public static class Others
        {
            [Saved(Mod = "yuta.other", Key = "Other.Stats.Kills")] public static int Kills;
            [Saved(Mod = "yuta.other", Key = "Other.Stats.Seen")] public static byte[] Seen = new byte[4];
        }

        public sealed partial class Larva
        {
            [Postfix(nameof(Init))]
            void Count()
            {
                M997.Hp = Others.Kills;
                {|ATL4004:Others.Kills = 5|};
                {|ATL4004:Others.Kills++|};
                {|ATL4004:Others.Seen[1] |= 2|};
            }
        }
        """);
}
