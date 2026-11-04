// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Cuts.Tests;

/// <summary>A made-up script block with two dead blocks, and a catalog that names them.</summary>
internal static class Fixture {
    private const uint V0 = 5u << 28 | 4u << 25;

    /// <summary>
    ///     Worker 0: a dead block (<c>if (false) { V0 = 8; }</c>), a dead block with an else
    ///     (<c>if (false) { V0 = 5; } else { V0 = 2; }</c>), and a live <c>jmp</c> over the same code as the first
    ///     block, which a <c>popa_jz</c> reaches: not a dead block. Worker 1 has the first block's code once more,
    ///     dead: one name, two blocks.
    /// </summary>
    private const string Code0 = """
        e0:
            pushimm 1
            popv v0
            jmp l0
            pushimm 8
            popv v0
        l0:
            jmp l1
            pushimm 5
            popv v0
            jmp l2
        l1:
            pushimm 2
            popv v0
        l2:
            pushv v0
            popa_jz l3
            pushimm 3
            popv v0
            jmp l4
        l3:
            pushimm 8
            popv v0
        l4:
            end
        e1:
            end
        """;

    private const string Code1 = """
        e0:
            jmp l0
            pushimm 8
            popv v0
        l0:
            end
        """;

    /// <summary>The block, named <paramref name="name"/>.</summary>
    public static byte[] Block(string name = "997") {
        ScriptImage image = new() {
            Author = "test"u8.ToArray(), Name = System.Text.Encoding.ASCII.GetBytes(name),
            Shared = new byte[0x40], Other = new byte[0x40], Event = new byte[0x10],
        };
        image.Variables.Add(new VarEntry(V0, 1, 0));
        foreach (string code in new[] { Code0, Code1 }) {
            WorkerAssembler a = new();
            a.Add(code);
            AssembledWorker done = a.Finish();
            image.Workers.Add(new WorkerImage { Type = 2, Code = done.Code, Entries = done.Entries, Labels = done.Labels });
        }
        return image.Write();
    }

    /// <summary>The block offset of instruction <paramref name="i"/> of worker <paramref name="w"/>.</summary>
    public static int At(byte[] block, int w, int i) {
        ScriptBlock b = ScriptBlock.Parse(block);
        return b.CodeStart + (int)b.Decode(b.Workers[w])[i].Offset;
    }

    private static string Signature(byte[] block, int from, int to) {
        ScriptBlock b = ScriptBlock.Parse(block);
        return DeadSignature.Of(b, 0, b.Decode(b.Workers[0]), from, to);
    }

    /// <summary>The first block's jmp in worker 0 and worker 1; the block with an else's jmp in worker 0.</summary>
    public static int PlainAt0(byte[] b) => At(b, 0, 2);
    public static int PlainAt1(byte[] b) => At(b, 1, 0);
    public static int ElseAt(byte[] b) => At(b, 0, 5);

    /// <summary>The live jmp over the same code as the first block (reached by a popa_jz): never switched.</summary>
    public static int LiveAt(byte[] b) => At(b, 0, 15);

    /// <summary><c>Plain</c> (two blocks) and <c>WithElse</c> (one), both in script <c>997</c>.</summary>
    public static CutCatalog Catalog() {
        byte[] b = Block();
        string names = $$"""
            # made up
            [cuts]
            "{{Signature(b, 3, 5)}}" = "Plain"   # both workers
            "{{Signature(b, 6, 8)}}" = "WithElse"

            [scripts]
            Plain = ["997"]
            WithElse = ["997"]
            """;
        const string docs = """
            public static class Cut
            {
                /// <summary>
                ///     Sets <c>V0</c> to 8, as <see cref="WithElse"/> does not.
                /// </summary>
                public const bool Plain = false;

                /// <summary>Sets V0 to 5 instead of 2.</summary>
                public const bool WithElse = false;
            }
            """;
        return CutCatalog.Parse(names, docs);
    }
}
