using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using LALR.CC.LexicalGrammar;
using LALR.CC.Schema;
using Xunit;

namespace LALR.CC.Tests;

/// <summary>
/// Trailing context on a lexer rule (<see cref="LexRule.Lookahead"/>, flex's <c>r/s</c>): a block label
/// <c>blk: {</c> lexes its name as LABEL, while <c>len :0</c> keeps an IDENT. Both lexers must agree, including
/// when the pipe delivers the lookahead's bytes in a later read.
/// </summary>
public class LexerLookaheadTests
{
    private const int Label = 1;
    private const int Ident = 2;
    private const int Colon = 3;
    private const int LBrace = 4;
    private const int Number = 5;
    private const int Ws = 6;
    private const int For = 7;

    private static IReadOnlyDictionary<string, LexRule[]> Table() => new Dictionary<string, LexRule[]>
    {
        {
            PipeBytesLexer.RootState,
            [
                new(For, IRxParser.Parse("for")),
                new(Ident, IRxParser.Parse("[a-z]+")),
                new(Colon, IRxParser.Parse(":")),
                new(LBrace, IRxParser.Parse("\\{")),
                new(Number, IRxParser.Parse("[0-9]+")),
                new(Ws, IRxParser.Parse("[ \\n]+"), PipeBytesLexer.Ignore),
            ]
        },
    };

    /// <summary>The table with a LABEL rule ahead of IDENT: <c>[a-z]+</c> followed by <c>: {</c> or <c>: for</c>.</summary>
    private static IReadOnlyDictionary<string, LexRule[]> LabelTable(bool labelFirst = true)
    {
        var label = new LexRule(Label, IRxParser.Parse("[a-z]+"), null, IRxParser.Parse("[ \\n]*:[ \\n]*(\\{|for )"));
        var rules = new List<LexRule>(Table()[PipeBytesLexer.RootState]);
        rules.Insert(labelFirst ? 0 : 2, label);
        return new Dictionary<string, LexRule[]> { { PipeBytesLexer.RootState, rules.ToArray() } };
    }

    private static List<(int Id, string Text)> Sync(string text, IReadOnlyDictionary<string, LexRule[]> table)
    {
        var tokens = new List<(int, string)>();
        using var lexer = BytesLexer.FromString(text, table);
        while (lexer.MoveNext()) { tokens.Add((lexer.Current.ID, (string)lexer.Current.Content)); }
        return tokens;
    }

    /// <summary>Lex through a pipe written one byte per flush, so every lookahead runs off the end of the buffer first.</summary>
    private static async Task<List<(int Id, string Text)>> Piped(string text, IReadOnlyDictionary<string, LexRule[]> table)
    {
        var pipe = new Pipe();
        var writer = Task.Run(async () =>
        {
            foreach (var b in Encoding.UTF8.GetBytes(text))
            {
                await pipe.Writer.WriteAsync(new[] { b });
            }
            await pipe.Writer.CompleteAsync();
        });
        var tokens = new List<(int, string)>();
        using var lexer = new PipeBytesLexer(pipe.Reader, table, cancellationToken: TestContext.Current.CancellationToken);
        while (await lexer.MoveNextAsync())
        {
            var item = await lexer.CurrentAsync();
            tokens.Add((item.ID, (string)item.Content));
        }
        await writer;
        return tokens;
    }

    public static TheoryData<string, string> Cases => new()
    {
        // A label before `{` or `for`: the name alone is the LABEL token, and the `:` lexes again after it.
        { "blk: {", "1:blk 3:: 4:{" },
        { "outer :\n for x", "1:outer 3:: 7:for 2:x" },
        // No `{` / `for` after the `:`: an ordinary IDENT (`buf[0..len :0]`).
        { "len :0", "2:len 3:: 5:0" },
        // `forward` is not `for `: the lookahead fails, so IDENT.
        { "x: forward", "2:x 3:: 2:forward" },
        // Lookahead at end of input with no `{`: IDENT.
        { "a:", "2:a 3::" },
        { "a", "2:a" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_lookahead_rule_matches_only_with_its_trailing_context(string text, string expected)
    {
        Assert.Equal(expected, Render(Sync(text, LabelTable())));
        Assert.Equal(expected, Render(await Piped(text, LabelTable())));
    }

    [Fact]
    public async Task A_tie_on_length_still_goes_to_the_earlier_rule()
    {
        // LABEL listed after IDENT: both match `blk` (3 bytes), and IDENT, the earlier rule, wins.
        Assert.Equal("2:blk 3:: 4:{", Render(Sync("blk: {", LabelTable(labelFirst: false))));
        Assert.Equal("2:blk 3:: 4:{", Render(await Piped("blk: {", LabelTable(labelFirst: false))));
    }

    [Fact]
    public void A_schema_lookahead_compiles_onto_the_rule()
    {
        var schema = new GrammarSchema
        {
            Symbols = ["S'", "E", "LABEL", "IDENT"],
            Productions =
            [
                new ProductionGroupSchema
                {
                    Rules =
                    [
                        new ProductionSchema { Lhs = "S'", Rhs = ["E"] },
                        new ProductionSchema { Lhs = "E", Rhs = ["IDENT"] },
                        new ProductionSchema { Lhs = "E", Rhs = ["LABEL"] },
                    ],
                },
            ],
            Lexer = new Dictionary<string, List<LexRuleSchema>>
            {
                [PipeBytesLexer.RootState] =
                [
                    new() { Symbol = "LABEL", Match = "[a-z]+", Lookahead = ":" },
                    new() { Symbol = "IDENT", Match = "[a-z]+" },
                ],
            },
        };
        var (_, table) = SchemaCompiler.Compile(schema);
        Assert.NotNull(table[PipeBytesLexer.RootState][0].Lookahead);
        Assert.Null(table[PipeBytesLexer.RootState][1].Lookahead);
        var root = table[PipeBytesLexer.RootState];
        var withColon = new Dictionary<string, LexRule[]> { { PipeBytesLexer.RootState, [root[0], root[1], new(99, IRxParser.Parse(":"))] } };
        // `a:` lexes LABEL then `:`; a bare `a` has no trailing `:`, so IDENT.
        Assert.Equal(root[0].SymbolId + ":a 99::", Render(Sync("a:", withColon)));
        Assert.Equal(root[1].SymbolId + ":a", Render(Sync("a", withColon)));
    }

    private static string Render(List<(int Id, string Text)> tokens)
        => string.Join(" ", tokens.ConvertAll(t => t.Id + ":" + t.Text));
}
