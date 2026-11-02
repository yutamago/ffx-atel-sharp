// SPDX-License-Identifier: MIT

using static Ffx.Atel.Analyzers.Tests.Harness;

namespace Ffx.Atel.Analyzers.Tests;

/// <summary>ATL2001-ATL2007: requests between workers.</summary>
public sealed class RequestTests {
    private const string Script = """
        [Event("test")]
        [Workers(typeof(W00), typeof(W01))]
        public static partial class Test { }

        """;

    [Fact]
    public void RequestNeitherAwaitedNorDiscarded() => Verify(Script + """
        public sealed class W00 : Worker1
        {
            public static W01 Lulu = null!;
            async Task E2Async()
            {
                {|ATL2001:Lulu.LookAsync()|};
                _ = Lulu.LookAsync();
                await Lulu.LookAsync();
                await Lulu.LookAsync().Finished;
                await Lulu.LookAsync().Started;
                int n = await Lulu.CountAsync();
            }
        }
        public sealed class W01 : Worker1
        {
            public async Task LookAsync() { }
            public async Task<int> CountAsync() => 1;
        }
        """, "", "CS1998");

    [Fact]
    public void RequestToAnEntryThatIsNotAsync() => Verify(Script + """
        public sealed class W00 : Worker1
        {
            public static W01 Lulu = null!;
            [Entry(2)]
            void E2()
            {
                {|ATL2002:Lulu.E3()|};
                {|ATL2002:E4()|};
                Helper();
            }
            [Entry(4)] public void E4() { }
            void Helper() { }
        }
        public sealed class W01 : Worker1
        {
            [Entry(3)] public void E3() { }
        }
        """);

    [Fact]
    public void AwaitOnSomethingThatIsNotARequest() => Verify(Script + """
        public sealed class W00 : Worker1
        {
            static Awaitable Soon => default;
            async Task E2Async()
            {
                await {|ATL2003:Soon|};
            }
        }
        public sealed class W01 : Worker1 { }
        """, "", "CS1998");

    /// <summary>Entry 0 (Init) runs at level 7, entry 1 (Main) at 0: an awaited request to the worker's own entry at or below that waits for ever.</summary>
    [Fact]
    public void AwaitedSelfRequestAtTheSameOrALowerLevel() => Verify(Script + """
        public sealed class W00 : Worker1
        {
            [Entry(0)]
            void E0()
            {
                {|ATL2004:RequestWaitEnd(7, 0, 3)|};
                {|ATL2004:RequestWaitStart(3, 0, 3)|};
                RequestWaitEnd(3, 1, 3);
                Request(3, 0, 3);
            }
            [Entry(1)]
            void E1()
            {
                RequestWaitEnd(1, 0, 3);
                RequestWaitEnd(7, 0, 3);
            }
            [Entry(2)]
            void E2()
            {
                RequestWaitEnd(1, 0, 3);
                Entry(0);
                {|ATL2004:RequestWaitEnd(1, 0, 3)|};
            }
        }
        public sealed class W01 : Worker1 { }
        """);

    [Fact]
    public void AwaitedSelfRequest() => Verify(Script + """
        public sealed class W00 : Worker1
        {
            public static W01 Lulu = null!;
            async Task E2Async()
            {
                {|ATL2005:await E3Async()|};
                {|ATL2005:await E3Async().Finished|};
                _ = E3Async();
                await Lulu.LookAsync();
            }
            async Task E3Async() { }
        }
        public sealed class W01 : Worker1
        {
            public async Task LookAsync() { }
        }
        """, "", "CS1998");

    [Fact]
    public void PrioritiesTheEngineUses() => Verify(Script + """
        public sealed class W00 : Worker1
        {
            [Entry(2)]
            void E2()
            {
                Request({|ATL2006:0|}, 1, 3);
                RequestWaitEnd({|ATL2006:8|}, 1, 3);
                Request(1, 1, 3);
                Request(7, 1, 3);
                RequestParty({|ATL2006:9|}, 0, 3);
            }
        }
        public sealed class W01 : Worker1 { }
        """);

    [Fact]
    public void AsyncWithoutAtelTask() => Verify(Script + """
        public sealed class W00 : Worker1
        {
            async void {|ATL2007:E2Async|}() { }
        }
        public sealed class W01 : Worker1 { }
        """, "", "CS1998");

    /// <summary>An entry the engine runs may be <c>async void</c> to await requests (the decompiler writes them so).</summary>
    [Fact]
    public void AnEngineRunEntryMayBeAsyncVoid() => Verify(Script + """
        public sealed class W00 : Worker1
        {
            [Entry(2)]
            async void E2() { await W01.E3Async(); }
        }
        public sealed class W01 : Worker1
        {
            [Entry(3)]
            public static async Task E3Async() { }
        }
        """, "", "CS1998");
}
