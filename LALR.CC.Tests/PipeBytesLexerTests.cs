using System.Collections.Generic;
using System.Threading.Tasks;
using LALR.CC.LexicalGrammar;
using Xunit;

namespace LALR.CC.Tests;

public class PipeBytesLexerTests
{
    private const int Number = 10;
    private const int Plus = 11;
    private const int Word = 12;
    private const int OpenString = 20;
    private const int CloseString = 21;
    private const int StringChar = 22;
    private const int Whitespace = 30;

    private static readonly LexRule[] _arithRules =
    [
        new(Number, new GroupRx(Multiplicity.OneOrMore, new CharClassRx(true, [new CharRangeRx('0', '9')]))),
        new(Plus, new CharRx('+')),
        new(Whitespace, new GroupRx(Multiplicity.OneOrMore,
            new CharClassRx(true, [(CharRx)' ', (CharRx)'\t', (CharRx)'\n'])),
            PipeBytesLexer.Ignore),
    ];

    private static IReadOnlyDictionary<string, LexRule[]> ArithTable() => new Dictionary<string, LexRule[]>
    {
        { PipeBytesLexer.RootState, _arithRules },
    };

    private static async Task<List<Item>> CollectAsync(PipeBytesLexer lexer)
    {
        var tokens = new List<Item>();
        while (await lexer.MoveNextAsync())
        {
            tokens.Add(await lexer.CurrentAsync());
        }
        return tokens;
    }

    [Fact]
    public async Task SingleNumber_EmitsOneToken()
    {
        using var lexer = PipeBytesLexer.FromString("42", ArithTable(), cancellationToken: TestContext.Current.CancellationToken);
        var tokens = await CollectAsync(lexer);
        Assert.Single(tokens);
        Assert.Equal(Number, tokens[0].ID);
        Assert.Equal("42", tokens[0].Content);
    }

    [Fact]
    public async Task LongestMatchWins_NumberConsumesAllDigits()
    {
        using var lexer = PipeBytesLexer.FromString("1234567890", ArithTable(), cancellationToken: TestContext.Current.CancellationToken);
        var tokens = await CollectAsync(lexer);
        Assert.Single(tokens);
        Assert.Equal("1234567890", tokens[0].Content);
    }

    [Fact]
    public async Task MultipleTokens_WithIgnoredWhitespace()
    {
        using var lexer = PipeBytesLexer.FromString("12 + 34", ArithTable(), cancellationToken: TestContext.Current.CancellationToken);
        var tokens = await CollectAsync(lexer);
        Assert.Equal(3, tokens.Count);
        Assert.Equal((Number, "12"), (tokens[0].ID, tokens[0].Content));
        Assert.Equal((Plus, "+"), (tokens[1].ID, tokens[1].Content));
        Assert.Equal((Number, "34"), (tokens[2].ID, tokens[2].Content));
    }

    [Fact]
    public async Task InitialLine_ShiftsLineBase()
    {
        // Parity with BytesLexer: a high line base shifts every token's reported
        // line (column / byte offset unaffected), and a newline bumps the line
        // relative to that base.
        const int Base = 1 << 20;
        using var lexer = PipeBytesLexer.FromString("12\n34", ArithTable(),
            cancellationToken: TestContext.Current.CancellationToken, initialLine: Base);
        var tokens = await CollectAsync(lexer);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(Base, tokens[0].Position.Line);
        Assert.Equal(1, tokens[0].Position.Column);
        Assert.Equal(Base + 1, tokens[1].Position.Line);
        Assert.Equal(3L, tokens[1].Position.ByteOffset);
    }

    [Fact]
    public async Task EmptyInput_NoTokens()
    {
        using var lexer = PipeBytesLexer.FromString("", ArithTable(), cancellationToken: TestContext.Current.CancellationToken);
        var tokens = await CollectAsync(lexer);
        Assert.Empty(tokens);
    }

    [Fact]
    public async Task UnrecognisedInput_ThrowsByDefault()
    {
        // 'x' isn't in any rule. Default LexerErrorMode.Throw surfaces the bad byte
        // with position info instead of silently EOFing.
        using var lexer = PipeBytesLexer.FromString("12x34", ArithTable(), cancellationToken: TestContext.Current.CancellationToken);
        // The leading "12" must be consumed first; the throw fires when the lexer
        // reaches 'x'.
        Assert.True(await lexer.MoveNextAsync());
        Assert.Equal("12", (await lexer.CurrentAsync()).Content);

        var ex = await Assert.ThrowsAsync<LexerException>(async () => await lexer.MoveNextAsync());
        Assert.Equal((byte)'x', ex.OffendingByte);
        Assert.Equal(3, ex.Position.Column); // "12" then 'x' at column 3
        Assert.Equal(1, ex.Position.Line);
        Assert.Equal(2L, ex.Position.ByteOffset);
        Assert.Equal(PipeBytesLexer.RootState, ex.LexerStateName);
    }

    [Fact]
    public async Task FirstPatternWins_OnEqualLengthTie()
    {
        // Two rules that match "if" the same length: keyword (id 1) and identifier (id 2).
        // The rule listed first wins.
        var keyword = new CharSequenceRx("if");
        var ident = new GroupRx(Multiplicity.OneOrMore, new CharClassRx(true, [new CharRangeRx('a', 'z')]));
        var table = new Dictionary<string, LexRule[]>
        {
            { PipeBytesLexer.RootState, [
                new(1, keyword),
                new(2, ident),
            ] },
        };

        using var lexer = PipeBytesLexer.FromString("if", table, cancellationToken: TestContext.Current.CancellationToken);
        var tokens = await CollectAsync(lexer);
        Assert.Single(tokens);
        Assert.Equal(1, tokens[0].ID); // keyword wins on tie
    }

    [Fact]
    public async Task LongestMatchWins_OverFirstPattern()
    {
        // Same setup; on "ifx" identifier matches 3 bytes, keyword only 2 → identifier wins.
        var keyword = new CharSequenceRx("if");
        var ident = new GroupRx(Multiplicity.OneOrMore, new CharClassRx(true, [new CharRangeRx('a', 'z')]));
        var table = new Dictionary<string, LexRule[]>
        {
            { PipeBytesLexer.RootState, [
                new(1, keyword),
                new(2, ident),
            ] },
        };

        using var lexer = PipeBytesLexer.FromString("ifx", table, cancellationToken: TestContext.Current.CancellationToken);
        var tokens = await CollectAsync(lexer);
        Assert.Single(tokens);
        Assert.Equal(2, tokens[0].ID);
        Assert.Equal("ifx", tokens[0].Content);
    }

    [Fact]
    public async Task StatePushAndPop_StringLiteral()
    {
        // Open quote pushes "string" state; inside, only chars and a closing quote are
        // recognised; close quote pops back to root.
        const string stringState = "string";
        var rootRules = new LexRule[]
        {
            new(OpenString, new CharRx('"'), stringState),
        };
        var stringRules = new LexRule[]
        {
            new(CloseString, new CharRx('"'), PipeBytesLexer.PopState),
            new(StringChar, new GroupRx(Multiplicity.OneOrMore, new CharClassRx(false, '"'))),
        };
        var table = new Dictionary<string, LexRule[]>
        {
            { PipeBytesLexer.RootState, rootRules },
            { stringState, stringRules },
        };

        using var lexer = PipeBytesLexer.FromString("\"hi\"", table, cancellationToken: TestContext.Current.CancellationToken);
        var tokens = await CollectAsync(lexer);
        Assert.Equal(3, tokens.Count);
        Assert.Equal((OpenString, "\""), (tokens[0].ID, tokens[0].Content));
        Assert.Equal((StringChar, "hi"), (tokens[1].ID, tokens[1].Content));
        Assert.Equal((CloseString, "\""), (tokens[2].ID, tokens[2].Content));
    }

    [Fact]
    public async Task UnknownPushedState_Throws()
    {
        var rules = new LexRule[]
        {
            new(1, new CharRx('a'), "doesNotExist"),
        };
        var table = new Dictionary<string, LexRule[]>
        {
            { PipeBytesLexer.RootState, rules },
        };

        using var lexer = PipeBytesLexer.FromString("a", table, cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<System.InvalidOperationException>(async () => await lexer.MoveNextAsync());
    }

    [Fact]
    public async Task TableMissingRootState_Throws()
    {
        var table = new Dictionary<string, LexRule[]>
        {
            { "other", [new(1, new CharRx('a'))] },
        };
        Assert.Throws<System.ArgumentException>(() => PipeBytesLexer.FromString("a", table, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MultiByteUtf8Input_DecodesContent()
    {
        // Pattern accepts only € (E2 82 AC). Use EmitAndStop so the trailing 'a' surfaces
        // as an error item instead of throwing — the test exists to verify € decodes
        // correctly through the byte DFA, not to test error semantics.
        var rules = new LexRule[]
        {
            new(1, new CharRx(0x20AC)),
        };
        var table = new Dictionary<string, LexRule[]>
        {
            { PipeBytesLexer.RootState, rules },
        };
        using var lexer = PipeBytesLexer.FromString("€a", table,
            errorMode: LexerErrorMode.EmitAndStop,
            errorSymbolId: 99,
            cancellationToken: TestContext.Current.CancellationToken);
        var tokens = await CollectAsync(lexer);
        Assert.Equal(2, tokens.Count);
        Assert.Equal((1, "€"), (tokens[0].ID, tokens[0].Content));
        Assert.True(tokens[1].IsError);
    }

    [Fact]
    public async Task LargeInput_CrossesPipeSegments()
    {
        // Default Pipe segment size is 4 KB; emit a token that spans well beyond that
        // to confirm cross-segment consumption works.
        var input = new string('1', 8192) + " + " + new string('2', 8192);
        using var lexer = PipeBytesLexer.FromString(input, ArithTable(), cancellationToken: TestContext.Current.CancellationToken);
        var tokens = await CollectAsync(lexer);
        Assert.Equal(3, tokens.Count);
        Assert.Equal(8192, ((string)tokens[0].Content).Length);
        Assert.Equal("+", tokens[1].Content);
        Assert.Equal(8192, ((string)tokens[2].Content).Length);
    }

    private const int ErrorSym = 99;

    [Fact]
    public async Task EmitAndStop_EmitsErrorTokenThenEofs()
    {
        using var lexer = PipeBytesLexer.FromString("12x34", ArithTable(),
            errorMode: LexerErrorMode.EmitAndStop,
            errorSymbolId: ErrorSym,
            cancellationToken: TestContext.Current.CancellationToken);
        var tokens = await CollectAsync(lexer);

        // Sequence: number "12", then error token for 'x', then false (no "34").
        Assert.Equal(2, tokens.Count);
        Assert.Equal((Number, "12"), (tokens[0].ID, tokens[0].Content));
        Assert.False(tokens[0].IsError);

        Assert.Equal(ErrorSym, tokens[1].ID);
        Assert.Equal("\\x78", tokens[1].Content); // 'x' is 0x78
        Assert.True(tokens[1].IsError);
        Assert.Equal(new SourcePosition(1, 3, 2), tokens[1].Position);
    }

    [Fact]
    public async Task EmitAndSkip_EmitsErrorPerBadByteAndContinues()
    {
        using var lexer = PipeBytesLexer.FromString("12xy34", ArithTable(),
            errorMode: LexerErrorMode.EmitAndSkip,
            errorSymbolId: ErrorSym,
            cancellationToken: TestContext.Current.CancellationToken);
        var tokens = await CollectAsync(lexer);

        // "12", err('x'), err('y'), "34" — recovery emits one error per bad byte.
        Assert.Equal(4, tokens.Count);
        Assert.Equal("12", tokens[0].Content);

        Assert.Equal(ErrorSym, tokens[1].ID);
        Assert.True(tokens[1].IsError);
        Assert.Equal("\\x78", tokens[1].Content); // 'x' is 0x78

        Assert.Equal(ErrorSym, tokens[2].ID);
        Assert.True(tokens[2].IsError);
        Assert.Equal("\\x79", tokens[2].Content); // 'y' is 0x79

        Assert.Equal((Number, "34"), (tokens[3].ID, tokens[3].Content));
        Assert.Equal(new SourcePosition(1, 5, 4), tokens[3].Position); // after "12" + "xy"
    }

    [Fact]
    public void EmitMode_WithoutErrorSymbolId_Throws()
    {
        // Construction must reject `EmitAndStop`/`EmitAndSkip` without a non-negative
        // errorSymbolId — the resulting Items would be unidentifiable.
        Assert.Throws<System.ArgumentException>(() =>
            PipeBytesLexer.FromString("a", ArithTable(),
                errorMode: LexerErrorMode.EmitAndStop,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CleanEof_DoesNotTriggerErrorMode()
    {
        // Throw mode + valid input + clean EOF should not throw.
        using var lexer = PipeBytesLexer.FromString("12 + 34", ArithTable(),
            errorMode: LexerErrorMode.Throw,
            cancellationToken: TestContext.Current.CancellationToken);
        var tokens = await CollectAsync(lexer);
        Assert.Equal(3, tokens.Count);
    }
}
