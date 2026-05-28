using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LALR.CC.SourceGenerators;

/// <summary>
/// Phase 5 / slice 6: parser-side port of <c>IRxParser.Parse</c>. Walks the same
/// regex dialect (literals + escapes + character classes + quantifiers + groups,
/// no alternation) and emits C# constructor expressions that re-build the
/// runtime <see cref="LexicalGrammar.IRx"/> tree at consumer init time. Lets the
/// generator pre-bake <c>LexRule[]</c> arrays without linking the runtime
/// regex sources into the netstandard2.0 generator (those use
/// <c>ArgumentNullException.ThrowIfNull</c> / <c>HashCode</c> / range syntax
/// that wouldn't compile here cleanly).
/// </summary>
/// <remarks>
/// Behaviour parity with the runtime parser is required — the lexer's longest-
/// match / first-rule-wins semantics depend on the IRx tree shape. Any
/// divergence between the two parsers will silently change lex behaviour. We
/// surface every parse error as <see cref="RegexFormatException"/>; the caller
/// (LexerEmitter / GrammarSourceGenerator) turns it into <c>LALR0005</c>.
/// </remarks>
internal static class RegexEmitter
{
    private const string IRxFqn = "global::LALR.CC.LexicalGrammar.IRx";
    private const string SingleCharFqn = "global::LALR.CC.LexicalGrammar.ISingleCharRx";
    private const string CharRxFqn = "global::LALR.CC.LexicalGrammar.CharRx";
    private const string CharRangeRxFqn = "global::LALR.CC.LexicalGrammar.CharRangeRx";
    private const string CharClassRxFqn = "global::LALR.CC.LexicalGrammar.CharClassRx";
    private const string GroupRxFqn = "global::LALR.CC.LexicalGrammar.GroupRx";
    private const string AlternationRxFqn = "global::LALR.CC.LexicalGrammar.AlternationRx";
    private const string MultiplicityFqn = "global::LALR.CC.LexicalGrammar.Multiplicity";

    /// <summary>
    /// Parse <paramref name="pattern"/> and return a C# expression that, when
    /// evaluated, builds the equivalent <see cref="LexicalGrammar.IRx"/> tree.
    /// Throws <see cref="RegexFormatException"/> on malformed input; the message
    /// mirrors the runtime parser's diagnostics so the user sees the same
    /// vocabulary regardless of which side caught the error.
    /// </summary>
    public static string Emit(string pattern)
    {
        if (pattern == null)
        {
            throw new ArgumentNullException(nameof(pattern));
        }
        if (pattern.Length == 0)
        {
            throw new RegexFormatException("empty pattern", 0, pattern);
        }
        var state = new ParserState(pattern);
        var rx = ParseAlternation(state);
        if (state.HasMore)
        {
            throw Fmt(state, $"unexpected '{state.Current}'");
        }
        return rx;
    }

    /// <summary>
    /// Lowest-precedence form: <c>concat ('|' concat)*</c>. Mirrors
    /// <c>IRxParser.ParseAlternation</c> in the runtime — both must
    /// stay in lockstep or the lexer behaves differently between the
    /// generator-side pre-bake and the runtime fallback.
    /// </summary>
    private static string ParseAlternation(ParserState p)
    {
        var first = ParseConcat(p);
        if (!p.HasMore || p.Current != '|')
        {
            return first;
        }
        var branches = new List<string> { first };
        while (p.HasMore && p.Current == '|')
        {
            p.Pos++; // consume '|'
            branches.Add(ParseConcat(p));
        }
        var sb = new StringBuilder();
        sb.Append("new ").Append(AlternationRxFqn).Append('(');
        for (var i = 0; i < branches.Count; i++)
        {
            if (i > 0) { sb.Append(", "); }
            sb.Append(branches[i]);
        }
        sb.Append(')');
        return sb.ToString();
    }

    private sealed class ParserState
    {
        public readonly string Source;
        public int Pos;

        public ParserState(string source)
        {
            Source = source;
            Pos = 0;
        }

        public bool HasMore => Pos < Source.Length;
        public char Current => Source[Pos];

        public char Consume() => Source[Pos++];

        public bool TryConsume(char c)
        {
            if (HasMore && Current == c)
            {
                Pos++;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Parse a concatenation. Returns a C# expression of static type
    /// <c>IRx</c> — either a single atom (when there's one) or a
    /// <c>GroupRx(Multiplicity.Once, ...)</c> wrapping the sequence.
    /// </summary>
    private static string ParseConcat(ParserState p)
    {
        var atoms = new List<string>();
        while (p.HasMore && p.Current != ')' && p.Current != '|')
        {
            atoms.Add(ParseAtom(p));
        }
        if (atoms.Count == 0)
        {
            throw Fmt(p, "empty (sub)expression");
        }
        if (atoms.Count == 1)
        {
            return atoms[0];
        }
        var sb = new StringBuilder();
        sb.Append("new ").Append(GroupRxFqn).Append('(').Append(MultiplicityFqn).Append(".Once");
        foreach (var atom in atoms)
        {
            sb.Append(", ").Append(atom);
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static string ParseAtom(ParserState p)
    {
        string atom;
        switch (p.Current)
        {
            case '(':
                atom = ParseGroup(p);
                break;
            case '[':
                atom = ParseClass(p);
                break;
            case '.':
                // `.` is "any codepoint except newline" — emit the same negated
                // single-char class the runtime parser produces, so the generator-
                // pre-baked IRx tree is bit-for-bit identical to the runtime parse.
                p.Pos++;
                atom = "new " + CharClassRxFqn + "(false, new " + SingleCharFqn
                    + "[] { " + EmitCharRx('\n') + " })";
                break;
            case '|':
                // ParseAlternation / ParseConcat should consume '|' at the
                // right boundary; hitting it here means an alternative is
                // missing (e.g. `a||b` or `|a` or `(|a)`).
                throw Fmt(p, "missing alternative — '|' must separate non-empty expressions");
            case ')':
            case '?':
            case '+':
            case '*':
                throw Fmt(p, $"unexpected '{p.Current}'");
            case '{':
                throw Fmt(p, "'{' without preceding atom");
            case '\\':
                p.Pos++;
                if (!p.HasMore)
                {
                    throw new RegexFormatException("trailing backslash", p.Pos, p.Source);
                }
                atom = EmitCharRx(ConsumeEscapedChar(p));
                break;
            default:
                atom = EmitCharRx(ConsumeChar(p));
                break;
        }

        if (p.HasMore && IsQuantifierChar(p.Current))
        {
            var multiplicity = ParseQuantifier(p);
            // Mirror the runtime parser's GroupRx wrapping: any quantifier on an
            // atom (single-char or group) becomes a one-element GroupRx.
            atom = "new " + GroupRxFqn + "(" + multiplicity + ", " + atom + ")";
        }
        return atom;
    }

    private static string ParseGroup(ParserState p)
    {
        p.Pos++; // consume '('
        // Recurse into ParseAlternation so a group bounds the scope of
        // any inner '|' — same as the runtime parser.
        var inner = ParseAlternation(p);
        if (!p.TryConsume(')'))
        {
            throw Fmt(p, "expected ')'");
        }
        return inner;
    }

    private static string ParseClass(ParserState p)
    {
        p.Pos++; // consume '['
        var positive = !p.TryConsume('^');
        var items = new List<string>(); // each entry is a C# expression of static type ISingleCharRx
        while (p.HasMore && p.Current != ']')
        {
            items.Add(ParseClassItem(p));
        }
        if (!p.TryConsume(']'))
        {
            throw new RegexFormatException("unterminated character class", p.Pos, p.Source);
        }
        if (items.Count == 0)
        {
            throw new RegexFormatException("empty character class", p.Pos, p.Source);
        }
        var sb = new StringBuilder();
        sb.Append("new ").Append(CharClassRxFqn).Append('(').Append(positive ? "true" : "false")
          .Append(", new ").Append(SingleCharFqn).Append("[] { ");
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(items[i]);
        }
        sb.Append(" })");
        return sb.ToString();
    }

    /// <summary>
    /// Parse one atom inside a character class. May be a single char (returns
    /// <c>new CharRx(...)</c>) or a range (returns <c>new CharRangeRx(...)</c>).
    /// Both are <c>ISingleCharRx</c> at the type level so the caller can stuff
    /// them into a <c>new ISingleCharRx[] { ... }</c>.
    /// </summary>
    private static string ParseClassItem(ParserState p)
    {
        var first = ConsumeClassChar(p);
        if (p.HasMore && p.Current == '-' && p.Pos + 1 < p.Source.Length && p.Source[p.Pos + 1] != ']')
        {
            p.Pos++; // consume '-'
            var second = ConsumeClassChar(p);
            return "new " + CharRangeRxFqn + "(" + EmitCharRx(first) + ", " + EmitCharRx(second) + ")";
        }
        return EmitCharRx(first);
    }

    private static int ConsumeClassChar(ParserState p)
    {
        if (p.Current == '\\')
        {
            p.Pos++;
            if (!p.HasMore)
            {
                throw new RegexFormatException("trailing backslash in character class", p.Pos, p.Source);
            }
            return ConsumeEscapedChar(p);
        }
        return ConsumeChar(p);
    }

    private static int ConsumeChar(ParserState p)
    {
        var c = p.Source[p.Pos];
        if (char.IsHighSurrogate(c) && p.Pos + 1 < p.Source.Length && char.IsLowSurrogate(p.Source[p.Pos + 1]))
        {
            var cp = char.ConvertToUtf32(c, p.Source[p.Pos + 1]);
            p.Pos += 2;
            return cp;
        }
        p.Pos++;
        return c;
    }

    private static int ConsumeEscapedChar(ParserState p)
    {
        var c = p.Consume();
        switch (c)
        {
            case 'n': return '\n';
            case 'r': return '\r';
            case 't': return '\t';
            case '\\':
            case '.':
            case '[':
            case ']':
            case '(':
            case ')':
            case '{':
            case '}':
            case '?':
            case '+':
            case '*':
            case '^':
            case '$':
            case '-':
            case '|':
            case '/':
                return c;
            default:
                throw new RegexFormatException(
                    "unknown escape '\\" + c + "' at position " + (p.Pos - 1),
                    p.Pos - 1,
                    p.Source);
        }
    }

    private static bool IsQuantifierChar(char c) => c == '?' || c == '+' || c == '*' || c == '{';

    private static string ParseQuantifier(ParserState p)
    {
        var c = p.Consume();
        switch (c)
        {
            case '?': return MultiplicityFqn + ".ZeroOrOnce";
            case '+': return MultiplicityFqn + ".OneOrMore";
            case '*': return MultiplicityFqn + ".ZeroOrMore";
            case '{':
                var from = ParseDigits(p);
                if (p.TryConsume('}'))
                {
                    return "new " + MultiplicityFqn + "(" + from.ToString(CultureInfo.InvariantCulture) + ")";
                }
                if (!p.TryConsume(','))
                {
                    throw Fmt(p, "expected ',' or '}' in quantifier");
                }
                if (p.TryConsume('}'))
                {
                    return "new " + MultiplicityFqn + "(" + from.ToString(CultureInfo.InvariantCulture) + ", -1)";
                }
                var to = ParseDigits(p);
                if (!p.TryConsume('}'))
                {
                    throw Fmt(p, "expected '}' in quantifier");
                }
                return "new " + MultiplicityFqn + "("
                    + from.ToString(CultureInfo.InvariantCulture) + ", "
                    + to.ToString(CultureInfo.InvariantCulture) + ")";
            default:
                throw Fmt(p, "unexpected quantifier '" + c + "'");
        }
    }

    private static int ParseDigits(ParserState p)
    {
        var start = p.Pos;
        while (p.HasMore && IsAsciiDigit(p.Current))
        {
            p.Pos++;
        }
        if (p.Pos == start)
        {
            throw Fmt(p, "expected digit");
        }
        return int.Parse(p.Source.Substring(start, p.Pos - start), CultureInfo.InvariantCulture);
    }

    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

    /// <summary>
    /// Build a <c>new CharRx(int)</c> expression. The runtime ctor takes a
    /// codepoint, so we always emit the integer literal — keeps Unicode
    /// astral-plane chars and ASCII control chars equally readable in the
    /// generated source.
    /// </summary>
    private static string EmitCharRx(int codepoint) =>
        "new " + CharRxFqn + "(" + codepoint.ToString(CultureInfo.InvariantCulture) + ")";

    private static RegexFormatException Fmt(ParserState p, string what) =>
        new RegexFormatException(
            what + " at position " + p.Pos.ToString(CultureInfo.InvariantCulture)
                + " in pattern \"" + p.Source + "\"",
            p.Pos,
            p.Source);
}

/// <summary>
/// Generator-side regex parse failure. Carries the offending position +
/// source so the LALR0005 diagnostic can quote both. Mirrors the
/// <c>FormatException</c> the runtime parser throws — same vocabulary,
/// different exception type so we don't have to import the runtime's
/// <c>System.FormatException</c> usage convention.
/// </summary>
internal sealed class RegexFormatException : System.Exception
{
    public RegexFormatException(string message, int position, string source) : base(message)
    {
        Position = position;
        Source = source;
    }
    public int Position { get; }
    public new string Source { get; }
}
