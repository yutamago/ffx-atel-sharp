// SPDX-License-Identifier: MIT

using static Ffx.Atel.Analyzers.Tests.Harness;

namespace Ffx.Atel.Analyzers.Tests;

/// <summary>Inline methods: ATL3003 (calls itself), ATL3004 (an argument with an effect), and how the other rules see a call.</summary>
public sealed class InlineTests {
    [Fact]
    public void AnInlineMethodCannotCallItself() => Verify("""
        public sealed class W00 : Worker1
        {
            [Entry(0)] void E0() { A(1); }
            [Inline] static void A(int n) { {|ATL3003:B(n)|}; }
            [Inline] static void B(int n) { {|ATL3003:A(n)|}; }
        }
        """);

    [Fact]
    public void AnArgumentIsAValue() => Verify("""
        public sealed class W00 : Worker1
        {
            [Register(0)] public static int I0;

            [Entry(0)]
            void E0()
            {
                Dialogue.Say(Text[12], window: I0, x: I0 * 2 + 1);
                Dialogue.Say(Text[12], window: {|ATL3004:Common.Random16()|});
                Dialogue.Say(Text[12], window: {|ATL3004:I0++|});
                Dialogue.Say(Text[12], y: {|ATL3004:I0 > 1 ? 224 : 448|});
            }
        }
        """);

    [Fact]
    public void AnInlineCallIsNoCall() => Verify("""
        public sealed class W00 : Worker1
        {
            // Inline methods add no call level: A, B and C are 3.
            [Entry(0)] void E0() { A(); Dialogue.Say(Text[1]); }
            void A() { B(); }
            void B() { C(); }
            void C() { Dialogue.Open(Text[1]); }
        }
        """);

    [Fact]
    public void AnInlineCallThatWaitsIsAWait() => Verify("""
        public sealed class W00 : Worker1
        {
            [Register(0)] public int I0;

            [Entry(1)]
            void E1()
            {
                I0 = 3;
                {|ATL0001:Dialogue.Say(Text[2])|};
                Common.WaitFrames(I0);
            }

            [Entry(3)]
            void E3()
            {
                I0 = 1;
            }
        }
        """);
}
