using System.Collections.Generic;
using LALR.CC.LexicalGrammar;

namespace LALR.CC;

/// <summary>
/// One recovered parse error from <see cref="Parser.ParseInputResilient"/>: the token that
/// had no valid action, the state it occurred in, the symbol ids that <em>would</em> have been
/// valid there, and the source span the recovery skipped to resynchronise. The same data a
/// <see cref="ParseErrorException"/> carries, captured instead of thrown so the parse can
/// continue and surface every broken region rather than only the first.
/// </summary>
public sealed class ParseErrorInfo
{
    /// <summary>The lookahead token whose id had no valid action at <see cref="State"/>.</summary>
    public Item OffendingToken { get; }

    /// <summary>LALR(1) parser state at which the error occurred.</summary>
    public int State { get; }

    /// <summary>Symbol ids that were valid lookaheads at <see cref="State"/> (-1 = end-of-input).</summary>
    public IReadOnlyList<int> ExpectedSymbolIds { get; }

    /// <summary>Human-readable diagnostic (same shape as <see cref="ParseErrorException.Message"/>).</summary>
    public string Message { get; }

    /// <summary>Where recovery began skipping — the offending token's position.</summary>
    public SourcePosition SkippedFrom { get; }

    /// <summary>The position parsing resumed at — the sync token recovery resynchronised to,
    /// or <see cref="SourcePosition.Unknown"/> when recovery ran to end-of-input.</summary>
    public SourcePosition ResumedAt { get; }

    public ParseErrorInfo(Item offendingToken, int state, IReadOnlyList<int> expectedSymbolIds,
        string message, SourcePosition skippedFrom, SourcePosition resumedAt)
    {
        OffendingToken = offendingToken;
        State = state;
        ExpectedSymbolIds = expectedSymbolIds;
        Message = message;
        SkippedFrom = skippedFrom;
        ResumedAt = resumedAt;
    }

    public override string ToString() => Message;
}

/// <summary>
/// The outcome of <see cref="Parser.ParseInputResilient"/>: the reduced tree over the input's
/// <em>well-formed</em> top-level list elements, plus one <see cref="ParseErrorInfo"/> per element
/// that failed to parse and was skipped. When <see cref="Errors"/> is empty the parse was clean and
/// <see cref="Tree"/> is identical to what <see cref="Parser.ParseInput"/> would return.
/// </summary>
/// <remarks>
/// Recovery is <em>list-boundary</em> panic mode: on a parse error the driver records the error,
/// discards the offending element's remaining tokens (tracking bracket depth so a nested
/// list-starter isn't mistaken for a boundary), pops the stack to a state that can continue the
/// list, and resumes. A broken element is <em>absent</em> from <see cref="Tree"/> — recovery never
/// injects a synthetic node — so consumers get a tree of exactly the elements that parsed, and the
/// skipped ones as <see cref="Errors"/>. This matches the lazy-analysis model where only referenced
/// elements need to be well-formed.
/// </remarks>
public sealed class ResilientParseResult
{
    /// <summary>The reduced tree over the well-formed list elements (see remarks).</summary>
    public Item Tree { get; }

    /// <summary>One entry per skipped (unparseable) list element, in source order.</summary>
    public IReadOnlyList<ParseErrorInfo> Errors { get; }

    public ResilientParseResult(Item tree, IReadOnlyList<ParseErrorInfo> errors)
    {
        Tree = tree;
        Errors = errors;
    }
}
