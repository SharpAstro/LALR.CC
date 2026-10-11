using System;

namespace LALR.CC.LexicalGrammar;

/// <summary>
/// One entry in a lexer state's pattern table for <see cref="PipeBytesLexer"/>.
/// On match, the lexer emits an <see cref="Item"/> with id <see cref="SymbolId"/>
/// and content equal to the UTF-8 decoded matched bytes; the
/// <see cref="Instruction"/> drives the state stack:
/// <list type="bullet">
/// <item>null/empty: stay in the current state</item>
/// <item><see cref="PipeBytesLexer.Ignore"/>: drop the matched token entirely</item>
/// <item><see cref="PipeBytesLexer.PopState"/>: pop one state off the stack</item>
/// <item>any other string: push that state name</item>
/// </list>
/// A rule with a <see cref="Lookahead"/> (trailing context, flex's <c>r/s</c>) matches its
/// <see cref="Pattern"/> only where the lookahead pattern matches right after it; the token is
/// the pattern's text alone, and the lookahead's bytes are scanned again as the next token.
/// </summary>
public readonly struct LexRule(int symbolId, IRx pattern, string instruction = null, IRx lookahead = null) : IEquatable<LexRule>
{
    public int SymbolId { get; } = symbolId;
    public IRx Pattern { get; } = pattern ?? throw new ArgumentNullException(nameof(pattern));
    public string Instruction { get; } = instruction;

    /// <summary>The trailing context that must follow a match, or null for none.</summary>
    public IRx Lookahead { get; } = lookahead;

    public bool Equals(LexRule other)
        => SymbolId == other.SymbolId && ReferenceEquals(Pattern, other.Pattern) && Instruction == other.Instruction
           && ReferenceEquals(Lookahead, other.Lookahead);

    public override bool Equals(object obj) => obj is LexRule other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(SymbolId, Pattern, Instruction, Lookahead);
    public static bool operator ==(LexRule a, LexRule b) => a.Equals(b);
    public static bool operator !=(LexRule a, LexRule b) => !a.Equals(b);
}
