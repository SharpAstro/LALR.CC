using System.Collections.Generic;
using LALR.CC.LexicalGrammar;
using Xunit;

namespace LALR.CC.Tests;

/// <summary>
/// Sync-path mirror of <see cref="PipeBytesLexerTests"/>. Same tokens, same
/// rules, same expected outputs — just driven through <see cref="BytesLexer"/>
/// instead of <see cref="PipeBytesLexer"/>. The two paths must agree on every
/// non-trivial input; divergence here would mean the sync DFA loop rounds
/// off something the async one doesn't (or vice versa).
/// </summary>
public class BytesLexerTests
{
    private const int Number = 10;
    private const int Plus = 11;
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

    private static List<Item> Collect(BytesLexer lexer)
    {
        var tokens = new List<Item>();
        while (lexer.MoveNext())
        {
            tokens.Add(lexer.Current);
        }
        return tokens;
    }

    [Fact]
    public void SingleNumber_EmitsOneToken()
    {
        using var lexer = BytesLexer.FromString("42", ArithTable());
        var tokens = Collect(lexer);
        Assert.Single(tokens);
        Assert.Equal(Number, tokens[0].ID);
        Assert.Equal("42", tokens[0].Content);
    }

    [Fact]
    public void NumbersWithWhitespace_IgnoresWhitespace()
    {
        using var lexer = BytesLexer.FromString("12  34\t56\n78", ArithTable());
        var tokens = Collect(lexer);
        // Four numbers, no whitespace tokens (#ignore drops them).
        Assert.Equal(4, tokens.Count);
        foreach (var t in tokens)
        {
            Assert.Equal(Number, t.ID);
        }
    }

    [Fact]
    public void Expression_TokenizesNumbersAndPlus()
    {
        using var lexer = BytesLexer.FromString("1 + 2 + 3", ArithTable());
        var tokens = Collect(lexer);
        Assert.Equal(5, tokens.Count);
        Assert.Equal(Number, tokens[0].ID);
        Assert.Equal(Plus, tokens[1].ID);
        Assert.Equal(Number, tokens[2].ID);
        Assert.Equal(Plus, tokens[3].ID);
        Assert.Equal(Number, tokens[4].ID);
    }

    [Fact]
    public void Position_TracksLineAndColumnInBytes()
    {
        // Same position semantics as PipeBytesLexer — 1-based line and byte column.
        using var lexer = BytesLexer.FromString("12\n34", ArithTable());
        var tokens = Collect(lexer);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(1, tokens[0].Position.Line);
        Assert.Equal(1, tokens[0].Position.Column);
        Assert.Equal(2, tokens[1].Position.Line);
        Assert.Equal(1, tokens[1].Position.Column);
    }

    [Fact]
    public void InitialLine_ShiftsLineBase()
    {
        // A lexer started at a high line base (mimicking a synthetic-header
        // sub-lexer in a reserved line band) reports every token at or past
        // that base; column and byte offset are unaffected, and a newline
        // bumps the line relative to the base.
        const int Base = 1 << 20;
        using var lexer = BytesLexer.FromString("12\n34", ArithTable(), initialLine: Base);
        var tokens = Collect(lexer);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(Base, tokens[0].Position.Line);
        Assert.Equal(1, tokens[0].Position.Column);
        Assert.Equal(Base + 1, tokens[1].Position.Line);
        Assert.Equal(1, tokens[1].Position.Column);
        Assert.Equal(3L, tokens[1].Position.ByteOffset);
    }

    [Fact]
    public void InitialLine_BelowOne_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => BytesLexer.FromString("1", ArithTable(), initialLine: 0));
    }

    [Fact]
    public void Utf8MultiByte_DefaultIsCodepointColumn()
    {
        // Default ColumnMode is Codepoints — "𝟏" (U+1D7CF) is one codepoint
        // even though it's 4 UTF-8 bytes. A digit following it would sit at
        // column 2, byte offset 4.
        var rules = new LexRule[]
        {
            new(Number, new CharClassRx(true, [new CharRangeRx(0, 0x10FFFF)])),
        };
        var table = new Dictionary<string, LexRule[]> { { PipeBytesLexer.RootState, rules } };
        using var lexer = BytesLexer.FromString("𝟏7", table);
        var tokens = Collect(lexer);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(1, tokens[0].Position.Column);
        // 1 codepoint past 𝟏 — column 2, byte offset 4.
        Assert.Equal(2, tokens[1].Position.Column);
        Assert.Equal(4L, tokens[1].Position.ByteOffset);
    }

    [Fact]
    public void Utf8MultiByte_BytesMode_ColumnIsByteCount()
    {
        // Opt into Bytes mode: 𝟏's 4 UTF-8 bytes contribute 4 to the column.
        var rules = new LexRule[]
        {
            new(Number, new CharClassRx(true, [new CharRangeRx(0, 0x10FFFF)])),
        };
        var table = new Dictionary<string, LexRule[]> { { PipeBytesLexer.RootState, rules } };
        using var lexer = BytesLexer.FromString("𝟏7", table,
            columnMode: ColumnMode.Bytes);
        var tokens = Collect(lexer);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(1, tokens[0].Position.Column);
        Assert.Equal(5, tokens[1].Position.Column); // 1 + 4 bytes of 𝟏 = byte column 5.
    }

    [Fact]
    public void Throw_ErrorMode_RaisesOnBadByte()
    {
        // No rule matches '?'; default error mode is Throw → LexerException.
        using var lexer = BytesLexer.FromString("12?34", ArithTable());
        Assert.Throws<LexerException>(() =>
        {
            while (lexer.MoveNext()) { /* drain */ }
        });
    }

    [Fact]
    public void EmitAndSkip_ErrorMode_EmitsErrorTokenAndContinues()
    {
        const int ErrSym = 99;
        using var lexer = BytesLexer.FromString("1?2", ArithTable(), LexerErrorMode.EmitAndSkip, ErrSym);
        var tokens = Collect(lexer);
        Assert.Equal(3, tokens.Count);
        Assert.Equal(Number, tokens[0].ID);
        Assert.Equal(ErrSym, tokens[1].ID);
        Assert.True(tokens[1].IsError);
        Assert.Equal(Number, tokens[2].ID);
    }

    [Fact]
    public void NoMoveNext_CurrentThrows()
    {
        using var lexer = BytesLexer.FromString("42", ArithTable());
        Assert.Throws<System.InvalidOperationException>(() => _ = lexer.Current);
    }
}
