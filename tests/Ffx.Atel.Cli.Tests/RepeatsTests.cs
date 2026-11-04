// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Cli.Repeats;
using Ffx.Atel.Decompiler.Repeats;
using Ffx.Atel.Decompiler;

namespace Ffx.Atel.Cli.Tests;

/// <summary><c>atel repeats</c>: shapes, the runs it finds, their parameters and the folded-if twins (docs/research/repeats.md).</summary>
public sealed class RepeatsTests {
    private static readonly ushort[] Ids = [.. ScriptDecompiler.Natives.All.Where(n => !n.Polls && n.Argc == 1).Take(3).Select(n => n.Id)];

    private static CallStmt Call(int native, int value) => new(Ids[native], [new IntLit(value)]);

    private static (Corpus Corpus, ShapeTable Table) Mine(params IReadOnlyList<Stmt>[] methods) {
        ShapeTable table = new();
        Corpus corpus = new();
        List<(int, List<BlockRecord>, long[])> workers = [];
        foreach (IReadOnlyList<Stmt> body in methods) {
            ShapeWalker w = new(table, ScriptDecompiler.Natives, vars: false);
            w.WalkMethod(body, 0);
            workers.Add((workers.Count, w.Blocks, [.. w.Holes]));
        }
        corpus.Add("test", workers);
        return (corpus, table);
    }

    [Fact]
    public void ConstantsAreHolesAndNativesAreNot() {
        (Corpus c, _) = Mine([Call(0, 1), Call(0, 2), Call(1, 1)]);
        StmtInfo[] s = c.Blocks[0].Record.Stmts;
        Assert.Equal(s[0].Id, s[1].Id);
        Assert.NotEqual(s[0].Id, s[2].Id);
        Assert.Equal([ShapeWalker.TagInt << 56 | 1, ShapeWalker.TagInt << 56 | 2, ShapeWalker.TagInt << 56 | 1], c.Holes[0]);
    }

    [Fact]
    public void FindsARunWithItsVaryingConstantsAsParameters() {
        // Three places of `N0(k); N1(7);` with k 1, 2, 3 and 7 fixed, apart from other code.
        List<Stmt> body = [];
        for (int k = 1; k <= 3; k++) {
            body.Add(Call(0, k));
            body.Add(Call(1, 7));
            body.Add(new GotoStmt(k));
        }
        (Corpus corpus, ShapeTable table) = Mine(body);
        Miner miner = new(corpus, minPlaces: 2, minSaved: 0);
        List<Helper> helpers = miner.Cover(miner.Candidates(), 10);
        Helper h = Assert.Single(helpers);
        Assert.Equal(2, h.Run.Pattern.Length);
        Assert.Equal(3, h.Places.Count);
        Param p = Assert.Single(h.Params);
        Assert.Equal([0], p.Columns);
        Assert.Equal(3, p.Distinct);

        // The template, with the parameter as a ParamExpr, gives each place back.
        Dictionary<int, int> param = new() { [0] = 0 };
        ShapeWalker rewriter = new(table, ScriptDecompiler.Natives, false, (hole, _) => param.TryGetValue(hole, out int x) ? new ParamExpr(x) : null);
        (List<Stmt> template, _) = rewriter.Rewrite(body[..2]);
        InlineMacro macro = new("Repeats.R001", [new InlineParam("p0", false, null)], template, false);
        foreach (int at in new[] { 0, 3, 6 }) {
            Expr[]? args = Inline.Match(macro, body, at);
            Assert.NotNull(args);
            Assert.True(Inline.Same(Inline.Expand(macro, args), body[at..(at + 2)]));
        }
    }

    [Fact]
    public void ARunHoldsNoBreakOfAnOuterLoop() {
        // `N2(k); break;` three times in a loop: a helper cannot leave the caller's loop, so no run.
        (Corpus corpus, _) = Mine([new WhileStmt(null, [Call(2, 1), new BreakStmt(), Call(2, 2), new BreakStmt(), Call(2, 3), new BreakStmt()])]);
        Miner miner = new(corpus, minPlaces: 2, minSaved: 0);
        Assert.Empty(miner.Candidates());
        // The break leaves its statement; the loop holds it.
        Assert.True(corpus.Blocks[1].Record.Stmts[1].Escapes);
        Assert.False(corpus.Blocks[0].Record.Stmts[0].Escapes);
    }

    [Fact]
    public void CountsFoldedIfTwins() {
        // The same `if (K) { A } else { B }` folded with K false and with K true.
        Stmt a = Call(0, 1), b = Call(1, 2);
        (Corpus corpus, _) = Mine([new IfStmt(null, [a], [b]), Call(2, 0), a, new IfStmt(null, [b], null)]);
        Cli.Repeats.DeadTwins.Result r = Cli.Repeats.DeadTwins.Count(corpus);
        Assert.Equal(1, r.FalseForms);
        Assert.Equal(1, r.TrueForms);
        Assert.Equal(1, r.TrueWithTwin);
        Assert.Equal(1, r.FalseWithTwin);
    }
}
