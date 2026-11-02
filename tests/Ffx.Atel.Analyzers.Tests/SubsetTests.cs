// SPDX-License-Identifier: MIT

using static Ffx.Atel.Analyzers.Tests.Harness;

namespace Ffx.Atel.Analyzers.Tests;

/// <summary>ATL1001-ATL1011: the C# that ATEL cannot run.</summary>
public sealed class SubsetTests {
    /// <summary>The shape the decompiler writes is Atel: nothing to report.</summary>
    [Fact]
    public void DecompiledShapeIsClean() => Verify(""""
        namespace Events.Bj.Test;

        [Event("test")]
        [Workers(typeof(W00), typeof(W01))]
        [Header(Author = "", Name = "test", Word14 = 0x00000002)]
        [Ints(1, 2, 70000)]
        [Floats(1f, 2.5f)]
        public static partial class Test
        {
            [Var(Storage.Private, 0x0000)] public static ushort[] V0 = new ushort[4];
            [Var(Storage.Save, 0x0a00)] public static byte[,] V1 = new byte[3, 8];
            [Var(Storage.Shared, 0x0010)] public static int V2;
        }

        public sealed class W00 : Worker1
        {
            [Register(0)] public int I0;
            [Register(0)] public float F0;

            [Entry(0)]
            void E0()
            {
                Asm("""
                    pushimm 1
                    """);
                Test.V2 = unchecked((int)0xffff0000u);
                for (I0 = 0; I0 < 4; I0++)
                {
                    Test.V0[I0] = (ushort)(Test.V0[I0] + 1);
                }
                if (AsBool(Common.WaitFrames(1)) & I0 == 4 | !(F0 > 1f))
                {
                    Call<W01>();
                }
                switch (RegB = Test.V2)
                {
                    case 1:
                        RequestWaitEnd(1, 1, 3);
                        goto case 2;
                    case 2:
                        Request(2, 1, 2);
                        break;
                    case >= 5 and <= 9:
                        break;
                }
                At(Test.V1, 9) = 1;
                Common.WaitFrames(Addr(ref Test.V0[0]));
                return;
            }
        }

        public sealed class W01 : Worker0
        {
            [Entry(0)]
            void E0()
            {
                Entry(1);
                return;
            }
        }

        [SharedData(0xe4, """
            0000: 00000000
            """)]
        static partial class Test
        {
        }
        """");

    [Fact]
    public void ObjectCreation() => Verify("""
        public sealed class Thing { }
        public struct Point { public int X; }
        public sealed class W00 : Worker1
        {
            static int[] Table = new int[3];
            static int[] Rows = { 1, 2 };
            void E0()
            {
                var t = {|ATL1001:new Thing()|};
                Point p = {|ATL1001:new()|};
                var a = {|ATL1001:new { X = 1 }|};
                int[] local = {|ATL1001:new int[2]|};
                int[] spread = {|ATL1001:[1, 2]|};
            }
        }
        """);

    [Fact]
    public void Strings() => Verify("""
        public sealed class W00 : Worker1
        {
            {|ATL1002:string|} Name = {|ATL1002:"wakka"|};
            void E0()
            {
                Asm("pushimm 1");
                var s = {|ATL1002:$"x{1}"|};
                Take({|ATL1002:"a" + "b"|});
            }
            void Take({|ATL1002:string|} s) { }
        }
        """);

    [Fact]
    public void BaseLibrary() => Verify("""
        public sealed class W00 : Worker1
        {
            void E0()
            {
                int a = Math.{|ATL1003:Abs|}(-1);
                Console.{|ATL1003:WriteLine|}(1);
                int h = a.{|ATL1003:GetHashCode|}();
                int n = Test.V.{|ATL1003:Length|};
            }
        }
        public static class Test { public static int[] V = new int[2]; }
        """, "using System;\n");

    [Fact]
    public void CollectionsAndLinq() => Verify("""
        public sealed class W00 : Worker1
        {
            static int[] V = new int[2];
            void E0()
            {
                {|ATL1004:List<int>|}? list = null;
                var made = new {|ATL1004:List<int>|}();
                var e = V.{|ATL1005:Select|}(x => x);
                var q = {|ATL1005:from|} x in V select x;
            }
        }
        """, "using System.Collections.Generic;\nusing System.Linq;\n", "ATL1007");

    [Fact]
    public void Exceptions() => Verify("""
        public sealed class W00 : Worker1
        {
            void E0()
            {
                {|ATL1006:try|} { } finally { }
                {|ATL1006:throw|} Runtime.NotRunnable();
            }
        }
        """);

    [Fact]
    public void LambdasAndDelegates() => Verify("""
        public delegate void {|ATL1007:Callback|}();
        public sealed class W00 : Worker1
        {
            void E0()
            {
                Callback c = () {|ATL1007:=>|} { };
                Callback m = {|ATL1007:E0|};
                void {|ATL1007:Local|}() { }
                {|ATL1007:Action|} a = {|ATL1007:delegate|} { };
            }
        }
        """, "using System;\n");

    [Fact]
    public void Generics() => Verify("""
        public sealed class {|ATL1008:Box|}<T> { }
        public sealed class W00 : Worker1
        {
            void {|ATL1008:Pick|}<T>() { }
            void E0()
            {
                Call<W01>();
                Pick<int>();
            }
        }
        public sealed class W01 : Worker0 { void E0() { } }
        """);

    [Fact]
    public void Types() => Verify("""
        public sealed class W00 : Worker1
        {
            {|ATL1009:long|} A;
            {|ATL1009:double|} B;
            {|ATL1009:decimal|} C;
            {|ATL1009:object|}? D;
            float F;
            void E0()
            {
                F = {|ATL1009:1.5|} > 1f ? 1f : 2f;
                var x = {|ATL1009:2L|};
                {|ATL1009:dynamic|}? y = null;
            }
        }
        """);

    [Fact]
    public void Checked() => Verify("""
        public sealed class W00 : Worker1
        {
            int I;
            void E0()
            {
                I = {|ATL1010:checked|}(I + 1);
                {|ATL1010:checked|} { I++; }
                I = unchecked(I + 1);
            }
        }
        """);

    [Fact]
    public void ShortCircuit() => Verify("""
        public sealed class W00 : Worker1
        {
            int I;
            void E0()
            {
                if (I > 1 {|ATL1011:&&|} I < 3 {|ATL1011:|||} I == 7) { }
                if (I > 1 & I < 3 | I == 7) { }
                if (I is > 1 and < 3) { }
            }
        }
        """);

    [Fact]
    public void NoAtelReferenceNoRules() {
        Microsoft.CodeAnalysis.CSharp.CSharpCompilation c = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Plain",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("class C { string S = \"x\"; long L; }", cancellationToken: TestContext.Current.CancellationToken)],
            [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        Assert.DoesNotContain(Analyze(c), d => d.Id.StartsWith("ATL", StringComparison.Ordinal));
    }
}
