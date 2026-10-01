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
    /// <summary>One lexer state: its byte DFA, whose pattern ids index <see cref="Rules"/>.</summary>
    internal readonly struct State(Dfa.Dfa byteDfa, LexRule[] rules)
    {
        public Dfa.Dfa ByteDfa { get; } = byteDfa;
        public LexRule[] Rules { get; } = rules;
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
            var dfaPatterns = new (IRx, int)[rules.Length];
            for (var i = 0; i < rules.Length; i++)
            {
                dfaPatterns[i] = (rules[i].Pattern, i);
            }
            var copy = (LexRule[])rules.Clone();
            states[name] = new State(Utf8DfaLowering.Lower(DfaCompiler.CompileMany(dfaPatterns)), copy);
            source[name] = copy;
        }
        return new CompiledLexerTable(states, source);
    }
}
