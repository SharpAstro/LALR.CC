using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LALR.CC.LexicalGrammar;
using Xunit;

namespace LALR.CC.Tests;

/// <summary>
/// The lexers share each state's compiled DFA across every lexer built over the same rules
/// (<c>CompiledLexerTable</c>). These pin that sharing never changes what a lexer does: a
/// table rebuilt over the same rule arrays lexes the same, a table whose rules changed after
/// a lexer was built is recompiled, and an invalid table is still rejected when its root
/// rules are already compiled.
/// </summary>
public class CompiledLexerTableTests
{
    private const int Number = 10;
    private const int Plus = 11;
    private const int Minus = 12;

    private static LexRule NumberRule() =>
        new(Number, new GroupRx(Multiplicity.OneOrMore, new CharClassRx(true, [new CharRangeRx('0', '9')])));

    private static List<(int Id, string Text)> Lex(string text, IReadOnlyDictionary<string, LexRule[]> table)
    {
        using var lexer = BytesLexer.FromString(text, table);
        var tokens = new List<(int, string)>();
        while (lexer.MoveNext()) { tokens.Add((lexer.Current.ID, (string)lexer.Current.Content)); }
        return tokens;
    }

    private static Dictionary<string, LexRule[]> Table(LexRule[] root) => new() { { PipeBytesLexer.RootState, root } };

    [Fact]
    public void A_new_table_over_the_same_rule_arrays_lexes_the_same()
    {
        LexRule[] rules = [NumberRule(), new(Plus, new CharRx('+'))];
        var first = Lex("1+23", Table(rules));
        var second = Lex("1+23", Table(rules));
        Assert.Equal([(Number, "1"), (Plus, "+"), (Number, "23")], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_rule_changed_in_place_after_a_lexer_was_built_is_recompiled()
    {
        LexRule[] rules = [NumberRule(), new(Plus, new CharRx('+'))];
        Assert.Equal([(Number, "1"), (Plus, "+"), (Number, "2")], Lex("1+2", Table(rules)));
        rules[1] = new LexRule(Minus, new CharRx('-'));
        Assert.Equal([(Number, "1"), (Minus, "-"), (Number, "2")], Lex("1-2", Table(rules)));
        Assert.Throws<LexerException>(() => Lex("1+2", Table(rules)));
    }

    [Fact]
    public void An_empty_state_is_rejected_even_when_the_root_rules_are_compiled()
    {
        LexRule[] rules = [NumberRule()];
        Assert.Single(Lex("7", Table(rules)));
        var table = Table(rules);
        table["empty"] = [];
        Assert.Throws<ArgumentException>(() => BytesLexer.FromString("7", table));
        Assert.Throws<ArgumentException>(() => PipeBytesLexer.FromString("7", table));
    }

    [Fact]
    public void Lexers_on_many_threads_share_one_table()
    {
        LexRule[] rules = [NumberRule(), new(Plus, new CharRx('+'))];
        var text = string.Join("+", Enumerable.Range(0, 200));
        var expected = Lex(text, Table(rules));
        var results = new List<(int, string)>[16];
        Parallel.For(0, results.Length, i => results[i] = Lex(text, Table(rules)));
        Assert.All(results, r => Assert.Equal(expected, r));
    }

    [Fact]
    public async Task The_pipe_lexer_reads_the_same_shared_states()
    {
        LexRule[] rules = [NumberRule(), new(Plus, new CharRx('+'))];
        var sync = Lex("4+56", Table(rules));
        var lexer = PipeBytesLexer.FromString("4+56", Table(rules));
        var tokens = new List<(int, string)>();
        while (await lexer.MoveNextAsync())
        {
            var item = await lexer.CurrentAsync();
            tokens.Add((item.ID, (string)item.Content));
        }
        Assert.Equal(sync, tokens);
    }
}
