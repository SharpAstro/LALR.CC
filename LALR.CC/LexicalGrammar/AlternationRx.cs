using System;
using System.Collections.Generic;
using System.Linq;

namespace LALR.CC.LexicalGrammar;

/// <summary>
/// Regex alternation — <c>A | B | C</c> — matches any one of its
/// alternatives. Compiled to an NFA via Thompson construction: a fresh
/// start state with ε-edges to each alternative's start, and a fresh
/// accept state with ε-edges from each alternative's accept.
/// </summary>
/// <remarks>
/// Alternation lives at the regex-AST level for cases where the lexer
/// can't easily be split into multiple rules — most prominently
/// string-literal patterns like <c>"(\\.|[^"\\])*"</c> where the
/// alternation is INSIDE the repeated atom. Outer-level alternation
/// can still be expressed by giving the lexer multiple rules with the
/// same accept name (longest match wins, first rule wins on ties).
/// </remarks>
public readonly struct AlternationRx : IRx
{
    private readonly IRx[] _alternatives;

    public AlternationRx(params IRx[] alternatives)
    {
        ArgumentNullException.ThrowIfNull(alternatives);
        if (alternatives.Length < 2)
        {
            throw new ArgumentException(
                "Alternation requires at least two alternatives.",
                nameof(alternatives));
        }
        _alternatives = alternatives;
    }

    internal IReadOnlyList<IRx> Alternatives => _alternatives;

    public string Pattern => string.Join("|", _alternatives.Select(a => a.Pattern));

    public override string ToString() => Pattern;
}
