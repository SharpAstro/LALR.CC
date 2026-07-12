using System.Collections.Generic;
using LALR.CC;
using LALR.CC.LexicalGrammar;
using Xunit;

namespace LALR.CC.Tests;

/// <summary>
/// Tests for <see cref="Parser.ParseInputResilient"/> — list-boundary panic-mode recovery. The
/// grammar is a list of declarations, each either <c>d ;</c> or a bracketed <c>d { L }</c>, so the
/// tests can inject a broken element (a stray <c>x</c>, which is a valid-but-never-expected symbol)
/// and assert recovery keeps the well-formed elements and records the skipped one.
/// </summary>
public class ResilientParseTests
{
    // Symbols: 0=S', 1=L(ist), 2=D(ecl), 3='d', 4=';', 5='{', 6='}', 7='x'(unexpected everywhere).
    private const int L = 1, D = 2, D_lit = 3, Semi = 4, Open = 5, Close = 6, Bad = 7;

    private static Grammar ListGrammar() => new(
        ["S'", "L", "D", "d", ";", "{", "}", "x"],
        new PrecedenceGroup(Derivation.None,
            new Production(0, L),               // S' -> L
            new Production(L, L, D),            // L  -> L D
            new Production(L, D),               // L  -> D
            new Production(D, D_lit, Semi),     // D  -> d ;
            new Production(D, D_lit, Open, L, Close)));  // D -> d { L }

    private static IReadOnlyDictionary<string, LexRule[]> ListLexer() => new Dictionary<string, LexRule[]>
    {
        { PipeBytesLexer.RootState, [
            new(D_lit, new CharRx('d')),
            new(Semi,  new CharRx(';')),
            new(Open,  new CharRx('{')),
            new(Close, new CharRx('}')),
            new(Bad,   new CharRx('x')),
        ] },
    };

    private static readonly HashSet<int> Sync = [D_lit];    // a declaration starts with 'd'
    private static readonly HashSet<int> OpenBr = [Open];
    private static readonly HashSet<int> CloseBr = [Close];

    private static ResilientParseResult ParseResilient(string input)
    {
        var parser = new Parser(ListGrammar());
        using var lexer = BytesLexer.FromString(input, ListLexer());
        using var tokens = new SyncLATokenIterator(lexer);
        return parser.ParseInputResilient(tokens, Sync, OpenBr, CloseBr,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Count terminal tokens with <paramref name="id"/> in the tree — with one decl per
    /// <c>d</c>, counting <c>d</c> (id 3) leaves counts the declarations that survived.</summary>
    private static int CountTokens(Item item, int id)
    {
        if (item is null) { return 0; }
        switch (item.ContentType)
        {
            case ContentType.Scalar:
                return item.ID == id ? 1 : 0;
            case ContentType.Nested:
                return CountTokens(item.Nested, id);
            case ContentType.Reduction:
                var n = 0;
                foreach (var c in item.Reduction.Children) { n += CountTokens(c, id); }
                return n;
            default:
                return 0;
        }
    }

    [Fact]
    public void CleanInput_MatchesParseInput_AndReportsNoErrors()
    {
        // Baseline via the ordinary parser.
        var parser = new Parser(ListGrammar());
        using var baseLexer = BytesLexer.FromString("d;d;", ListLexer());
        using var baseTokens = new SyncLATokenIterator(baseLexer);
        var baseline = parser.ParseInput(baseTokens, cancellationToken: TestContext.Current.CancellationToken);

        var r = ParseResilient("d;d;");
        Assert.Empty(r.Errors);
        Assert.False(r.Tree.IsError);
        Assert.Equal(2, CountTokens(r.Tree, D_lit));
        // A clean input must yield exactly the ParseInput tree.
        Assert.Equal(baseline.ToString(), r.Tree.ToString());
    }

    [Fact]
    public void BrokenMiddleElement_Recovers_KeepsBothGoodDecls()
    {
        // `d ; x d ;` — the stray `x` errors after the first decl; recovery skips it and resyncs at
        // the following `d`.
        var r = ParseResilient("d;xd;");
        Assert.Single(r.Errors);
        Assert.Equal(Bad, r.Errors[0].OffendingToken.ID);
        Assert.False(r.Tree.IsError);
        Assert.Equal(2, CountTokens(r.Tree, D_lit));   // both `d ;` decls survive
    }

    [Fact]
    public void BrokenBracketedElement_DepthGuardsNestedStarter()
    {
        // `d { x d ; } d ;` — the broken bracketed decl contains a NESTED `d` (a sync terminal) at
        // depth 1. Depth-seeding must keep it from being mistaken for a top-level boundary, so the
        // whole `d { … }` element is skipped and recovery resyncs at the trailing top-level `d ;`.
        var r = ParseResilient("d{xd;}d;");
        Assert.Single(r.Errors);
        Assert.False(r.Tree.IsError);
        Assert.Equal(1, CountTokens(r.Tree, D_lit));   // only the trailing `d ;` (nested `d` was skipped, not resynced-to)
    }

    [Fact]
    public void BrokenTailAtEof_ReturnsGoodPrefix()
    {
        // `d ; x` — a good decl then a broken tail that runs to EOF. The prefix is returned.
        var r = ParseResilient("d;x");
        Assert.Single(r.Errors);
        Assert.False(r.Tree.IsError);
        Assert.Equal(1, CountTokens(r.Tree, D_lit));
    }

    [Fact]
    public void MultipleBrokenElements_RecordsEach()
    {
        // Two separate broken elements → two recorded errors, three good decls kept.
        var r = ParseResilient("d;xd;xd;");
        Assert.Equal(2, r.Errors.Count);
        Assert.False(r.Tree.IsError);
        Assert.Equal(3, CountTokens(r.Tree, D_lit));
    }
}
