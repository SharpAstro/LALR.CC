using System;
using System.Collections.Generic;
using System.Text;
using LALR.CC.LexicalGrammar.Dfa;

namespace LALR.CC.LexicalGrammar;

/// <summary>
/// Sync UTF-8 byte lexer over an in-memory <see cref="ReadOnlyMemory{Byte}"/>.
/// Sibling to <see cref="PipeBytesLexer"/>: same DFA-per-state compilation
/// (via <see cref="DfaCompiler.CompileMany"/> + <see cref="Utf8DfaLowering.Lower"/>),
/// same longest-match / first-rule-wins / instruction semantics, but
/// without the <c>PipeReader</c> + async machinery — a single contiguous
/// byte span, walked by index. Use this when the input is already in
/// memory; use <see cref="PipeBytesLexer"/> when you need streaming I/O.
/// </summary>
/// <remarks>
/// Drives the parser's sync loop (<see cref="Parser.ParseInput"/>) via
/// <see cref="SyncLATokenIterator"/>. The async path stays for stdin /
/// network / on-disk consumers — both ship.
/// </remarks>
public sealed class BytesLexer : ISyncIterator<Item>
{
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly Dictionary<string, CompiledState> _compiledStates;
    private readonly Stack<string> _states;
    private readonly LexerErrorMode _errorMode;
    private readonly int _errorSymbolId;
    private readonly ColumnMode _columnMode;
    private int _position;
    private Item _currentItem;

    private long _byteOffset;
    private int _line = 1;
    private int _column = 1;

    private readonly struct CompiledState(Dfa.Dfa byteDfa, LexRule[] rules)
    {
        public Dfa.Dfa ByteDfa { get; } = byteDfa;
        public LexRule[] Rules { get; } = rules;
    }

    /// <param name="initialLine">
    /// 1-based line number the first token reports (default 1). Lets a caller lex a
    /// nested buffer in a reserved line band — e.g. a synthetic-header sub-lexer that
    /// wants its tokens distinguishable by position from the main translation unit's.
    /// Column and byte offset still start at 1 and 0; only the line base shifts.
    /// </param>
    public BytesLexer(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, LexRule[]> patternTable,
        LexerErrorMode errorMode = LexerErrorMode.Throw,
        int errorSymbolId = -1,
        ColumnMode columnMode = ColumnMode.Codepoints,
        int initialLine = 1)
    {
        ArgumentNullException.ThrowIfNull(patternTable);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialLine, 1);
        if (!patternTable.ContainsKey(PipeBytesLexer.RootState))
        {
            throw new ArgumentException(
                $"pattern table must contain a '{PipeBytesLexer.RootState}' state",
                nameof(patternTable));
        }
        if (errorMode != LexerErrorMode.Throw && errorSymbolId < 0)
        {
            throw new ArgumentException(
                $"errorSymbolId must be non-negative when errorMode is {errorMode}",
                nameof(errorSymbolId));
        }

        _bytes = bytes;
        _errorMode = errorMode;
        _errorSymbolId = errorSymbolId;
        _columnMode = columnMode;
        _line = initialLine;
        _states = new Stack<string>([PipeBytesLexer.RootState]);
        _compiledStates = new Dictionary<string, CompiledState>(patternTable.Count, StringComparer.Ordinal);
        // Same compilation pass as PipeBytesLexer — first-rule-wins falls out of
        // assigning rule index as DFA pattern id, so the smallest accepting id
        // at any DFA state is the first matching rule.
        foreach (var kv in patternTable)
        {
            var rules = kv.Value;
            if (rules is null || rules.Length == 0)
            {
                throw new ArgumentException($"state '{kv.Key}' has no rules", nameof(patternTable));
            }
            var dfaPatterns = new (IRx, int)[rules.Length];
            for (var i = 0; i < rules.Length; i++)
            {
                dfaPatterns[i] = (rules[i].Pattern, i);
            }
            var codepointDfa = DfaCompiler.CompileMany(dfaPatterns);
            var byteDfa = Utf8DfaLowering.Lower(codepointDfa);
            _compiledStates[kv.Key] = new CompiledState(byteDfa, rules);
        }
    }

    /// <summary>Convenience: encode a UTF-16 string to UTF-8 once, then lex synchronously.</summary>
    /// <param name="initialLine">1-based starting line for the first token (default 1); see the constructor.</param>
    public static BytesLexer FromString(string text, IReadOnlyDictionary<string, LexRule[]> patternTable,
        LexerErrorMode errorMode = LexerErrorMode.Throw, int errorSymbolId = -1,
        ColumnMode columnMode = ColumnMode.Codepoints, int initialLine = 1)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new BytesLexer(Encoding.UTF8.GetBytes(text), patternTable, errorMode, errorSymbolId, columnMode, initialLine);
    }

    public Item Current
    {
        get
        {
            if (_currentItem is null)
            {
                throw new InvalidOperationException("Did not call MoveNext() yet!");
            }
            return _currentItem;
        }
    }

    public bool MoveNext()
    {
        // Outer loop restarts on #ignore so we keep scanning until we have a real token.
        while (true)
        {
            if (_position >= _bytes.Length)
            {
                return false;
            }

            var (advanced, token) = ScanOne();
            if (!advanced)
            {
                return false;
            }
            if (token is not null)
            {
                _currentItem = token;
                return true;
            }
            // token is null → #ignore; loop and try again
        }
    }

    /// <summary>
    /// Advance the lexer one DFA pass. Returns (advanced=false) on EOF /
    /// no-pattern. Returns (advanced=true, token=null) on a #ignore match —
    /// caller should restart. Returns (advanced=true, token=Item) on a
    /// normal match. Mirrors <see cref="PipeBytesLexer"/>'s ScanOneAsync
    /// semantics one-for-one.
    /// </summary>
    private (bool Advanced, Item Token) ScanOne()
    {
        var compiled = _compiledStates[_states.Peek()];
        var dfa = compiled.ByteDfa;
        var state = dfa.Start;
        var bestLen = 0;
        var bestPattern = dfa.States[state].IsAccept ? dfa.States[state].Accept : -1;
        var startPosition = new SourcePosition(_line, _column, _byteOffset);

        var span = _bytes.Span;
        for (var i = _position; i < span.Length; i++)
        {
            var next = dfa.Step(state, span[i]);
            if (next < 0)
            {
                break;
            }
            state = next;
            if (dfa.States[state].IsAccept)
            {
                bestLen = i - _position + 1;
                bestPattern = dfa.States[state].Accept;
            }
        }

        if (bestPattern >= 0 && bestLen > 0)
        {
            var matched = _bytes.Slice(_position, bestLen);
            var matchedString = Encoding.UTF8.GetString(matched.Span);
            AdvanceCursor(matched.Span);
            _position += bestLen;

            var rule = compiled.Rules[bestPattern];
            ApplyInstruction(rule.Instruction);

            return rule.Instruction == PipeBytesLexer.Ignore
                ? (true, null)
                : (true, new Item(rule.SymbolId, matchedString, startPosition));
        }

        // No match. Two distinct cases hide here:
        //   (a) clean EOF — already at end of input
        //   (b) unrecognized byte at _position
        if (_position >= span.Length)
        {
            return (false, null);
        }
        return HandleLexError(span[_position], startPosition);
    }

    private (bool Advanced, Item Token) HandleLexError(byte firstByte, SourcePosition errorPosition)
    {
        var stateName = _states.Peek();
        switch (_errorMode)
        {
            case LexerErrorMode.Throw:
                _position = _bytes.Length;
                throw new LexerException(errorPosition, firstByte, stateName);

            case LexerErrorMode.EmitAndStop:
                _position = _bytes.Length;
                return (true, MakeErrorItem(firstByte, errorPosition));

            case LexerErrorMode.EmitAndSkip:
                // Consume exactly one byte so the next scan starts past the offender.
                AdvanceCursor(_bytes.Span.Slice(_position, 1));
                _position++;
                return (true, MakeErrorItem(firstByte, errorPosition));

            default:
                throw new InvalidOperationException($"unhandled LexerErrorMode: {_errorMode}");
        }
    }

    private Item MakeErrorItem(byte offendingByte, SourcePosition position)
    {
        var content = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\\x{offendingByte:X2}");
        var item = new Item(_errorSymbolId, content, position);
        item.State = -1;
        return item;
    }

    private void AdvanceCursor(ReadOnlySpan<byte> matched)
    {
        // Mirror PipeBytesLexer.AdvanceCursor — same line/column/offset
        // semantics, just walking a single span instead of a sequence.
        // Codepoint mode skips UTF-8 continuation bytes (0b10xxxxxx) so each
        // codepoint contributes exactly 1 to the column; Bytes mode counts
        // every byte.
        var codepointMode = _columnMode == ColumnMode.Codepoints;
        for (var i = 0; i < matched.Length; i++)
        {
            var b = matched[i];
            _byteOffset++;
            if (b == (byte)'\n')
            {
                _line++;
                _column = 1;
            }
            else if (!codepointMode || (b & 0xC0) != 0x80)
            {
                _column++;
            }
        }
    }

    private void ApplyInstruction(string instruction)
    {
        if (string.IsNullOrEmpty(instruction) || instruction == PipeBytesLexer.Ignore)
        {
            return;
        }
        if (instruction == PipeBytesLexer.PopState)
        {
            _states.Pop();
            return;
        }
        if (!_compiledStates.ContainsKey(instruction))
        {
            throw new InvalidOperationException($"lexer rule pushed unknown state '{instruction}'");
        }
        _states.Push(instruction);
    }

    public void Reset() => throw new InvalidOperationException("Resetting is not supported");

    public bool SupportsResetting => false;

    public void Dispose() { /* no resources to release; the byte memory is caller-owned. */ }
}
