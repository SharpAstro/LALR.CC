using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LALR.CC.LexicalGrammar;

namespace LALR.CC;

public class Parser
{
    // Null when constructed from a pre-baked ParseTable (compiler-compiler /
    // Phase 5 path) — the introspection getters that depend on table-build
    // state (FirstSets, LR0/LR1 items, kernels, gotos, propogations) throw
    // NotSupportedException on this path. Productions / NonTerminals / Conflicts
    // stay available because they're cheaply derivable from Grammar at ctor time.
    private readonly ParserTableBuilder _builder;
    private readonly Grammar _grammar;
    private readonly ParseTable _parseTable;
    // Always populated. On the runtime-build path these mirror _builder's
    // equivalents (passthrough); on the pre-baked path they're computed
    // directly from Grammar in the ctor.
    private readonly IReadOnlyList<Production> _productions;
    private readonly HashSet<int> _nonterminals;

    private const string PreBakedIntrospectionMessage =
        "This Parser was constructed from a pre-baked ParseTable; LR0/LR1 introspection state is unavailable. " +
        "Use new Parser(grammar) (which runs ParserTableBuilder) if you need to inspect items, kernels, gotos, or first-sets.";

    public IReadOnlyList<HashSet<int>> FirstSets =>
        _builder?.FirstSets ?? throw new System.NotSupportedException(PreBakedIntrospectionMessage);

    public IReadOnlyList<LR0Item> LR0Items =>
        _builder?.LR0Items ?? throw new System.NotSupportedException(PreBakedIntrospectionMessage);

    public IReadOnlyList<LR1Item> LR1Items =>
        _builder?.LR1Items ?? throw new System.NotSupportedException(PreBakedIntrospectionMessage);

    public IReadOnlyList<HashSet<int>> LR0States =>
        _builder?.LR0States ?? throw new System.NotSupportedException(PreBakedIntrospectionMessage);

    public IReadOnlyList<HashSet<int>> LR0Kernels =>
        _builder?.LR0Kernels ?? throw new System.NotSupportedException(PreBakedIntrospectionMessage);

    public IReadOnlyList<HashSet<int>> LALRStates =>
        _builder?.LALRStates ?? throw new System.NotSupportedException(PreBakedIntrospectionMessage);

    public IReadOnlyList<int[]> LRGotos =>
        _builder?.LRGotos ?? throw new System.NotSupportedException(PreBakedIntrospectionMessage);

    public IReadOnlyList<int[]> GotoPrecedence =>
        _builder?.GotoPrecedence ?? throw new System.NotSupportedException(PreBakedIntrospectionMessage);

    public IReadOnlyList<Production> Productions => _productions;

    public HashSet<int> Terminals =>
        _builder?.Terminals ?? throw new System.NotSupportedException(PreBakedIntrospectionMessage);

    public HashSet<int> NonTerminals => _nonterminals;

    public IReadOnlyList<IDictionary<int, IList<LALRPropogation>>> LALRPropogations =>
        _builder?.LALRPropogations ?? throw new System.NotSupportedException(PreBakedIntrospectionMessage);

    public IReadOnlyList<int> ProductionPrecedence =>
        _builder?.ProductionPrecedence ?? throw new System.NotSupportedException(PreBakedIntrospectionMessage);

    public IReadOnlyList<Derivation> ProductionDerivation =>
        _builder?.ProductionDerivation ?? throw new System.NotSupportedException(PreBakedIntrospectionMessage);

    public Grammar Grammar => _grammar;

    public ParseTable ParseTable => _parseTable;

    /// <summary>
    /// Unresolved shift-reduce / reduce-reduce conflicts found while building the parse
    /// table. Empty after a successful runtime-build constructor call (the constructor
    /// throws <see cref="GrammarConflictException"/> if any conflicts remain). Always
    /// empty on the pre-baked path — conflicts surfaced as <c>LALR0004</c> Roslyn
    /// diagnostics at generator time, so a pre-baked Parser by construction has none.
    /// </summary>
    public IReadOnlyList<GrammarConflict> Conflicts =>
        _builder?.Conflicts ?? System.Array.Empty<GrammarConflict>();

    /// <summary>
    /// Symbol ids that have a non-error action at <paramref name="state"/>. Column 0
    /// is end-of-input (id -1); column N is symbol id N-1. Used to build the
    /// "expected one of …" set for <see cref="ParseErrorException"/>.
    /// </summary>
    private List<int> ExpectedTerminalsAt(int state)
    {
        var expected = new List<int>();
        var nCols = _parseTable.Tokens;
        for (var col = 0; col < nCols; col++)
        {
            var act = _parseTable.Actions[state, col];
            if (act.ActionType == ActionType.Shift || act.ActionType == ActionType.Reduce)
            {
                expected.Add(col - 1);
            }
        }
        return expected;
    }

    /// <summary>
    /// Based on: http://www.goldparser.org/doc/engine-pseudo/parse-Item.htm
    /// </summary>
    /// <param name="tokenIterator">Item iterator which will be owned by the caller</param>
    /// <param name="debugger">Enables debugging support</param>
    /// <param name="trimReductions">If true (default), trim reductions of the form L -> R, where R is a non-terminal</param>
    /// <param name="allowRewriting">Apply rewriting functions</param>
    /// <param name="errorMode">How to surface parse errors: throw <see cref="ParseErrorException"/> (default) or return the offending Item.</param>
    /// <param name="cancellationToken">Cancels the parse loop between iterations.</param>
    /// <returns>The reduced program tree on acceptance, or — when <paramref name="errorMode"/> is <see cref="ParserErrorMode.Return"/> — the erroneous Item.</returns>
    public async Task<Item> ParseInputAsync(IAsyncLAIterator<Item> tokenIterator, Debug debugger = null,
        bool trimReductions = true,
        bool allowRewriting = true,
        ParserErrorMode errorMode = ParserErrorMode.Throw,
        CancellationToken cancellationToken = default)
    {
        const int initState = 0;
        var tokenStack = new Stack<Item>();
        var state = initState;
        var productions = Productions;
        var nonterminals = NonTerminals;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = await tokenIterator.LookAheadAsync();
            var action = _parseTable.Actions[state, token.ID + 1];
            // debugger may be null (caller wants no trace); the methods are
            // [Conditional("DEBUG")] so the call vanishes in Release regardless,
            // but in Debug builds we'd dereference null without this guard.
            debugger?.DumpParsingState(state, tokenStack, token, action);

            switch (action.ActionType)
            {
                case ActionType.Shift:
                    state = action.ActionParameter;
                    token.State = state;
                    tokenStack.Push(token);
                    await tokenIterator.MoveNextAsync();
                    break;

                case ActionType.Reduce:
                    var nProduction = action.ActionParameter;
                    var production = productions[nProduction];
                    var nChildren = production.Right.Length;
                    Item reduction;
                    // Trim single-non-terminal-RHS reductions ONLY when the
                    // production carries no semantic action — those are the
                    // "passthrough" rules (e.g. `LOr → LAnd`, `Stmt → Block`)
                    // whose content propagates verbatim. If the author supplied
                    // an action / rewriter, they explicitly want it invoked at
                    // this point — trimming would silently swallow it. (The
                    // common author footgun: declarator-specifier-list rules
                    // like `Type → TypeSpecList` where the action resolves a
                    // marker accumulator into a final type string.)
                    if (trimReductions && nChildren == 1
                        && nonterminals.Contains(production.Right[0])
                        && !production.HasRewriter)
                    {
                        var popped = tokenStack.Pop();
                        reduction = new Item(production.Left, popped.Content, popped.Position);
                    }
                    else
                    {
                        var children = new Item[nChildren];
                        for (var i = 0; i < nChildren; i++)
                        {
                            children[nChildren - i - 1] = tokenStack.Pop();
                        }
                        // Distinguish "rewriter present, returned null" (keep the null —
                        // it's the legitimate content the visitor produced) from "no
                        // rewriter at all" (fall back to the default Reduction). Driving
                        // this off Production.HasRewriter avoids the old `?? new Reduction`
                        // conflation that ate a JSON visitor's null literal returns.
                        object rewrite = allowRewriting && production.HasRewriter
                            ? production.Rewrite(children)
                            : new Reduction(nProduction, children);
                        // empty reductions (epsilon) take the lookahead position so the
                        // emitted item still has a meaningful place in the source.
                        var pos = nChildren > 0 ? children[0].Position : token.Position;
                        reduction = new Item(production.Left, rewrite, pos);
                    }
                    var lastState = tokenStack.Count > 0 ? tokenStack.Peek().State : initState;
                    state = _parseTable.Actions[lastState, production.Left + 1].ActionParameter;
                    // Stash the goto-target parser state on the item so the next
                    // reduction's `lastState = Peek().State` resolves to a real state.
                    // The constructor already set State to the symbol id (or -1 if any
                    // child is an error); the IsError property recomputes from
                    // children when State >= 0, so marking the parser state here keeps
                    // error propagation working while routing nested reductions correctly.
                    // The original code stored production.Left here, which only happened
                    // to work for grammars where a reduction is never the stack item
                    // immediately below the next reduction's children — Wikipedia LaTeX's
                    // `\frac{...}{...}` (two adjacent A non-terminals separated only by
                    // a brace pair, where the second {E}'s reduction peeks the first A)
                    // is the case that exposed it.
                    reduction.State = state;
                    tokenStack.Push(reduction);
                    if (tokenStack.Count == 1 && tokenStack.Peek().ID == 0)
                    {
                        return tokenStack.Pop();
                    }
                    break;

                case ActionType.Error:
                    // Item.IsError is driven by State < 0. Use -(state+1) so even state 0
                    // produces a negative marker (the original `-state` collapsed to 0 at
                    // the initial state, leaving IsError==false on errors at the very
                    // first token — a latent bug in the pre-2026 code).
                    token.State = -(state + 1);
                    if (errorMode == ParserErrorMode.Throw)
                    {
                        var expected = ExpectedTerminalsAt(state);
                        throw new ParseErrorException(token, state, expected,
                            ParseErrorException.FormatMessage(token, state, expected, _grammar));
                    }
                    return token;

                case ActionType.ErrorRR:
                    throw new InvalidOperationException("Reduce-Reduce conflict in grammar: " + token);

                case ActionType.ErrorSR:
                    throw new InvalidOperationException("Shift-Reduce conflict in grammar: " + token);
            }
            debugger?.Flush();
        }
    }

    /// <summary>
    /// Sync mirror of <see cref="ParseInputAsync"/>. Same parse loop, same
    /// behaviour, no async machinery. Use this when the input is already in
    /// memory (e.g. <see cref="LexicalGrammar.BytesLexer"/> wrapped in a
    /// <see cref="LexicalGrammar.SyncLATokenIterator"/>) — it skips the
    /// per-token <c>Task</c> allocations and state-machine restores the async
    /// path pays even when the underlying lexer never blocks.
    /// </summary>
    /// <remarks>
    /// Code-duplicates the async loop on purpose: extracting a shared body
    /// would force <see cref="System.Threading.Tasks.ValueTask{TResult}"/>
    /// boxing or generic gymnastics that erodes the savings we're after.
    /// The two methods must stay in lockstep — the test suite covers parity
    /// against the async implementation on the same inputs.
    /// </remarks>
    public Item ParseInput(ISyncLAIterator<Item> tokenIterator, Debug debugger = null,
        bool trimReductions = true,
        bool allowRewriting = true,
        ParserErrorMode errorMode = ParserErrorMode.Throw,
        CancellationToken cancellationToken = default)
    {
        const int initState = 0;
        var tokenStack = new Stack<Item>();
        var state = initState;
        var productions = Productions;
        var nonterminals = NonTerminals;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = tokenIterator.LookAhead();
            var action = _parseTable.Actions[state, token.ID + 1];
            debugger?.DumpParsingState(state, tokenStack, token, action);

            switch (action.ActionType)
            {
                case ActionType.Shift:
                    state = action.ActionParameter;
                    token.State = state;
                    tokenStack.Push(token);
                    tokenIterator.MoveNext();
                    break;

                case ActionType.Reduce:
                    var nProduction = action.ActionParameter;
                    var production = productions[nProduction];
                    var nChildren = production.Right.Length;
                    Item reduction;
                    // Trim single-non-terminal-RHS reductions ONLY when the
                    // production carries no semantic action — those are the
                    // "passthrough" rules (e.g. `LOr → LAnd`, `Stmt → Block`)
                    // whose content propagates verbatim. If the author supplied
                    // an action / rewriter, they explicitly want it invoked at
                    // this point — trimming would silently swallow it. (The
                    // common author footgun: declarator-specifier-list rules
                    // like `Type → TypeSpecList` where the action resolves a
                    // marker accumulator into a final type string.)
                    if (trimReductions && nChildren == 1
                        && nonterminals.Contains(production.Right[0])
                        && !production.HasRewriter)
                    {
                        var popped = tokenStack.Pop();
                        reduction = new Item(production.Left, popped.Content, popped.Position);
                    }
                    else
                    {
                        var children = new Item[nChildren];
                        for (var i = 0; i < nChildren; i++)
                        {
                            children[nChildren - i - 1] = tokenStack.Pop();
                        }
                        object rewrite = allowRewriting && production.HasRewriter
                            ? production.Rewrite(children)
                            : new Reduction(nProduction, children);
                        var pos = nChildren > 0 ? children[0].Position : token.Position;
                        reduction = new Item(production.Left, rewrite, pos);
                    }
                    var lastState = tokenStack.Count > 0 ? tokenStack.Peek().State : initState;
                    state = _parseTable.Actions[lastState, production.Left + 1].ActionParameter;
                    reduction.State = state;
                    tokenStack.Push(reduction);
                    if (tokenStack.Count == 1 && tokenStack.Peek().ID == 0)
                    {
                        return tokenStack.Pop();
                    }
                    break;

                case ActionType.Error:
                    token.State = -(state + 1);
                    if (errorMode == ParserErrorMode.Throw)
                    {
                        var expected = ExpectedTerminalsAt(state);
                        throw new ParseErrorException(token, state, expected,
                            ParseErrorException.FormatMessage(token, state, expected, _grammar));
                    }
                    return token;

                case ActionType.ErrorRR:
                    throw new InvalidOperationException("Reduce-Reduce conflict in grammar: " + token);

                case ActionType.ErrorSR:
                    throw new InvalidOperationException("Shift-Reduce conflict in grammar: " + token);
            }
            debugger?.Flush();
        }
    }

    /// <summary>
    /// Resilient (error-recovering) variant of <see cref="ParseInput"/> for a grammar whose start
    /// symbol is a <em>list</em> of top-level elements (e.g. <c>Decls → Decl …</c>). Parses as far as
    /// it can; on a parse error it records the error, skips the offending element, and resynchronises
    /// at the next element boundary instead of throwing — so the returned <see cref="ResilientParseResult.Tree"/>
    /// contains exactly the well-formed elements and <see cref="ResilientParseResult.Errors"/> lists the
    /// skipped ones. This is what a lazy/decl-driven consumer wants: only the elements it actually
    /// references need to be well-formed. A clean input yields a tree identical to <see cref="ParseInput"/>
    /// and an empty error list.
    /// </summary>
    /// <param name="tokenIterator">Item iterator, owned by the caller.</param>
    /// <param name="syncTerminalIds">Terminal ids that start a top-level list element (the resync set —
    /// e.g. the language's declaration-starting keywords). Recovery resumes only at one of these seen at
    /// bracket-depth 0. End-of-input is always an implicit resync/termination point; it need not be listed.</param>
    /// <param name="openBracketIds">Terminal ids that open a nesting level (<c>{ ( [</c>); used so a
    /// sync terminal <em>inside</em> the broken element (a nested declaration) isn't mistaken for a boundary.</param>
    /// <param name="closeBracketIds">Terminal ids that close a nesting level (<c>} ) ]</c>). A stray
    /// closer at depth 0 clamps (never goes negative), tolerating the broken element's unmatched closers.</param>
    /// <remarks>
    /// Uses ONLY the parse table (<see cref="ParseTable.Actions"/>) and grammar — never the
    /// table-builder — so it is safe on the pre-baked (source-generated) parser path where
    /// <see cref="FirstSets"/> and friends are unavailable. Recovery is guaranteed to terminate:
    /// each error either makes forward parse progress or discards at least one input token.
    /// </remarks>
    public ResilientParseResult ParseInputResilient(ISyncLAIterator<Item> tokenIterator,
        IReadOnlySet<int> syncTerminalIds,
        IReadOnlySet<int> openBracketIds,
        IReadOnlySet<int> closeBracketIds,
        Debug debugger = null,
        bool trimReductions = true,
        bool allowRewriting = true,
        CancellationToken cancellationToken = default)
    {
        const int initState = 0;
        var tokenStack = new Stack<Item>();
        var state = initState;
        var productions = Productions;
        var nonterminals = NonTerminals;
        var errors = new List<ParseErrorInfo>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = tokenIterator.LookAhead();
            var action = _parseTable.Actions[state, token.ID + 1];
            debugger?.DumpParsingState(state, tokenStack, token, action);

            switch (action.ActionType)
            {
                case ActionType.Shift:
                    state = action.ActionParameter;
                    token.State = state;
                    tokenStack.Push(token);
                    tokenIterator.MoveNext();
                    break;

                case ActionType.Reduce:
                    var nProduction = action.ActionParameter;
                    var production = productions[nProduction];
                    var nChildren = production.Right.Length;
                    Item reduction;
                    if (trimReductions && nChildren == 1
                        && nonterminals.Contains(production.Right[0])
                        && !production.HasRewriter)
                    {
                        var popped = tokenStack.Pop();
                        reduction = new Item(production.Left, popped.Content, popped.Position);
                    }
                    else
                    {
                        var children = new Item[nChildren];
                        for (var i = 0; i < nChildren; i++)
                        {
                            children[nChildren - i - 1] = tokenStack.Pop();
                        }
                        object rewrite = allowRewriting && production.HasRewriter
                            ? production.Rewrite(children)
                            : new Reduction(nProduction, children);
                        var pos = nChildren > 0 ? children[0].Position : token.Position;
                        reduction = new Item(production.Left, rewrite, pos);
                    }
                    var lastState = tokenStack.Count > 0 ? tokenStack.Peek().State : initState;
                    state = _parseTable.Actions[lastState, production.Left + 1].ActionParameter;
                    reduction.State = state;
                    tokenStack.Push(reduction);
                    if (tokenStack.Count == 1 && tokenStack.Peek().ID == 0)
                    {
                        return new ResilientParseResult(tokenStack.Pop(), errors);
                    }
                    break;

                case ActionType.Error:
                    // Capture the error (like the Throw path builds for the exception), then recover
                    // instead of throwing: skip the broken element and resynchronise at a boundary.
                    token.State = -(state + 1);
                    var expected = ExpectedTerminalsAt(state);
                    var message = ParseErrorException.FormatMessage(token, state, expected, _grammar);
                    var skippedFrom = token.Position;
                    var resyncState = Resync(tokenStack, tokenIterator,
                        syncTerminalIds, openBracketIds, closeBracketIds, out var resumeToken);
                    errors.Add(new ParseErrorInfo(token, state, expected, message, skippedFrom,
                        resyncState >= 0 ? resumeToken.Position : SourcePosition.Unknown));
                    if (resyncState < 0)
                    {
                        // Reached end-of-input with nothing on the stack able to continue: return the
                        // best-effort partial tree (the accumulated prefix) plus the recorded errors.
                        return new ResilientParseResult(BuildPartialTree(tokenStack), errors);
                    }
                    state = resyncState;
                    break;

                case ActionType.ErrorRR:
                    throw new InvalidOperationException("Reduce-Reduce conflict in grammar: " + token);

                case ActionType.ErrorSR:
                    throw new InvalidOperationException("Shift-Reduce conflict in grammar: " + token);
            }
            debugger?.Flush();
        }
    }

    /// <summary>
    /// Panic-mode resynchronisation for <see cref="ParseInputResilient"/>. From the current
    /// lookahead, discards input — tracking bracket depth via <paramref name="openBracketIds"/> /
    /// <paramref name="closeBracketIds"/> (a stray closer clamps at 0) — until the lookahead is a
    /// depth-0 <paramref name="syncTerminalIds"/> member (a top-level element boundary) or
    /// end-of-input; then pops <paramref name="tokenStack"/> to the nearest state whose action on
    /// that lookahead is a real Shift/Reduce and returns it, with the iterator left positioned at
    /// (not past) <paramref name="resumeToken"/>. If a candidate boundary is a sync terminal that no
    /// stacked state can act on, it is discarded and scanning continues. Returns -1 only at
    /// end-of-input when no stacked state (down to the initial state) can continue — an
    /// unrecoverable tail. The stack is popped ONLY on success, so a failed search never destroys the
    /// accumulated prefix.
    /// </summary>
    private int Resync(Stack<Item> tokenStack, ISyncLAIterator<Item> tokenIterator,
        IReadOnlySet<int> syncTerminalIds, IReadOnlySet<int> openBracketIds,
        IReadOnlySet<int> closeBracketIds, out Item resumeToken)
    {
        const int initState = 0;
        // Seed the input depth from brackets ALREADY on the stack. When the error fires mid-element the
        // enclosing openers (a `struct {`, a `fn (`) have been shifted but not yet closed, so they're
        // still individual un-reduced terminal items on the stack (a closer would have reduced its group
        // away). Seeding means the discard reaches "depth 0" only after it has consumed the closers for
        // all of those openers — i.e. genuinely exited to the top level — so a struct field's `IDENT` or
        // a nested `comptime` inside the broken element is NOT mistaken for a top-level boundary.
        var startDepth = 0;
        foreach (var stacked in tokenStack)
        {
            if (openBracketIds.Contains(stacked.ID)) { startDepth++; }
            else if (closeBracketIds.Contains(stacked.ID)) { startDepth--; }
        }
        if (startDepth < 0) { startDepth = 0; }

        var depth = startDepth;
        while (true)
        {
            // Discard the broken element's tokens up to the next top-level (depth-0) sync terminal, or EOF.
            while (true)
            {
                var la = tokenIterator.LookAhead();
                if (la.ID == Item.EOF.ID) { break; }
                if (depth == 0 && syncTerminalIds.Contains(la.ID)) { break; }
                if (openBracketIds.Contains(la.ID)) { depth++; }
                else if (closeBracketIds.Contains(la.ID) && depth > 0) { depth--; }
                tokenIterator.MoveNext();
            }
            resumeToken = tokenIterator.LookAhead();
            var col = resumeToken.ID + 1;

            // Pop to the OUTERMOST level: past every unclosed opener on the stack (V1 recovers at the
            // top-level list only), then to the nearest state that actually accepts the resume lookahead.
            // A Stack's ToArray is top-first, so snapshot[k].State is the state exposed after popping k
            // items (k == length ⇒ the initial state). The pop is applied only on success, so a failed
            // search never destroys the accumulated good-element prefix.
            var snapshot = tokenStack.ToArray();
            var minPop = 0;
            for (var k = 0; k < snapshot.Length; k++)
            {
                if (openBracketIds.Contains(snapshot[k].ID)) { minPop = k + 1; }
            }
            for (var k = minPop; k <= snapshot.Length; k++)
            {
                var st = k < snapshot.Length ? snapshot[k].State : initState;
                var act = _parseTable.Actions[st, col].ActionType;
                if (act == ActionType.Shift || act == ActionType.Reduce)
                {
                    for (var i = 0; i < k; i++) { tokenStack.Pop(); }
                    return st;
                }
            }

            // No top-level state accepts this boundary. At EOF the prefix can't be completed → give up
            // (caller returns the best-effort partial tree). Otherwise the sync token is unusable here;
            // discard it and scan on from depth 0 (we were at a depth-0 boundary), guaranteeing progress.
            if (resumeToken.ID == Item.EOF.ID) { return -1; }
            tokenIterator.MoveNext();
            depth = 0;
        }
    }

    /// <summary>Best-effort tree when <see cref="ParseInputResilient"/> hits an unrecoverable tail
    /// (end-of-input with a stack that can't reduce to the start symbol): the bottom-most stacked
    /// item — for a list grammar, the accumulated element prefix — or <see cref="Item.EOF"/> if the
    /// stack is empty. Consumers should consult <see cref="ResilientParseResult.Errors"/> alongside it.</summary>
    private static Item BuildPartialTree(Stack<Item> tokenStack)
    {
        if (tokenStack.Count == 0) { return Item.EOF; }
        var arr = tokenStack.ToArray();   // top..bottom
        return arr[^1];
    }

    /// <summary>
    /// Runtime-build constructor: invokes <see cref="ParserTableBuilder"/> to
    /// compute the parse table from the grammar. Throws <see cref="GrammarConflictException"/>
    /// on any unresolved S/R or R/R conflict (use <see cref="Conflicts"/> on the
    /// exception for details, or place the offending productions in a
    /// <see cref="PrecedenceGroup"/> with <see cref="Derivation.LeftMost"/> /
    /// <see cref="Derivation.RightMost"/>).
    /// </summary>
    public Parser(Grammar grammar)
    {
        _grammar = grammar;
        _builder = new ParserTableBuilder(grammar);
        _parseTable = _builder.ParseTable;
        _productions = _builder.Productions;
        _nonterminals = _builder.NonTerminals;

        if (_builder.Conflicts.Count > 0)
        {
            // Surface unresolved S/R and R/R conflicts immediately rather than waiting for
            // an input to drive the parser through the offending parse-table cell.
            throw new GrammarConflictException(_builder.Conflicts,
                GrammarConflictException.FormatMessage(_builder.Conflicts, _grammar, _builder.Productions));
        }
    }

    /// <summary>
    /// Pre-baked constructor (Phase 5 / compiler-compiler path). Skips
    /// <see cref="ParserTableBuilder"/> entirely — the source generator already
    /// ran the algorithm at build time and emitted the populated
    /// <paramref name="parseTable"/> as a C# literal. Productions and
    /// NonTerminals are derived directly from <paramref name="grammar"/>;
    /// LR0/LR1 introspection state isn't available on this path (see the
    /// individual property docs).
    ///
    /// On this path the trimmer can drop <see cref="ParserTableBuilder"/> and
    /// its dependencies from the consumer's AOT image — the Parser only needs
    /// the parse loop in <see cref="ParseInputAsync"/>.
    /// </summary>
    public Parser(Grammar grammar, ParseTable parseTable)
    {
        _grammar = grammar;
        _parseTable = parseTable;
        _builder = null;

        // Productions: flatten precedence groups in declaration order, matching
        // the order ParserTableBuilder.PopulateProductions uses (so production
        // indices in the pre-baked table line up).
        var productions = new List<Production>();
        foreach (var group in grammar.PrecedenceGroups)
        {
            foreach (var prod in group.Productions)
            {
                productions.Add(prod);
            }
        }
        _productions = productions;

        // Non-terminals: any symbol that appears as the left-hand side of a
        // production. Same definition ParserTableBuilder.InitSymbols uses.
        var nonterminals = new HashSet<int>();
        foreach (var prod in productions)
        {
            nonterminals.Add(prod.Left);
        }
        _nonterminals = nonterminals;
    }
}
