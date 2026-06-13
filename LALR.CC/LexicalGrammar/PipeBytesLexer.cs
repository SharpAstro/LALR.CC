using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LALR.CC.LexicalGrammar.Dfa;

namespace LALR.CC.LexicalGrammar;

/// <summary>
/// Bytes-to-tokens lexer driven by a UTF-8 byte DFA per lexer state. Bytes flow from
/// a <see cref="PipeReader"/> through DFAs lowered by <see cref="Utf8DfaLowering"/> at
/// construction; there is no codepoint or UTF-16 round trip in the inner loop. The
/// matched bytes are UTF-8 decoded once, at token boundaries, into
/// <see cref="Item.Content"/>.
/// </summary>
public sealed class PipeBytesLexer : IAsyncIterator<Item>
{
    public const string RootState = "root";
    public const string PopState = "#pop";
    public const string Ignore = "#ignore";

    private readonly PipeReader _reader;
    private readonly Dictionary<string, CompiledState> _compiledStates;
    private readonly Stack<string> _states;
    private readonly CancellationToken _cancellationToken;
    private readonly LexerErrorMode _errorMode;
    private readonly int _errorSymbolId;
    private readonly ColumnMode _columnMode;
    private bool _readerCompleted;
    private Item _currentItem;

    // Cursor position into the input. Updated as bytes are consumed (matched + ignored).
    // Lines are 1-based; column is the 1-based byte column inside the current line.
    private long _byteOffset;
    private int _line = 1;
    private int _column = 1;

    private readonly struct CompiledState(Dfa.Dfa byteDfa, LexRule[] rules)
    {
        public Dfa.Dfa ByteDfa { get; } = byteDfa;
        public LexRule[] Rules { get; } = rules;
    }

    /// <param name="initialLine">
    /// 1-based line number the first token reports (default 1). Mirrors
    /// <see cref="BytesLexer"/> — lets a caller lex a nested buffer in a reserved line
    /// band so its tokens are distinguishable by position. Trailing parameter to stay
    /// source-compatible with existing positional callers passing <paramref name="cancellationToken"/>.
    /// </param>
    public PipeBytesLexer(PipeReader reader, IReadOnlyDictionary<string, LexRule[]> patternTable,
        LexerErrorMode errorMode = LexerErrorMode.Throw,
        int errorSymbolId = -1,
        ColumnMode columnMode = ColumnMode.Codepoints,
        CancellationToken cancellationToken = default,
        int initialLine = 1)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(patternTable);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialLine, 1);
        if (!patternTable.ContainsKey(RootState))
        {
            throw new ArgumentException($"pattern table must contain a '{RootState}' state", nameof(patternTable));
        }
        if (errorMode != LexerErrorMode.Throw && errorSymbolId < 0)
        {
            throw new ArgumentException(
                $"errorSymbolId must be non-negative when errorMode is {errorMode}",
                nameof(errorSymbolId));
        }

        _reader = reader;
        _cancellationToken = cancellationToken;
        _errorMode = errorMode;
        _errorSymbolId = errorSymbolId;
        _columnMode = columnMode;
        _line = initialLine;
        _states = new Stack<string>([RootState]);
        _compiledStates = new Dictionary<string, CompiledState>(patternTable.Count, StringComparer.Ordinal);
        foreach (var kv in patternTable)
        {
            var rules = kv.Value;
            if (rules is null || rules.Length == 0)
            {
                throw new ArgumentException($"state '{kv.Key}' has no rules", nameof(patternTable));
            }
            // Each rule's index becomes its DFA pattern id, so the smallest accepting id
            // at any DFA state corresponds to the first matching rule (first-pattern-wins).
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

    /// <summary>Convenience factory that wraps a UTF-8 byte buffer.</summary>
    /// <param name="initialLine">1-based starting line for the first token (default 1); see the constructor.</param>
    public static PipeBytesLexer FromBytes(ReadOnlyMemory<byte> utf8Bytes, IReadOnlyDictionary<string, LexRule[]> patternTable,
        LexerErrorMode errorMode = LexerErrorMode.Throw, int errorSymbolId = -1,
        ColumnMode columnMode = ColumnMode.Codepoints,
        CancellationToken cancellationToken = default,
        int initialLine = 1)
    {
        var reader = PipeReader.Create(new ReadOnlySequence<byte>(utf8Bytes));
        return new PipeBytesLexer(reader, patternTable, errorMode, errorSymbolId, columnMode, cancellationToken, initialLine);
    }

    /// <summary>Convenience factory that wraps a UTF-8 stream via Pipelines.</summary>
    /// <param name="initialLine">1-based starting line for the first token (default 1); see the constructor.</param>
    public static PipeBytesLexer FromStream(Stream stream, IReadOnlyDictionary<string, LexRule[]> patternTable,
        bool leaveOpen = false,
        LexerErrorMode errorMode = LexerErrorMode.Throw, int errorSymbolId = -1,
        ColumnMode columnMode = ColumnMode.Codepoints,
        CancellationToken cancellationToken = default,
        int initialLine = 1)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: leaveOpen));
        return new PipeBytesLexer(reader, patternTable, errorMode, errorSymbolId, columnMode, cancellationToken, initialLine);
    }

    /// <summary>
    /// Convenience factory: encodes a UTF-16 string to UTF-8 once, then lexes. Prefer
    /// <see cref="FromBytes"/> or <see cref="FromStream"/> when the source is already bytes.
    /// </summary>
    public static PipeBytesLexer FromString(string text, IReadOnlyDictionary<string, LexRule[]> patternTable,
        LexerErrorMode errorMode = LexerErrorMode.Throw, int errorSymbolId = -1,
        ColumnMode columnMode = ColumnMode.Codepoints,
        CancellationToken cancellationToken = default,
        int initialLine = 1)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FromBytes(Encoding.UTF8.GetBytes(text), patternTable, errorMode, errorSymbolId, columnMode, cancellationToken, initialLine);
    }

    public Task<Item> CurrentAsync()
    {
        if (_currentItem is null)
        {
            throw new InvalidOperationException("Did not call MoveNextAsync() yet!");
        }
        return Task.FromResult(_currentItem);
    }

    public async Task<bool> MoveNextAsync()
    {
        // Outer loop restarts on #ignore so we keep scanning until we have a real token.
        while (true)
        {
            if (_readerCompleted)
            {
                return false;
            }

            var (advanced, token) = await ScanOneAsync().ConfigureAwait(false);
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
    /// Advance the lexer one DFA pass. Returns (advanced=false) on EOF or when no
    /// pattern matches. Returns (advanced=true, token=null) on a #ignore match — caller
    /// should restart. Returns (advanced=true, token=Item) on a normal match.
    /// </summary>
    private async Task<(bool Advanced, Item Token)> ScanOneAsync()
    {
        var compiled = _compiledStates[_states.Peek()];
        var dfa = compiled.ByteDfa;
        var state = dfa.Start;
        var bestLen = 0L;
        var bestPattern = dfa.States[state].IsAccept ? dfa.States[state].Accept : -1;
        var examined = 0L;
        // Snapshot the cursor at the start of the scan; this is the position the
        // emitted token will report. The cursor is advanced again after we know the
        // actual match length, by walking the matched bytes once more (cheap and
        // simpler than tracking line/col per accept inside the DFA loop).
        var startPosition = new SourcePosition(_line, _column, _byteOffset);

        while (true)
        {
            var result = await _reader.ReadAsync(_cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            // Step the DFA on bytes we haven't seen yet (offset `examined`).
            var stuck = StepThrough(dfa, buffer, ref state, ref examined, ref bestLen, ref bestPattern);

            if (stuck || result.IsCompleted)
            {
                if (bestPattern >= 0 && bestLen > 0)
                {
                    var matchSlice = buffer.Slice(0, bestLen);
                    var matchedString = DecodeUtf8(matchSlice);
                    AdvanceCursor(matchSlice);
                    _reader.AdvanceTo(buffer.GetPosition(bestLen));

                    var rule = compiled.Rules[bestPattern];
                    ApplyInstruction(rule.Instruction);

                    return rule.Instruction == Ignore
                        ? (true, null)
                        : (true, new Item(rule.SymbolId, matchedString, startPosition));
                }

                // No match. Two distinct cases hide here:
                //   (a) clean EOF — buffer empty AND reader completed
                //   (b) unrecognized bytes — buffer non-empty (the failing prefix is here)
                if (buffer.IsEmpty && result.IsCompleted)
                {
                    _reader.AdvanceTo(buffer.End);
                    _readerCompleted = true;
                    return (false, null);
                }

                return HandleLexError(buffer, startPosition);
            }

            // Need more bytes — keep examined ones in buffer, ask reader to extend.
            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private (bool Advanced, Item Token) HandleLexError(ReadOnlySequence<byte> buffer, SourcePosition errorPosition)
    {
        var firstByte = buffer.FirstSpan.Length > 0 ? buffer.FirstSpan[0] : (byte)0;
        var stateName = _states.Peek();

        switch (_errorMode)
        {
            case LexerErrorMode.Throw:
                // Drop the buffer and mark completed so a caller catching the exception
                // and resuming gets a clean false on the next call.
                _reader.AdvanceTo(buffer.End);
                _readerCompleted = true;
                throw new LexerException(errorPosition, firstByte, stateName);

            case LexerErrorMode.EmitAndStop:
                _reader.AdvanceTo(buffer.End);
                _readerCompleted = true;
                return (true, MakeErrorItem(firstByte, errorPosition));

            case LexerErrorMode.EmitAndSkip:
                // Consume exactly one byte so the next scan starts past the offender.
                var skipSlice = buffer.Slice(0, 1);
                AdvanceCursor(skipSlice);
                _reader.AdvanceTo(buffer.GetPosition(1));
                return (true, MakeErrorItem(firstByte, errorPosition));

            default:
                // Unreachable — but keep the compiler happy and fail loudly if a new
                // mode is ever added without a switch arm.
                throw new InvalidOperationException($"unhandled LexerErrorMode: {_errorMode}");
        }
    }

    private Item MakeErrorItem(byte offendingByte, SourcePosition position)
    {
        var content = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\\x{offendingByte:X2}");
        var item = new Item(_errorSymbolId, content, position);
        // Force IsError=true. Item.State propagates -1 through reductions automatically.
        item.State = -1;
        return item;
    }

    private void AdvanceCursor(ReadOnlySequence<byte> matched)
    {
        // Walk the matched bytes once to update line/column/offset.
        // - ByteOffset always counts every byte (the field is documented as a byte index).
        // - Line bumps on every '\n' regardless of column mode.
        // - Column behaviour depends on _columnMode:
        //     Codepoints (default): skip UTF-8 continuation bytes (0b10xxxxxx) so each
        //       codepoint contributes exactly 1. ASCII = same as Bytes mode.
        //     Bytes: every byte contributes 1.
        // No decoding either way — both modes are O(1) per byte.
        var codepointMode = _columnMode == ColumnMode.Codepoints;
        foreach (var segment in matched)
        {
            var span = segment.Span;
            for (var i = 0; i < span.Length; i++)
            {
                var b = span[i];
                _byteOffset++;
                if (b == (byte)'\n')
                {
                    _line++;
                    _column = 1;
                }
                else if (!codepointMode || (b & 0xC0) != 0x80)
                {
                    // Codepoint mode skips continuation bytes; Bytes mode counts every byte.
                    _column++;
                }
            }
        }
    }

    private static bool StepThrough(Dfa.Dfa dfa, ReadOnlySequence<byte> buffer, ref int state, ref long examined, ref long bestLen, ref int bestPattern)
    {
        // Iterate segments of the unexamined slice; SequenceReader<byte> would
        // ergonomically wrap this but is a ref struct and complicates `ref` plumbing.
        var slice = buffer.Slice(examined);
        foreach (var segment in slice)
        {
            var span = segment.Span;
            for (var i = 0; i < span.Length; i++)
            {
                var next = dfa.Step(state, span[i]);
                if (next < 0)
                {
                    return true;
                }
                state = next;
                examined++;
                if (dfa.States[state].IsAccept)
                {
                    bestLen = examined;
                    bestPattern = dfa.States[state].Accept;
                }
            }
        }
        return false;
    }

    private static string DecodeUtf8(ReadOnlySequence<byte> matchSlice)
    {
        // Single-segment fast path skips the rented-buffer copy.
        if (matchSlice.IsSingleSegment)
        {
            return Encoding.UTF8.GetString(matchSlice.FirstSpan);
        }
        var len = (int)matchSlice.Length;
        var rented = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            matchSlice.CopyTo(rented);
            return Encoding.UTF8.GetString(rented, 0, len);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void ApplyInstruction(string instruction)
    {
        if (string.IsNullOrEmpty(instruction) || instruction == Ignore)
        {
            return;
        }
        if (instruction == PopState)
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

    public void Dispose() => _reader.Complete();
}
