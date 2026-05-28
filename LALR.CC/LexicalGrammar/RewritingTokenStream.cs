using System;
using System.Collections.Generic;

namespace LALR.CC.LexicalGrammar;

/// <summary>
/// Base class for stateful token-stream rewriters that sit between a raw
/// lexer and the parser. Owns the iterator plumbing — a ready-to-emit
/// queue, a single-token look-ahead buffer, an exhaustion flag, and the
/// <see cref="ISyncIterator{T}"/> surface — and exposes a single
/// <see cref="ProcessToken"/> hook plus the <see cref="Emit(Item)"/> /
/// <see cref="EmitRange"/> / <see cref="CollectUntil"/> / <see cref="HoldNext"/>
/// helpers. Subclasses focus on the policy they implement (macro expansion,
/// typedef-name rewriting, contextual-keyword promotion, embedded-DSL mode
/// shifts, …) without re-implementing the mechanics each time.
/// </summary>
/// <remarks>
/// The pattern this captures is the same one that drives the C
/// <em>preprocessor</em> (token-level macro expansion + directive dispatch),
/// the C <em>lexer hack</em> for <c>typedef</c> (rewrite <c>ID</c> →
/// <c>TYPE_NAME</c> after a typedef declaration), contextual keywords in
/// modern languages, user-defined infix operators, etc. The unifying shape:
/// <list type="bullet">
///   <item>Wrap an inner <see cref="ISyncIterator{Item}"/>.</item>
///   <item>Track state (a name set, a macro table, a mode flag).</item>
///   <item>Per token: rewrite it, drop it, replace it with a sequence, or
///     consume neighbouring tokens before emitting.</item>
/// </list>
/// Cost of using this base is small — one virtual call per token — and the
/// payoff is real once you have two implementations.
/// <para>
/// Re-entrancy: instances are not thread-safe. A subclass may construct
/// another <c>RewritingTokenStream</c> over a freshly-lexed inner source
/// (e.g. <c>#include</c> handlers do this) and drain it inside its
/// <see cref="ProcessToken"/> — the outer instance has no live cursor while
/// the inner runs.
/// </para>
/// </remarks>
public abstract class RewritingTokenStream : ISyncIterator<Item>
{
    private readonly ISyncIterator<Item> _inner;

    // Queue of tokens ready to emit: produced by ProcessToken via Emit/EmitRange.
    private readonly Queue<Item> _ready = new();

    // The inner iterator's last MoveNext gave us a token that didn't belong to
    // the current rewrite step (e.g. crossed a line boundary in the
    // preprocessor's same-line arg collection) — we held onto it via
    // HoldNext. Cleared by TryReadNext when consumed.
    private Item _heldNext;
    private bool _heldNextValid;

    // Has _inner been exhausted? Once true, MoveNext only drains _ready /
    // _heldNext, never pumps inner again.
    private bool _innerExhausted;

    private Item _current;
    private bool _hasCurrent;

    protected RewritingTokenStream(ISyncIterator<Item> inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>The inner iterator this stream wraps. Exposed for subclasses
    /// that need direct access (rare — prefer <see cref="TryReadNext"/>).</summary>
    protected ISyncIterator<Item> Inner => _inner;

    public Item Current => _hasCurrent ? _current : default;

    public bool MoveNext()
    {
        while (true)
        {
            // 1) Anything queued from a previous step? Pop the next ready
            //    token — the queue is the authoritative source of what to
            //    emit next; don't pump inner until it's drained.
            if (_ready.Count > 0)
            {
                _current = _ready.Dequeue();
                _hasCurrent = true;
                return true;
            }

            // 2) Pull the next raw token. _heldNext is preferred over inner
            //    because we already read it ahead during a previous step.
            if (!TryReadNext(out var token))
            {
                _hasCurrent = false;
                _current = default;
                return false;
            }

            // 3) Hand off to the subclass. It enqueues 0+ tokens for emit
            //    (via Emit / EmitRange) and may consume additional tokens
            //    from the inner stream (via TryReadNext / CollectUntil).
            //    On return, the loop drains _ready before pulling the next
            //    raw token.
            ProcessToken(token);
        }
    }

    /// <summary>
    /// Subclass hook: handle a single token from the upstream. Implementations
    /// typically inspect the token's symbol id (<see cref="Item.ID"/>) and
    /// decide whether to emit it unchanged, drop it, rewrite it into a
    /// different token, expand it into a sequence, or consume further
    /// upstream tokens (using <see cref="TryReadNext"/> /
    /// <see cref="CollectUntil"/>) before emitting.
    /// </summary>
    protected abstract void ProcessToken(Item token);

    /// <summary>Queue a token for downstream emission.</summary>
    protected void Emit(Item token) => _ready.Enqueue(token);

    /// <summary>
    /// Queue a sequence of tokens for downstream emission, preserving order.
    /// Null entries are silently skipped — matches the convenience of
    /// hand-built directive-handler return values.
    /// </summary>
    protected void EmitRange(IEnumerable<Item> tokens)
    {
        if (tokens is null) { return; }
        foreach (var t in tokens)
        {
            if (t != null) { _ready.Enqueue(t); }
        }
    }

    /// <summary>
    /// Pull the next raw token from the upstream. Returns false only when
    /// both the held-buffer and the inner iterator are empty.
    /// </summary>
    protected bool TryReadNext(out Item token)
    {
        if (_heldNextValid)
        {
            token = _heldNext;
            _heldNext = default;
            _heldNextValid = false;
            return true;
        }
        if (_innerExhausted)
        {
            token = default;
            return false;
        }
        if (_inner.MoveNext())
        {
            token = _inner.Current;
            return true;
        }
        _innerExhausted = true;
        token = default;
        return false;
    }

    /// <summary>
    /// Hold a token back so the next <see cref="TryReadNext"/> returns it
    /// instead of pumping the inner iterator. Used when a multi-token
    /// look-ahead reads one token too many and needs to put it back.
    /// </summary>
    /// <exception cref="InvalidOperationException">The look-ahead buffer
    /// already holds a token. Drain it via <see cref="TryReadNext"/> before
    /// holding another.</exception>
    protected void HoldNext(Item token)
    {
        if (_heldNextValid)
        {
            throw new InvalidOperationException(
                "RewritingTokenStream: look-ahead buffer already holds a token. "
                + "Each ProcessToken step may hold at most one.");
        }
        _heldNext = token;
        _heldNextValid = true;
    }

    /// <summary>
    /// Drain tokens off the upstream until <paramref name="stop"/> returns
    /// true for one — that stop-triggering token is held back via
    /// <see cref="HoldNext"/> so the next pull sees it again. Returns the
    /// collected prefix (not including the stop token).
    /// </summary>
    /// <remarks>
    /// Useful for "collect tokens until <c>;</c>" (typedef declaration body),
    /// "collect tokens on the same source line" (preprocessor same-line args
    /// — pass a line-aware predicate), or any other multi-token consumption
    /// step inside <see cref="ProcessToken"/>.
    /// </remarks>
    protected IReadOnlyList<Item> CollectUntil(Func<Item, bool> stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        var collected = new List<Item>();
        while (TryReadNext(out var next))
        {
            if (stop(next))
            {
                HoldNext(next);
                break;
            }
            collected.Add(next);
        }
        return collected;
    }

    /// <summary>
    /// Reset the stream. Subclasses that hold their own state must override
    /// and call <c>base.Reset()</c>.
    /// </summary>
    public virtual void Reset()
    {
        _ready.Clear();
        _heldNext = default;
        _heldNextValid = false;
        _innerExhausted = false;
        _current = default;
        _hasCurrent = false;
        _inner.Reset();
    }

    public bool SupportsResetting => _inner.SupportsResetting;

    public virtual void Dispose()
    {
        _ready.Clear();
        _inner.Dispose();
    }
}
