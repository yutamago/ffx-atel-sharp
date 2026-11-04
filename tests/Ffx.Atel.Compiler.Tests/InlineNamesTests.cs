// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Decompiler.Layout;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>The names of the worker's own inline methods (<c>data/inline-names.toml</c>): names, parameters, summaries.</summary>
public sealed class InlineNamesTests {
    private const string Table = """
        [helpers]
        "0000000000000001" = "Plain"                          # a comment
        "0000000000000002" = "WithParams(stat, slot)"
        "0000000000000003" = { name = "Own(frame)", summary = "Its own summary, with `code`." }
        "0000000000000004" = "Shared"
        "0000000000000005" = { name = "Shared(x)", summary = "Overrides the shared one." }

        [summaries]
        Shared = "What every Shared does."
        """;

    [Fact]
    public void ATableGivesNamesParametersAndSummaries() {
        InlineNames names = InlineNames.Parse(Table, "test");
        Assert.Equal(new InlineNames.Entry("Plain", null, null), names.Find("0000000000000001"));
        Assert.Equal(["stat", "slot"], names.Find("0000000000000002")!.Params!);
        Assert.Equal("Its own summary, with `code`.", names.Find("0000000000000003")!.Summary);
        Assert.Equal("What every Shared does.", names.Find("0000000000000004")!.Summary);
        Assert.Equal("Overrides the shared one.", names.Find("0000000000000005")!.Summary);
        Assert.Null(names.Find("0000000000000006"));
    }

    [Theory]
    [InlineData("[helpers]\n\"0000000000000001\" = \"A(x, x)\"", "named twice")]
    [InlineData("[helpers]\n\"0000000000000001\" = { name = \"A\" summary = \"s\" }", "expected {")]
    [InlineData("[summaries]\nnot-a-name = \"s\"", "is not a C# name")]
    [InlineData("[summaries]\nA = \"s\"\nA = \"t\"", "two summaries")]
    [InlineData("\"0000000000000001\" = \"A\"", "start with [helpers]")]
    public void ATableIsChecked(string text, string message) {
        FormatException ex = Assert.Throws<FormatException>(() => InlineNames.Parse(text, "test"));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRepositorysTableReads() {
        Assert.True(InlineNames.Default.Count > 300);
        Assert.Contains(InlineNames.Default.Signatures, s => InlineNames.Default.Find(s)!.Summary is not null);
    }

    [Fact]
    public void ASignatureIsTheCodeNotItsNames() {
        // Variable 0 and variable 2 are at one location: the same code; variable 1 is elsewhere.
        VarEntry[] table = [new(0x0800_0004, 1, 0), new(0x0800_0008, 1, 0), new(0x0800_0004, 1, 0)];
        static List<Stmt> Body(int v, Expr value) => [new AssignStmt(new VarExpr(v), value), new CallStmt(0x5001, [new ParamExpr(0), new IntLit(3)])];
        string a = InlineNames.Signature(Body(0, new IntLit(1)), table);
        Assert.Equal(InlineNames.Length, a.Length);
        Assert.Equal(a, InlineNames.Signature(Body(2, new IntLit(1)), table));
        Assert.NotEqual(a, InlineNames.Signature(Body(1, new IntLit(1)), table));
        Assert.NotEqual(a, InlineNames.Signature(Body(0, new IntLit(2)), table));
        // A dead block's Cut name is no part of the code.
        Assert.Equal(
            InlineNames.Signature([new IfStmt(null, Body(0, new IntLit(1)), null)], table),
            InlineNames.Signature([new IfStmt(null, Body(0, new IntLit(1)), null) { Cut = "SomeName" }], table));
    }
}
