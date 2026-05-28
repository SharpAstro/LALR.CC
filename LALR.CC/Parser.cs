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
