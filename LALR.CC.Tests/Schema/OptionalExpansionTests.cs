using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LALR.CC;
using LALR.CC.LexicalGrammar;
using LALR.CC.Schema;
using Xunit;

namespace LALR.CC.Tests.Schema;

/// <summary>The optional operator <c>X?</c> (<see cref="OptionalExpansion"/>).</summary>
public class OptionalExpansionTests
{
    /// <summary>A schema with one group holding <paramref name="rules"/>, over <paramref name="symbols"/>.</summary>
    private static GrammarSchema OneGroup(List<string> symbols, params ProductionSchema[] rules) => new()
    {
        Symbols = symbols,
        Productions = [new ProductionGroupSchema { Derivation = Derivation.None, Rules = [.. rules] }],
    };

    [Fact]
    public void Two_optionals_become_four_rules_all_present_first_each_with_its_slots()
    {
        var schema = OneGroup(["Root", "D", "pub", "inline", "fn", "id"],
            new ProductionSchema { Lhs = "D", Rhs = ["pub?", "inline?", "fn", "id"], Action = "decl" });

        Assert.Empty(OptionalExpansion.Expand(schema));

        var rules = schema.Productions[0].Rules;
        Assert.Equal(4, rules.Count);
        Assert.All(rules, r => Assert.Equal("decl", r.Action));
        Assert.Equal(["pub", "inline", "fn", "id"], rules[0].Rhs);
        Assert.Null(rules[0].Slots);                           // children already are the arguments
        Assert.Equal(["pub", "fn", "id"], rules[1].Rhs);
        Assert.Equal([0, -1, 1, 2], rules[1].Slots);
        Assert.Equal(["inline", "fn", "id"], rules[2].Rhs);
        Assert.Equal([-1, 0, 1, 2], rules[2].Slots);
        Assert.Equal(["fn", "id"], rules[3].Rhs);
        Assert.Equal([-1, -1, 0, 1], rules[3].Slots);
    }

    [Fact]
    public void A_symbol_spelled_with_a_question_mark_stays_itself_and_can_be_made_optional()
    {
        // `'?'` is a terminal (zig's optional-type prefix); `'?'?` is that terminal made optional.
        var schema = OneGroup(["Root", "T", "'?'", "id"],
            new ProductionSchema { Lhs = "T", Rhs = ["'?'", "id"], Action = "opt" },
            new ProductionSchema { Lhs = "T", Rhs = ["'?'?", "id", "id"], Action = "pair" });

        Assert.Empty(OptionalExpansion.Expand(schema));

        var rules = schema.Productions[0].Rules;
        Assert.Equal(3, rules.Count);
        Assert.Equal(["'?'", "id"], rules[0].Rhs);
        Assert.Equal(["'?'", "id", "id"], rules[1].Rhs);
        Assert.Equal(["id", "id"], rules[2].Rhs);
        Assert.Equal([-1, 0, 1], rules[2].Slots);
    }

    [Fact]
    public void A_name_whose_base_is_no_symbol_is_left_for_the_validator()
    {
        var schema = OneGroup(["Root", "D", "fn"], new ProductionSchema { Lhs = "D", Rhs = ["nope?", "fn"] });

        Assert.Empty(OptionalExpansion.Expand(schema));
        Assert.Equal(["nope?", "fn"], schema.Productions[0].Rules.Single().Rhs);
    }

    [Fact]
    public void More_than_four_optionals_is_an_error_and_the_rule_stays_as_written()
    {
        var schema = OneGroup(["Root", "D", "a", "b", "c", "d", "e"],
            new ProductionSchema { Lhs = "D", Rhs = ["a?", "b?", "c?", "d?", "e?"] });

        var errors = OptionalExpansion.Expand(schema);

        var error = Assert.Single(errors);
        Assert.Equal("productions[0].rules[0]", error.Path);
        Assert.Contains("at most 4", error.Message);
        Assert.Equal(["a?", "b?", "c?", "d?", "e?"], schema.Productions[0].Rules.Single().Rhs);
    }

    [Fact]
    public void Expanding_an_expanded_schema_changes_nothing()
    {
        var schema = OneGroup(["Root", "D", "pub", "fn"], new ProductionSchema { Lhs = "D", Rhs = ["pub?", "fn"], Action = "d" });
        OptionalExpansion.Expand(schema);
        var once = schema.Productions[0].Rules.Select(r => string.Join(" ", r.Rhs)).ToList();

        OptionalExpansion.Expand(schema);

        Assert.Equal(once, schema.Productions[0].Rules.Select(r => string.Join(" ", r.Rhs)));
    }

    [Fact]
    public async Task The_action_sees_null_where_the_optional_is_absent()
    {
        // Root -> D ; D -> 'p'? 'f' n  (action decl): "p f 1" and "f 1" both reach `decl` with three arguments.
        var schema = new GrammarSchema
        {
            Symbols = ["Root", "D", "p", "f", "n"],
            Productions =
            [
                new ProductionGroupSchema
                {
                    Derivation = Derivation.None,
                    Rules =
                    [
                        new ProductionSchema { Lhs = "Root", Rhs = ["D"] },
                        new ProductionSchema { Lhs = "D", Rhs = ["p?", "f", "n"], Action = "decl" },
                    ],
                },
            ],
            Lexer = new Dictionary<string, List<LexRuleSchema>>
            {
                [PipeBytesLexer.RootState] =
                [
                    new LexRuleSchema { Symbol = "p", Match = "p" },
                    new LexRuleSchema { Symbol = "f", Match = "f" },
                    new LexRuleSchema { Symbol = "n", Match = "[0-9]+" },
                    new LexRuleSchema { Symbol = "n", Match = "[ ]+", Action = "ignore" },
                ],
            },
        };
        var actions = new Dictionary<string, Func<int, Item[], object>>
        {
            ["decl"] = (lhs, args) => $"{args.Length}:{(args[0] is null ? "-" : args[0].Content)}:{args[2].Content}",
        };

        var (grammar, lexerTable) = SchemaCompiler.Compile(schema, actions);

        Assert.Equal("3:p:1", await Parse(grammar, lexerTable, "p f 1"));
        Assert.Equal("3:-:2", await Parse(grammar, lexerTable, "f 2"));
    }

    /// <summary>Parse <paramref name="input"/> and return the result's content.</summary>
    private static async Task<object> Parse(Grammar grammar, Dictionary<string, LexRule[]> lexerTable, string input)
    {
        var parser = new Parser(grammar);
        using var lexer = PipeBytesLexer.FromString(input, lexerTable, cancellationToken: TestContext.Current.CancellationToken);
        using var la = new AsyncLATokenIterator(lexer);
        var result = await parser.ParseInputAsync(la, new Debug(parser, null, null),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        return result.Content;
    }
}
