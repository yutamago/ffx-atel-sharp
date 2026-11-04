// SPDX-License-Identifier: MIT

using static Ffx.Atel.Analyzers.Tests.Harness;

namespace Ffx.Atel.Analyzers.Tests;

/// <summary>ATL3001, ATL3002 (<c>call</c>) and ATL0001 (registers across a wait).</summary>
public sealed class CallAndRegisterTests {
    [Fact]
    public void DirectRecursion() => Verify("""
        public sealed class W00 : Worker1
        {
            [Entry(0)] void E0() { Again(); }
            void Again() { {|ATL3001:Again()|}; }
        }
        """);

    [Fact]
    public void RecursionThroughHelpersAndFunctionWorkers() => Verify("""
        public sealed class W00 : Worker1
        {
            [Entry(0)] void E0() { Call<W01>(); Shared.Twice(); }
        }
        public sealed class W01 : Worker0
        {
            [Entry(0)] void E0() { {|ATL3001:Call<W02>()|}; }
        }
        public sealed class W02 : Worker0
        {
            [Entry(0)] void E0() { {|ATL3001:Call<W01>()|}; }
        }
        public static class Shared
        {
            public static void Twice() { {|ATL3001:Back()|}; }
            static void Back() { {|ATL3001:Third()|}; }
            static void Third() { {|ATL3001:Twice()|}; }
        }
        """);

    [Fact]
    public void CallDepth() => Verify("""
        public sealed class W00 : Worker1
        {
            [Entry(0)] void E0() { {|ATL3002:A()|}; B(); }
            [Entry(1)] void E1() { {|ATL3002:Call<W01>()|}; }
            void A() { B(); }
            void B() { C(); }
            void C() { D(); }
            void D() { }
        }
        public sealed class W01 : Worker0
        {
            [Entry(0)] void E0() { Call<W02>(); }
        }
        public sealed class W02 : Worker0
        {
            [Entry(0)] void E0() { Call<W03>(); }
        }
        public sealed class W03 : Worker0
        {
            [Entry(0)] void E0() { Call<W04>(); }
        }
        public sealed class W04 : Worker0
        {
            [Entry(0)] void E0() { }
        }
        """);

    /// <summary>A member whose native polls (<c>Turn.Wait()</c>, <c>Wait.Frames(…)</c>) waits like the native.</summary>
    [Fact]
    public void MembersThatPollAreWaits() => Verify("""
        public sealed class W00 : Worker1
        {
            [Register(0)] public int I0;

            [Entry(1)]
            void E1()
            {
                I0 = Turn.GetKind();
                {|ATL0001:Turn.Wait()|};
                Motion = Turn.GetMotion(I0);
                {|ATL0001:Wait.Frames(1)|};
                Common.WaitFrames(I0);
            }

            [Entry(2)]
            void E2() { I0 = 5; }
        }
        """);

    [Fact]
    public void RegisterAcrossAWaitWrittenByAnotherEntry() => Verify("""
        public sealed class W00 : Worker1
        {
            [Register(0)] public int I0;
            [Register(1)] public int I1;
            [Register(2)] public int I2;

            [Entry(1)]
            void E1()
            {
                I0 = 3;
                {|ATL0001:Common.WaitFrames(1)|};
                Common.WaitFrames(I0);
                I1 = 2;
                Common.WaitFrames(1);
                I1 = 5;
                Common.WaitFrames(I1);
                while (true)
                {
                    I2 = I2 + 1;
                    {|ATL0001:Common.WaitFrames(1)|};
                }
            }

            [Entry(3)]
            void E3()
            {
                I0 = 1;
                I1 = 1;
                I2 = 0;
            }
        }
        """);

    [Fact]
    public void RegisterAcrossAWaitNobodyElseWrites() => Verify("""
        public sealed class W00 : Worker1
        {
            [Register(0)] public int I0;

            [Entry(1)]
            void E1()
            {
                I0 = 3;
                Common.WaitFrames(1);
                Common.WaitFrames(I0);
            }

            [Entry(3)]
            void E3()
            {
                Common.WaitFrames(I0);
            }
        }
        """);
}
