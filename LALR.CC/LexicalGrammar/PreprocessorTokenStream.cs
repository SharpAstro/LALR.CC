using System;
using System.Collections.Generic;

namespace LALR.CC.LexicalGrammar;

/// <summary>
/// Sits between a raw token stream (typically <see cref="BytesLexer"/>) and the
/// parser's <see cref="SyncLATokenIterator"/>: when the inner stream yields a
/// token whose id is a declared preprocessor directive, this adapter buffers
/// the directive's same-line arg tokens, dispatches to the handler, and emits
/// the handler's returned tokens in place of the directive line. Non-directive
/// tokens optionally pass through a <c>rewrite</c> hook for macro expansion;
/// when that's null, they pass through unchanged.
/// </summary>
/// <remarks>
/// "Same line" means tokens whose <see cref="SourcePosition.Line"/> equals the
/// directive token's line. Items with unknown positions (<c>Line == 0</c>) are
/// treated as same-line as whatever the directive carried — this matches the
/// usual case where the directive and its args were emitted by the same lexer
/// invocation, but also lets a directive be hand-built in code without
/// breaking the adapter.
/// <para>
/// The iterator plumbing (ready queue, one-token look-ahead, exhaustion flag,
/// <see cref="ISyncIterator{T}"/> surface) lives in the
/// <see cref="RewritingTokenStream"/> base — this class focuses on the
/// preprocessor-specific policy. See that class for the cross-cutting pattern
/// shared with future rewriters (e.g. the C typedef "lexer hack").
/// </para>
/// <para>
/// Re-entrancy: <see cref="PreprocessorTokenStream"/> instances are *not*
/// thread-safe, but the directive handler may construct another
/// <see cref="PreprocessorTokenStream"/> over a freshly-lexed inner source
/// (the C-minus example does this for <c>#include</c>) and drain it into a
/// list — the outer adapter has no live cursor while the handler runs.
/// </para>
/// </remarks>
public sealed class PreprocessorTokenStream : RewritingTokenStream
{
    private readonly IReadOnlyDictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>> _directives;
    private readonly Func<Item, IEnumerable<Item>> _rewrite;
    private readonly PreprocessorConditionals _conditionals;

    // Conditional-compilation state. Stack of "is this branch's body currently
    // emitting?" flags — one per open #if/#ifdef/#ifndef. _falseDepth counts
    // false entries on the stack so the "are we suppressed?" check is O(1).
    // Tokens are emitted only when _falseDepth == 0 (or the conditional engine
    // is disabled). The engine still consumes #if/#ifdef/#ifndef/#else/#endif
    // inside a suppressed region — depth tracking has to keep working so we
    // pop the right number of times when we hit the matching #endif.
    private readonly Stack<bool> _branchStack = new();
    private int _falseDepth;

    /// <summary>
    /// Construct the adapter. <paramref name="directives"/> maps
    /// directive-token symbol ids to handlers; pass an empty dictionary to
    /// disable directive dispatch (then the adapter is just a
    /// <paramref name="rewrite"/>-applying passthrough). <paramref name="rewrite"/>
    /// can be null when no macro expansion is desired — non-directive tokens
    /// then flow through verbatim. <paramref name="conditionals"/> wires up
    /// the conditional-compilation engine — pass <c>default</c> to disable.
    /// </summary>
    public PreprocessorTokenStream(
        ISyncIterator<Item> inner,
        IReadOnlyDictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>> directives,
        Func<Item, IEnumerable<Item>> rewrite = null,
        PreprocessorConditionals conditionals = default)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(directives);
        _directives = directives;
        _rewrite = rewrite;
        _conditionals = conditionals;
    }

    protected override void ProcessToken(Item token)
    {
        // 1) Conditional-compilation gate. Always runs (suppressed or not)
        //    — the engine has to track #if/#endif depth even inside a
        //    false branch, otherwise it can't tell which #endif closes
        //    which open conditional. Returns true if the token was a
        //    conditional directive (already consumed); false means fall
        //    through to suppression + directive-dispatch.
        if (_conditionals.Enabled && HandleConditional(token))
        {
            return;
        }

        // 2) If suppressed by an open false branch, drop the token. Don't
        //    dispatch directives (user handlers can have side effects like
        //    populating the macro table) and don't run rewrite.
        if (_falseDepth > 0)
        {
            return;
        }

        // 3) Is this token a declared directive?
        if (_directives.TryGetValue(token.ID, out var handler))
        {
            // Collect same-line args. Stops at first token whose line
            // differs (which gets held for the next outer call) or at
            // inner-exhaustion.
            var args = CollectSameLineArgs(token.Position.Line);
            EmitRange(handler(args));
            return;
        }

        // 4) Non-directive token: route through rewrite (or passthrough).
        if (_rewrite is null)
        {
            Emit(token);
            return;
        }
        EmitRange(_rewrite(token));
    }

    /// <summary>
    /// If <paramref name="token"/> is one of the configured conditional
    /// directives (#if / #ifdef / #ifndef / #else / #endif), drive the branch
    /// stack and return <c>true</c> (caller should stop processing this
    /// token). Returns <c>false</c> when the token is unrelated to
    /// conditional compilation — caller falls through to suppression check +
    /// regular directive dispatch.
    /// </summary>
    /// <remarks>
    /// The same-line arg collection runs both in emit-mode and in
    /// suppression — we always need to consume the arg tokens off the inner
    /// stream so they don't leak out. The evaluation of <c>#ifdef NAME</c>
    /// is short-circuited when suppressed: pushed branch is always false in
    /// that case, since the outer is already suppressing. The flip on
    /// <c>#else</c> always runs regardless of suppression because the inner
    /// branch's *value* still needs to be inverted (so an <c>#endif</c>
    /// later pops the right kind of entry, and a <c>#else</c> doesn't make
    /// us emit when an outer is still false).
    /// </remarks>
    private bool HandleConditional(Item token)
    {
        var c = _conditionals;
        var id = token.ID;
        if (id == c.IfSymbol)
        {
            var args = CollectSameLineArgs(token.Position.Line);
            var branch = _falseDepth > 0 ? false : EvaluateIfExpression(args);
            PushBranch(branch);
            return true;
        }
        if (id == c.IfDefSymbol)
        {
            var args = CollectSameLineArgs(token.Position.Line);
            var branch = false;
            if (_falseDepth == 0 && args.Count > 0)
            {
                var name = args[0].Content as string;
                branch = !string.IsNullOrEmpty(name) && c.IsDefined(name);
            }
            PushBranch(branch);
            return true;
        }
        if (id == c.IfNDefSymbol)
        {
            var args = CollectSameLineArgs(token.Position.Line);
            var branch = false;
            if (_falseDepth == 0 && args.Count > 0)
            {
                var name = args[0].Content as string;
                branch = !string.IsNullOrEmpty(name) && !c.IsDefined(name);
            }
            PushBranch(branch);
            return true;
        }
        if (id == c.ElseSymbol)
        {
            // Drop any same-line args (real C: #else takes none).
            _ = CollectSameLineArgs(token.Position.Line);
            FlipBranch();
            return true;
        }
        if (id == c.EndIfSymbol)
        {
            _ = CollectSameLineArgs(token.Position.Line);
            PopBranch();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Evaluate the expression after <c>#if</c>. v1 supports only literal
    /// <c>0</c> and <c>1</c> — anything else falls back to <c>false</c>.
    /// Full expression evaluation (<c>#if X &amp;&amp; Y &gt; 5</c>) is a
    /// deferred follow-up; header guards (the user's stated need) use only
    /// <c>#ifdef</c>/<c>#ifndef</c> anyway.
    /// </summary>
    private static bool EvaluateIfExpression(IReadOnlyList<Item> args)
    {
        if (args.Count == 0)
        {
            return false;
        }
        var s = args[0].Content as string;
        return s == "1";
    }

    private void PushBranch(bool emitting)
    {
        _branchStack.Push(emitting);
        if (!emitting)
        {
            _falseDepth++;
        }
    }

    private void PopBranch()
    {
        if (_branchStack.Count == 0)
        {
            throw new InvalidOperationException(
                "preprocessor: #endif without a matching #if/#ifdef/#ifndef");
        }
        var top = _branchStack.Pop();
        if (!top)
        {
            _falseDepth--;
        }
    }

    private void FlipBranch()
    {
        if (_branchStack.Count == 0)
        {
            throw new InvalidOperationException(
                "preprocessor: #else without a matching #if/#ifdef/#ifndef");
        }
        var top = _branchStack.Pop();
        if (top)
        {
            _falseDepth++;
        }
        else
        {
            _falseDepth--;
        }
        _branchStack.Push(!top);
    }

    /// <summary>
    /// Drain tokens off the inner iterator that share <paramref name="directiveLine"/>.
    /// Stops at the first cross-line token (held for the next outer pull via
    /// <see cref="RewritingTokenStream.HoldNext"/>) or at inner-exhaustion.
    /// Unknown positions (line 0) are treated as same-line — see remarks on
    /// the class.
    /// </summary>
    private IReadOnlyList<Item> CollectSameLineArgs(int directiveLine) =>
        CollectUntil(next =>
        {
            var nextLine = next.Position.Line;
            // Stop (and hold back) the first cross-line token; line 0 is
            // "unknown position" and counts as same-line.
            return nextLine != directiveLine && nextLine != 0;
        });

    public override void Reset()
    {
        _branchStack.Clear();
        _falseDepth = 0;
        base.Reset();
    }
}
