using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LALR.CC.LexicalGrammar.Dfa;

namespace LALR.CC.LexicalGrammar;

/// <summary>
/// The byte DFA of every state of a lexer pattern table, compiled once and shared by every
/// <see cref="BytesLexer"/> and <see cref="PipeBytesLexer"/> built over the same rules. A
/// state's DFA costs a Thompson construction, a subset construction and a UTF-8 lowering,
/// while a lexer over a short input (a macro body, an included header) does little else, so
/// a host that makes many lexers spent most of its time rebuilding identical DFAs.
/// </summary>
/// <remarks>
/// The cache is keyed by the root state's rule array, not by the table: a generated
/// <c>BuildLexer()</c> returns a new dictionary on each call over the same static rule
/// arrays, and should hit. A hit is used only when the table still holds exactly the rules
/// the entry was compiled from (the same states, each with equal rules in the same order),
/// so a table that was changed, or another table that shares the root array, recompiles.
/// The compiled DFAs are read-only, so lexers on different threads share them safely.
/// </remarks>
internal sealed class CompiledLexerTable
{
    /// <summary>One lexer state: its byte DFA, whose pattern ids index <see cref="Rules"/>, and its
    /// trailing-context rules, which the DFA leaves out.</summary>
    internal readonly struct State(Dfa.Dfa byteDfa, LexRule[] rules, LookaheadRule[] lookaheads)
    {
        public Dfa.Dfa ByteDfa { get; } = byteDfa;
        public LexRule[] Rules { get; } = rules;
        public LookaheadRule[] Lookaheads { get; } = lookaheads;
    }

    /// <summary>A rule with trailing context (<see cref="LexRule.Lookahead"/>): its index in
    /// <see cref="State.Rules"/>, a DFA for its pattern and one for its lookahead. A lexer runs them
    /// after the state's DFA, so the rule competes in longest-match on the pattern's length.</summary>
    internal readonly struct LookaheadRule(int rule, Dfa.Dfa head, Dfa.Dfa trail)
    {
        public int Rule { get; } = rule;
        public Dfa.Dfa Head { get; } = head;
        public Dfa.Dfa Trail { get; } = trail;
    }

    private static readonly ConditionalWeakTable<LexRule[], CompiledLexerTable> Cache = new();

    /// <summary>Each state of the table, by name.</summary>
    public IReadOnlyDictionary<string, State> States => _states;

    private readonly Dictionary<string, State> _states;

    /// <summary>The rules each state was compiled from, copied: the arrays are the
    /// caller's, and could be changed in place after the compile.</summary>
    private readonly Dictionary<string, LexRule[]> _source;

    private CompiledLexerTable(Dictionary<string, State> states, Dictionary<string, LexRule[]> source)
    {
        _states = states;
        _source = source;
    }

    /// <summary>The compiled states of <paramref name="patternTable"/>, from the cache when
    /// an entry for the same rules exists. <paramref name="paramName"/> names the lexer
    /// argument an invalid table is reported against.</summary>
    public static CompiledLexerTable For(IReadOnlyDictionary<string, LexRule[]> patternTable, string paramName)
    {
        ArgumentNullException.ThrowIfNull(patternTable);
        if (!patternTable.TryGetValue(PipeBytesLexer.RootState, out var root) || root is null)
        {
            throw new ArgumentException($"pattern table must contain a '{PipeBytesLexer.RootState}' state", paramName);
        }
        if (Cache.TryGetValue(root, out var cached) && cached.CompiledFrom(patternTable))
        {
            return cached;
        }
        var compiled = Compile(patternTable, paramName);
        Cache.AddOrUpdate(root, compiled);
        return compiled;
    }

    /// <summary>True when <paramref name="patternTable"/> holds exactly the rules this
    /// table was compiled from.</summary>
    private bool CompiledFrom(IReadOnlyDictionary<string, LexRule[]> patternTable)
    {
        if (patternTable.Count != _source.Count) { return false; }
        foreach (var (name, rules) in patternTable)
        {
            if (rules is null || !_source.TryGetValue(name, out var source) || source.Length != rules.Length)
            {
                return false;
            }
            for (var i = 0; i < rules.Length; i++)
            {
                if (!rules[i].Equals(source[i])) { return false; }
            }
        }
        return true;
    }

    private static CompiledLexerTable Compile(IReadOnlyDictionary<string, LexRule[]> patternTable, string paramName)
    {
        var states = new Dictionary<string, State>(patternTable.Count, StringComparer.Ordinal);
        var source = new Dictionary<string, LexRule[]>(patternTable.Count, StringComparer.Ordinal);
        foreach (var (name, rules) in patternTable)
        {
            if (rules is null || rules.Length == 0)
            {
                throw new ArgumentException($"state '{name}' has no rules", paramName);
            }
            // Each rule's index becomes its DFA pattern id, so the smallest accepting id
            // at any DFA state corresponds to the first matching rule (first-pattern-wins).
            // A trailing-context rule gets DFAs of its own instead, which keep its index.
            var dfaPatterns = new List<(IRx, int)>(rules.Length);
            var lookaheads = new List<LookaheadRule>();
            for (var i = 0; i < rules.Length; i++)
            {
                if (rules[i].Lookahead is { } trail)
                {
                    lookaheads.Add(new LookaheadRule(i, CompileOne(rules[i].Pattern), CompileOne(trail)));
                }
                else
                {
                    dfaPatterns.Add((rules[i].Pattern, i));
                }
            }
            var copy = (LexRule[])rules.Clone();
            var dfa = dfaPatterns.Count > 0
                ? Utf8DfaLowering.Lower(DfaCompiler.CompileMany(dfaPatterns))
                : new Dfa.Dfa(new[] { new DfaState(-1, Array.Empty<DfaTransition>()) });   // matches nothing
            states[name] = new State(dfa, copy, lookaheads.ToArray());
            source[name] = copy;
        }
        return new CompiledLexerTable(states, source);
    }

    private static Dfa.Dfa CompileOne(IRx pattern) => Utf8DfaLowering.Lower(DfaCompiler.CompileMany(new[] { (pattern, 0) }));

    /// <summary>Let the trailing-context rules of <paramref name="state"/> compete with the DFA's best
    /// match over <paramref name="input"/> (the bytes from the token start): a rule whose pattern
    /// matches longer, or as long with an earlier rule index, wins when its lookahead matches at the
    /// pattern's end. The pattern's longest match is the one tried. Returns false, changing nothing,
    /// when a DFA ran off the end of an <paramref name="input"/> that is not
    /// <paramref name="complete"/>, so the answer needs more bytes.</summary>
    internal static bool ApplyLookaheads(in State state, ReadOnlySpan<byte> input, bool complete, ref long bestLen, ref int bestPattern)
    {
        var len = bestLen;
        var pattern = bestPattern;
        foreach (var la in state.Lookaheads)
        {
            var (headLen, headAtEnd) = Run(la.Head, input, firstAccept: false);
            if (headAtEnd && !complete) { return false; }
            if (headLen <= 0) { continue; }
            if (headLen < len || (headLen == len && pattern >= 0 && la.Rule > pattern)) { continue; }
            var (trailLen, trailAtEnd) = Run(la.Trail, input.Slice((int)headLen), firstAccept: true);
            if (trailLen < 0)
            {
                if (trailAtEnd && !complete) { return false; }
                continue;
            }
            len = headLen;
            pattern = la.Rule;
        }
        bestLen = len;
        bestPattern = pattern;
        return true;
    }

    /// <summary>The longest accepting prefix of <paramref name="input"/> under <paramref name="dfa"/> (or the first, with
    /// <paramref name="firstAccept"/>), -1 for none, and whether the DFA was still running at the input's end.</summary>
    private static (long Length, bool AtEnd) Run(Dfa.Dfa dfa, ReadOnlySpan<byte> input, bool firstAccept)
    {
        var state = dfa.Start;
        long best = dfa.States[state].IsAccept ? 0 : -1;
        if (best == 0 && firstAccept) { return (0, false); }
        for (var i = 0; i < input.Length; i++)
        {
            state = dfa.Step(state, input[i]);
            if (state < 0) { return (best, false); }
            if (dfa.States[state].IsAccept)
            {
                best = i + 1;
                if (firstAccept) { return (best, false); }
            }
        }
        return (best, true);
    }
}
