using System;
using System.Collections.Generic;
using System.Linq;

namespace LALR.CC.LexicalGrammar.Dfa;

/// <summary>
/// Compiles the typed regex AST (<see cref="IRx"/>) into a <see cref="Dfa"/> via
/// Thompson construction (NFA) and subset construction (NFA → DFA). The codepoint
/// alphabet is the full scalar range [0..0x10FFFF]. Surrogates are not filtered out;
/// they simply never reach the matcher because <see cref="PipeRuneIterator"/> only
/// emits valid Unicode scalars.
/// </summary>
public static class DfaCompiler
{
    private const int MinCodepoint = 0;
    private const int MaxCodepoint = 0x10FFFF;

    /// <summary>Compile a single pattern; the resulting DFA accepts with id <paramref name="patternId"/>.</summary>
    public static Dfa Compile(IRx pattern, int patternId = 0)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return CompileMany([(pattern, patternId)]);
    }

    /// <summary>
    /// Compile multiple patterns into one DFA. Each pattern keeps its id; on overlap
    /// the smaller id wins (first-pattern-wins semantics). The longest match overall
    /// wins on differing prefix lengths.
    /// </summary>
    public static Dfa CompileMany(IReadOnlyList<(IRx Pattern, int PatternId)> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Count == 0)
        {
            throw new ArgumentException("At least one pattern is required", nameof(patterns));
        }

        var nfa = new NfaBuilder();
        var combinedStart = nfa.AddState();
        foreach (var (rx, id) in patterns)
        {
            var (start, accept) = nfa.Visit(rx);
            nfa.AddEpsilon(combinedStart, start);
            nfa.States[accept].Accept = id;
        }

        return SubsetConstruction.Build(nfa, combinedStart);
    }
}

internal sealed class NfaState
{
    public List<int> EpsilonTo { get; } = [];
    public List<NfaTransition> RangeTo { get; } = [];
    public int Accept { get; set; } = -1;
}

internal readonly struct NfaTransition(int lo, int hi, int next)
{
    public int Lo { get; } = lo;
    public int Hi { get; } = hi;
    public int Next { get; } = next;
}

internal sealed class NfaBuilder
{
    public List<NfaState> States { get; } = [];

    public int AddState()
    {
        States.Add(new NfaState());
        return States.Count - 1;
    }

    public void AddEpsilon(int from, int to) => States[from].EpsilonTo.Add(to);

    public void AddRange(int from, int lo, int hi, int to)
        => States[from].RangeTo.Add(new NfaTransition(lo, hi, to));

    /// <summary>Build an NFA fragment for <paramref name="rx"/> and return its (start, accept).</summary>
    public (int Start, int Accept) Visit(IRx rx)
    {
        return rx switch
        {
            CharRx c => Literal(c.Codepoint, c.Codepoint),
            CharRangeRx r => Literal(r.From, r.To),
            CharClassRx cc => Class(cc),
            CharSequenceRx s => ConcatChars(s.Chars),
            GroupRx g => Repeat(g.Items, g.Multiplicity),
            AlternationRx a => Alternation(a.Alternatives),
            _ => throw new NotSupportedException($"Unsupported IRx node: {rx.GetType()}"),
        };
    }

    /// <summary>
    /// Thompson construction for alternation: fresh start ε-edges to
    /// each alternative's start, each alternative's accept ε-edges to
    /// a fresh union accept. NFA subset construction collapses the
    /// fan-in/fan-out epsilons into the right DFA structure.
    /// </summary>
    private (int Start, int Accept) Alternation(IReadOnlyList<IRx> alternatives)
    {
        var start = AddState();
        var accept = AddState();
        foreach (var alt in alternatives)
        {
            var (altStart, altAccept) = Visit(alt);
            AddEpsilon(start, altStart);
            AddEpsilon(altAccept, accept);
        }
        return (start, accept);
    }

    private (int Start, int Accept) Literal(int lo, int hi)
    {
        var s = AddState();
        var a = AddState();
        AddRange(s, lo, hi, a);
        return (s, a);
    }

    private (int Start, int Accept) Epsilon()
    {
        var s = AddState();
        var a = AddState();
        AddEpsilon(s, a);
        return (s, a);
    }

    private (int Start, int Accept) Class(CharClassRx cc)
    {
        var ranges = ExtractRanges(cc.Chars);
        if (!cc.Positive)
        {
            ranges = Complement(ranges);
        }
        if (ranges.Count == 0)
        {
            // Negative class covering everything: still produce a state but no transitions
            // means "never accept". Return an isolated fragment so concatenation behaves.
            var s0 = AddState();
            var a0 = AddState();
            return (s0, a0);
        }
        var s = AddState();
        var a = AddState();
        foreach (var (lo, hi) in ranges)
        {
            AddRange(s, lo, hi, a);
        }
        return (s, a);
    }

    private (int Start, int Accept) ConcatChars(IReadOnlyList<CharRx> chars)
    {
        if (chars.Count == 0)
        {
            return Epsilon();
        }
        var first = chars[0].Codepoint;
        var frag = Literal(first, first);
        for (var i = 1; i < chars.Count; i++)
        {
            var cp = chars[i].Codepoint;
            var next = Literal(cp, cp);
            AddEpsilon(frag.Accept, next.Start);
            frag = (frag.Start, next.Accept);
        }
        return frag;
    }

    private (int Start, int Accept) ConcatItems(IReadOnlyList<IRx> items)
    {
        if (items.Count == 0)
        {
            return Epsilon();
        }
        var frag = Visit(items[0]);
        for (var i = 1; i < items.Count; i++)
        {
            var next = Visit(items[i]);
            AddEpsilon(frag.Accept, next.Start);
            frag = (frag.Start, next.Accept);
        }
        return frag;
    }

    private (int Start, int Accept) Repeat(IReadOnlyList<IRx> items, Multiplicity m)
    {
        var from = m.From;
        var to = m.To;

        // build a fresh inner fragment each call so we can clone for repetition.
        (int, int) Inner() => ConcatItems(items);

        if (from == to)
        {
            return RepeatExact(Inner, from);
        }
        if (from == 0 && to == 1)
        {
            return Optional(Inner());
        }
        if (from == 0 && to == -1)
        {
            return Star(Inner());
        }
        if (from == 1 && to == -1)
        {
            return Plus(Inner());
        }
        if (to == -1)
        {
            // {n, ∞}: n required, then star tail
            var head = RepeatExact(Inner, from);
            var tail = Star(Inner());
            AddEpsilon(head.Accept, tail.Start);
            return (head.Start, tail.Accept);
        }
        // {n, m}: n required, then (m - n) optional in sequence
        var fixedHead = RepeatExact(Inner, from);
        var optTail = OptionalChain(Inner, to - from);
        AddEpsilon(fixedHead.Accept, optTail.Start);
        return (fixedHead.Start, optTail.Accept);
    }

    private (int Start, int Accept) RepeatExact(Func<(int, int)> inner, int n)
    {
        if (n == 0)
        {
            return Epsilon();
        }
        var (s, a) = inner();
        for (var i = 1; i < n; i++)
        {
            var next = inner();
            AddEpsilon(a, next.Item1);
            a = next.Item2;
        }
        return (s, a);
    }

    private (int Start, int Accept) Optional((int Start, int Accept) inner)
    {
        // bypass edge: start ↦ accept lets us skip the inner
        AddEpsilon(inner.Start, inner.Accept);
        return inner;
    }

    private (int Start, int Accept) Star((int Start, int Accept) inner)
    {
        var s = AddState();
        var a = AddState();
        AddEpsilon(s, inner.Start);
        AddEpsilon(s, a);
        AddEpsilon(inner.Accept, inner.Start);
        AddEpsilon(inner.Accept, a);
        return (s, a);
    }

    private (int Start, int Accept) Plus((int Start, int Accept) inner)
    {
        // one-or-more = once + optional loop back; reuses the inner fragment.
        AddEpsilon(inner.Accept, inner.Start);
        return inner;
    }

    private (int Start, int Accept) OptionalChain(Func<(int, int)> inner, int count)
    {
        if (count <= 0)
        {
            return Epsilon();
        }
        var (s, a) = Optional(inner());
        for (var i = 1; i < count; i++)
        {
            var next = Optional(inner());
            AddEpsilon(a, next.Item1);
            a = next.Item2;
        }
        return (s, a);
    }

    private static List<(int Lo, int Hi)> ExtractRanges(IReadOnlyList<ISingleCharRx> items)
    {
        var ranges = new List<(int Lo, int Hi)>(items.Count);
        foreach (var item in items)
        {
            switch (item)
            {
                case CharRx cr:
                    ranges.Add((cr.Codepoint, cr.Codepoint));
                    break;
                case CharRangeRx rr:
                    ranges.Add((rr.From, rr.To));
                    break;
                case CharClassRx subClass:
                    // CharClassRx.ItemToPattern enforces matching polarity at construction time,
                    // so flattening nested positive classes is safe.
                    foreach (var inner in ExtractRanges(subClass.Chars))
                    {
                        ranges.Add(inner);
                    }
                    break;
                default:
                    throw new NotSupportedException($"Unsupported ISingleCharRx node: {item.GetType()}");
            }
        }
        return MergeOverlapping(ranges);
    }

    private static List<(int Lo, int Hi)> MergeOverlapping(List<(int Lo, int Hi)> ranges)
    {
        if (ranges.Count <= 1)
        {
            return ranges;
        }
        ranges.Sort(static (a, b) => a.Lo.CompareTo(b.Lo));
        var merged = new List<(int Lo, int Hi)>(ranges.Count);
        var current = ranges[0];
        for (var i = 1; i < ranges.Count; i++)
        {
            var next = ranges[i];
            if (next.Lo <= current.Hi + 1)
            {
                current = (current.Lo, Math.Max(current.Hi, next.Hi));
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);
        return merged;
    }

    private static List<(int Lo, int Hi)> Complement(IReadOnlyList<(int Lo, int Hi)> ranges)
    {
        var result = new List<(int Lo, int Hi)>(ranges.Count + 1);
        var cursor = 0;
        foreach (var (lo, hi) in ranges)
        {
            if (cursor <= lo - 1)
            {
                result.Add((cursor, lo - 1));
            }
            cursor = hi + 1;
        }
        if (cursor <= 0x10FFFF)
        {
            result.Add((cursor, 0x10FFFF));
        }
        return result;
    }
}

internal static class SubsetConstruction
{
    public static Dfa Build(NfaBuilder nfa, int start)
    {
        var subsets = new List<int[]>();
        var subsetIndex = new Dictionary<string, int>();

        int Intern(SortedSet<int> set, out bool added)
        {
            // Subset key: sorted state IDs joined by ','. The SortedSet already gives
            // ascending order; this is fine for moderate-sized DFAs (a few hundred states).
            // A perfect-hash key would be faster but adds complexity for negligible gain here.
            var key = string.Join(",", set);
            if (subsetIndex.TryGetValue(key, out var idx))
            {
                added = false;
                return idx;
            }
            idx = subsets.Count;
            subsets.Add([.. set]);
            subsetIndex[key] = idx;
            added = true;
            return idx;
        }

        var initialClosure = EpsilonClosure([start], nfa);
        Intern(initialClosure, out _);

        var dfaStates = new List<DfaState>();
        var queue = new Queue<int>();
        queue.Enqueue(0);
        while (dfaStates.Count < subsets.Count)
        {
            dfaStates.Add(default);
        }

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var subset = subsets[index];

            var accept = -1;
            foreach (var s in subset)
            {
                var a = nfa.States[s].Accept;
                if (a >= 0 && (accept < 0 || a < accept))
                {
                    accept = a;
                }
            }

            var partitions = PartitionAlphabet(subset, nfa);
            var transitions = new List<DfaTransition>(partitions.Count);
            foreach (var (lo, hi) in partitions)
            {
                var dest = new SortedSet<int>();
                foreach (var s in subset)
                {
                    foreach (var t in nfa.States[s].RangeTo)
                    {
                        // partition intervals are subsets of the union of all transition
                        // ranges, so this containment check selects exactly the transitions
                        // that should fire on any codepoint in [lo..hi].
                        if (t.Lo <= lo && t.Hi >= hi)
                        {
                            dest.Add(t.Next);
                        }
                    }
                }
                if (dest.Count == 0)
                {
                    continue;
                }
                var destClosure = EpsilonClosure(dest, nfa);
                var destIdx = Intern(destClosure, out var added);
                if (added)
                {
                    while (dfaStates.Count < subsets.Count)
                    {
                        dfaStates.Add(default);
                    }
                    queue.Enqueue(destIdx);
                }
                transitions.Add(new DfaTransition(lo, hi, destIdx));
            }
            transitions.Sort(static (a, b) => a.Lo.CompareTo(b.Lo));
            var merged = MergeAdjacent(transitions);
            dfaStates[index] = new DfaState(accept, [.. merged]);
        }

        return new Dfa([.. dfaStates]);
    }

    private static SortedSet<int> EpsilonClosure(IEnumerable<int> seed, NfaBuilder nfa)
    {
        var closure = new SortedSet<int>(seed);
        var stack = new Stack<int>(closure);
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            foreach (var t in nfa.States[s].EpsilonTo)
            {
                if (closure.Add(t))
                {
                    stack.Push(t);
                }
            }
        }
        return closure;
    }

    private static List<(int Lo, int Hi)> PartitionAlphabet(int[] subset, NfaBuilder nfa)
    {
        // Collect all interval boundaries from every transition in the subset, then
        // build maximal-disjoint intervals between consecutive boundaries. Each resulting
        // interval is uniformly covered (or not) by every original transition.
        var points = new SortedSet<int>();
        foreach (var s in subset)
        {
            foreach (var t in nfa.States[s].RangeTo)
            {
                points.Add(t.Lo);
                points.Add(t.Hi + 1);
            }
        }
        var partitions = new List<(int Lo, int Hi)>();
        int? prev = null;
        foreach (var p in points)
        {
            if (prev is int prevValue)
            {
                partitions.Add((prevValue, p - 1));
            }
            prev = p;
        }
        return partitions;
    }

    private static List<DfaTransition> MergeAdjacent(List<DfaTransition> transitions)
    {
        var merged = new List<DfaTransition>(transitions.Count);
        if (transitions.Count == 0)
        {
            return merged;
        }
        var current = transitions[0];
        for (var i = 1; i < transitions.Count; i++)
        {
            var next = transitions[i];
            if (next.Lo == current.Hi + 1 && next.Next == current.Next)
            {
                current = new DfaTransition(current.Lo, next.Hi, current.Next);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);
        return merged;
    }
}
