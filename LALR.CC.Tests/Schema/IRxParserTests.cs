using System;
using System.Text;
using LALR.CC.LexicalGrammar;
using LALR.CC.LexicalGrammar.Dfa;
using LALR.CC.Schema;
using Xunit;

namespace LALR.CC.Tests.Schema;

public class IRxParserTests
{
    /// <summary>
    /// Compile an <see cref="IRx"/> through the byte DFA pipeline and return
    /// (patternId, byteLength) for the given UTF-8 input. This is the test's
    /// way of saying "the parsed pattern actually matches what we'd expect".
    /// </summary>
    private static (int PatternId, int Length) MatchBytes(IRx pattern, string input)
    {
        var dfa = Utf8DfaLowering.Lower(DfaCompiler.Compile(pattern, 1));
        return dfa.LongestMatchBytes(Encoding.UTF8.GetBytes(input));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Literals
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a", "a", 1)]
    [InlineData("ab", "ab", 2)]
    [InlineData("foo", "foobar", 3)]
    public void LiteralChars_Match(string pattern, string input, int expectedLen)
    {
        var rx = IRxParser.Parse(pattern);
        var (id, len) = MatchBytes(rx, input);
        Assert.Equal(1, id);
        Assert.Equal(expectedLen, len);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Escapes
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"\n", "\n")]
    [InlineData(@"\r", "\r")]
    [InlineData(@"\t", "\t")]
    [InlineData(@"\\", "\\")]
    [InlineData(@"\.", ".")]
    [InlineData(@"\[", "[")]
    [InlineData(@"\]", "]")]
    [InlineData(@"\(", "(")]
    [InlineData(@"\)", ")")]
    [InlineData(@"\{", "{")]
    [InlineData(@"\}", "}")]
    [InlineData(@"\?", "?")]
    [InlineData(@"\+", "+")]
    [InlineData(@"\*", "*")]
    [InlineData(@"\^", "^")]
    [InlineData(@"\$", "$")]
    [InlineData(@"\-", "-")]
    [InlineData(@"\|", "|")]
    public void Escapes_MatchTheEscapedChar(string pattern, string input)
    {
        var rx = IRxParser.Parse(pattern);
        var (id, len) = MatchBytes(rx, input);
        Assert.Equal(1, id);
        Assert.True(len > 0);
    }

    [Fact]
    public void TrailingBackslash_Throws()
    {
        Assert.Throws<FormatException>(() => IRxParser.Parse(@"a\"));
    }

    [Fact]
    public void UnknownEscape_Throws()
    {
        Assert.Throws<FormatException>(() => IRxParser.Parse(@"\z"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Character classes
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PositiveClass_MatchesMembers()
    {
        var rx = IRxParser.Parse("[abc]");
        Assert.Equal((1, 1), MatchBytes(rx, "a"));
        Assert.Equal((1, 1), MatchBytes(rx, "b"));
        Assert.Equal((1, 1), MatchBytes(rx, "c"));
        Assert.Equal((-1, 0), MatchBytes(rx, "d"));
    }

    [Fact]
    public void NegativeClass_RejectsMembers()
    {
        var rx = IRxParser.Parse("[^abc]");
        Assert.Equal((-1, 0), MatchBytes(rx, "a"));
        Assert.Equal((1, 1), MatchBytes(rx, "z"));
        Assert.Equal((1, 1), MatchBytes(rx, "0"));
    }

    [Fact]
    public void RangeInClass_MatchesAcrossRange()
    {
        var rx = IRxParser.Parse("[a-z]");
        Assert.Equal((1, 1), MatchBytes(rx, "a"));
        Assert.Equal((1, 1), MatchBytes(rx, "m"));
        Assert.Equal((1, 1), MatchBytes(rx, "z"));
        Assert.Equal((-1, 0), MatchBytes(rx, "A"));
        Assert.Equal((-1, 0), MatchBytes(rx, "0"));
    }

    [Fact]
    public void MixedClass_RangesPlusLiterals()
    {
        // Bootstrap-ish identifier set.
        var rx = IRxParser.Parse("[-A-Za-z0-9_]");
        Assert.Equal((1, 1), MatchBytes(rx, "a"));
        Assert.Equal((1, 1), MatchBytes(rx, "Z"));
        Assert.Equal((1, 1), MatchBytes(rx, "0"));
        Assert.Equal((1, 1), MatchBytes(rx, "_"));
        Assert.Equal((1, 1), MatchBytes(rx, "-"));
        Assert.Equal((-1, 0), MatchBytes(rx, "."));
    }

    [Fact]
    public void TrailingDashInClass_IsLiteral()
    {
        // [a-] is literal 'a' or literal '-' — not an unfinished range.
        var rx = IRxParser.Parse("[a-]");
        Assert.Equal((1, 1), MatchBytes(rx, "a"));
        Assert.Equal((1, 1), MatchBytes(rx, "-"));
        Assert.Equal((-1, 0), MatchBytes(rx, "b"));
    }

    [Fact]
    public void EscapesInsideClass_Work()
    {
        var rx = IRxParser.Parse(@"[\\\]\-]");   // backslash, ']', '-'
        Assert.Equal((1, 1), MatchBytes(rx, "\\"));
        Assert.Equal((1, 1), MatchBytes(rx, "]"));
        Assert.Equal((1, 1), MatchBytes(rx, "-"));
    }

    [Fact]
    public void UnterminatedClass_Throws()
    {
        Assert.Throws<FormatException>(() => IRxParser.Parse("[abc"));
    }

    [Fact]
    public void EmptyClass_Throws()
    {
        Assert.Throws<FormatException>(() => IRxParser.Parse("[]"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Quantifiers
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a?", "", 0)]
    [InlineData("a?", "a", 1)]
    [InlineData("a?", "aa", 1)]
    [InlineData("a*", "", 0)]
    [InlineData("a*", "aaaa", 4)]
    [InlineData("a+", "a", 1)]
    [InlineData("a+", "aaaa", 4)]
    [InlineData("a{3}", "aaa", 3)]
    [InlineData("a{2,4}", "aaaaa", 4)]
    [InlineData("a{2,}", "aaaaa", 5)]
    public void Quantifiers_MatchAsExpected(string pattern, string input, int expectedLen)
    {
        var rx = IRxParser.Parse(pattern);
        var (_, len) = MatchBytes(rx, input);
        Assert.Equal(expectedLen, len);
    }

    [Fact]
    public void PlusWithoutAtom_Throws()
    {
        Assert.Throws<FormatException>(() => IRxParser.Parse("+"));
    }

    [Fact]
    public void BraceWithoutAtom_Throws()
    {
        Assert.Throws<FormatException>(() => IRxParser.Parse("{2}"));
    }

    [Fact]
    public void BadBraceQuantifier_Throws()
    {
        Assert.Throws<FormatException>(() => IRxParser.Parse("a{}"));
        Assert.Throws<FormatException>(() => IRxParser.Parse("a{2"));
        Assert.Throws<FormatException>(() => IRxParser.Parse("a{2,3"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Groups
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GroupedRepetition_Works()
    {
        // (ab)+ matches "ab", "abab", "ababab", but not "aba" past 2 chars.
        var rx = IRxParser.Parse("(ab)+");
        Assert.Equal(2, MatchBytes(rx, "ab").Length);
        Assert.Equal(4, MatchBytes(rx, "abab").Length);
        Assert.Equal(2, MatchBytes(rx, "abc").Length);
        Assert.Equal(0, MatchBytes(rx, "ba").Length);
    }

    [Fact]
    public void UnmatchedOpenParen_Throws()
    {
        Assert.Throws<FormatException>(() => IRxParser.Parse("(ab"));
    }

    [Fact]
    public void StrayCloseParen_Throws()
    {
        // ParseConcat returns at first ')'; the trailing ')' is unconsumed → top-level
        // throws "unexpected ')'".
        Assert.Throws<FormatException>(() => IRxParser.Parse("ab)"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Alternation
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Alternation_TwoLiterals_BuildsAlternationNode()
    {
        var rx = IRxParser.Parse("a|b");
        var alt = Assert.IsType<AlternationRx>(rx);
        Assert.Equal("a|b", alt.Pattern);
    }

    [Fact]
    public void Alternation_ThreeWay_PreservesOrder()
    {
        var rx = IRxParser.Parse("a|b|c");
        var alt = Assert.IsType<AlternationRx>(rx);
        Assert.Equal("a|b|c", alt.Pattern);
    }

    [Fact]
    public void Alternation_InsideGroup_BoundedToGroup()
    {
        // `a(b|c)d` is a-then-(b-or-c)-then-d, NOT (a-then-b)-or-(c-then-d).
        // The semantic claim: the outer node is a concat group of three
        // items, NOT an alternation. (GroupRx.Pattern's historical
        // formatting wraps multi-item Once groups in parens, so the
        // formatted string is `(a(b|c)d)` — the round-trip parses back
        // to the same shape, but the formatting is the pre-existing
        // convention rather than the input verbatim.)
        var rx = IRxParser.Parse("a(b|c)d");
        Assert.IsNotType<AlternationRx>(rx);
    }

    [Fact]
    public void Alternation_InRepetition_TheStringLiteralPattern()
    {
        // The canonical C string-literal pattern: `"(\\.|[^"\\])*"`.
        // Without alternation inside the (…)* you can't express this.
        var rx = IRxParser.Parse("\"(\\\\.|[^\"\\\\])*\"");
        // Compiles to a DFA without error — that's the load-bearing assertion.
        var dfa = DfaCompiler.Compile(rx, patternId: 0);
        Assert.NotNull(dfa);
    }

    [Fact]
    public void Alternation_BackslashPipe_Literal()
    {
        // `\|` is the literal pipe character (escape), not the alternation operator.
        var rx = IRxParser.Parse("a\\|b");
        Assert.IsNotType<AlternationRx>(rx);
    }

    [Fact]
    public void Alternation_EmptyAlternative_Throws()
    {
        // `a||b` and `|a` and `a|` are all malformed.
        Assert.Throws<FormatException>(() => IRxParser.Parse("a||b"));
        Assert.Throws<FormatException>(() => IRxParser.Parse("|a"));
        Assert.Throws<FormatException>(() => IRxParser.Parse("a|"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Any-char wildcard `.`
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dot_MatchesAnyCharExceptNewline()
    {
        var rx = IRxParser.Parse(".");
        Assert.Equal(1, MatchBytes(rx, "a").Length);
        Assert.Equal(1, MatchBytes(rx, "Z").Length);
        Assert.Equal(1, MatchBytes(rx, "0").Length);
        Assert.Equal(1, MatchBytes(rx, " ").Length);
        Assert.Equal(1, MatchBytes(rx, "!").Length);
        // CR matches (only LF is excluded — same as PCRE default)
        Assert.Equal(1, MatchBytes(rx, "\r").Length);
        // LF does not match
        Assert.Equal(0, MatchBytes(rx, "\n").Length);
    }

    [Fact]
    public void Dot_WithStar_MatchesNonNewlineRun()
    {
        // `.*` is the "rest of line" idiom — eats everything until a newline.
        var rx = IRxParser.Parse(".*");
        Assert.Equal(11, MatchBytes(rx, "hello world").Length);
        Assert.Equal(5, MatchBytes(rx, "hello\nworld").Length);
        Assert.Equal(0, MatchBytes(rx, "\nrest").Length);
    }

    [Fact]
    public void Dot_InLineCommentPattern()
    {
        // `//.*` — the canonical C++/C99 single-line comment shape.
        // `.*` stops at `\n`, so the LF is NOT included in the match.
        var rx = IRxParser.Parse("//.*");
        Assert.Equal(12, MatchBytes(rx, "// a comment\n").Length);
        Assert.Equal(2, MatchBytes(rx, "//").Length);
        Assert.Equal(0, MatchBytes(rx, "/* not me */").Length);
    }

    [Fact]
    public void EscapedDot_IsLiteral()
    {
        // `\.` matches a literal dot — sanity check that the escape table
        // still wins over the new `.` atom path.
        var rx = IRxParser.Parse(@"\.");
        Assert.Equal(1, MatchBytes(rx, ".").Length);
        Assert.Equal(0, MatchBytes(rx, "a").Length);
    }

    [Fact]
    public void Dot_InsideCharClass_IsLiteral()
    {
        // `[.]` is a literal dot — character classes don't treat `.` as
        // the wildcard metachar.
        var rx = IRxParser.Parse("[.]");
        Assert.Equal(1, MatchBytes(rx, ".").Length);
        Assert.Equal(0, MatchBytes(rx, "a").Length);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Real Bootstrap-style patterns
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EolPattern_OptionalCrThenLf()
    {
        // \r?\n
        var rx = IRxParser.Parse(@"\r?\n");
        Assert.Equal(1, MatchBytes(rx, "\n").Length);
        Assert.Equal(2, MatchBytes(rx, "\r\n").Length);
        Assert.Equal(0, MatchBytes(rx, "abc").Length);
    }

    [Fact]
    public void IdentifierPattern_Letters_Digits_Underscores()
    {
        var rx = IRxParser.Parse("[a-zA-Z_][a-zA-Z0-9_]*");
        Assert.Equal(11, MatchBytes(rx, "rule_name42").Length);
        Assert.Equal(3, MatchBytes(rx, "x42 abc").Length);
        Assert.Equal(0, MatchBytes(rx, "9bad").Length);
    }

    [Fact]
    public void EmptyPattern_Throws()
    {
        Assert.Throws<FormatException>(() => IRxParser.Parse(""));
    }

    [Fact]
    public void NullPattern_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => IRxParser.Parse(null));
    }
}
