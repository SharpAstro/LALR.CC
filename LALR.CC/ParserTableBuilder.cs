using System.Collections.Generic;
using System.Linq;

namespace LALR.CC;

/// <summary>
/// Builds the LALR(1) parse table for a <see cref="Grammar"/>. Pure C#: no I/O,
/// no async, no <c>LexicalGrammar</c> dependency. Owns all the table-construction
/// state (LR0/LR1 item sets, kernels, propogations, FIRST sets, etc.) and exposes
/// it through public read-only properties so <see cref="Parser"/> can passthrough
/// them as introspection surface for <see cref="Debug"/> and <c>lalr-tui</c>.
///
/// Phase 5 / compiler-compiler: this class is `&lt;Compile Link&gt;`-shared into the
/// netstandard2.0 source generator so the generator can run the same algorithm
/// at build time and emit a populated <see cref="ParseTable"/> directly,
/// trimming the table-build code out of consumer AOT images.
/// </summary>
public sealed class ParserTableBuilder
{
    private readonly HashSet<int>[] _firstSets;
    private readonly List<LR0Item> _lr0Items;
    private readonly List<LR1Item> _lr1Items;

    // O(1) interning maps parallel to the item/state lists (list index == id), so
    // GetLR0ItemID/GetLR1ItemID/GetLR0StateID hash-look-up instead of linear-scanning
    // every item built so far (quadratic on a large grammar's tens of thousands of
    // items). Ids are unchanged — still assigned in first-seen order — so the emitted
    // parse table is byte-identical. The public LR0Items/LR1Items/LR0States
    // introspection surface is untouched.
    private readonly Dictionary<LR0Item, int> _lr0ItemIds;
    private readonly Dictionary<LR1Item, int> _lr1ItemIds;
    private readonly Dictionary<int, List<int>> _lr0StateBuckets;

    private readonly List<HashSet<int>> _lr0States;
    private readonly List<HashSet<int>> _lr0Kernels;
    private readonly List<HashSet<int>> _lalrStates;

    private readonly List<int[]> _lrGotos;
    private readonly List<int[]> _gotoPrecedence;

    private readonly Grammar _grammar;
    private readonly List<Production> _productions;
    private readonly HashSet<int> _terminals;
    private readonly HashSet<int> _nonterminals;

    private readonly List<IDictionary<int, IList<LALRPropogation>>> _lalrPropogations;

    private ParseTable _parseTable;

    private readonly List<int> _productionPrecedence;
    private readonly List<Derivation> _productionDerivation;

    private readonly List<GrammarConflict> _conflicts;

    public Grammar Grammar => _grammar;

    public ParseTable ParseTable => _parseTable;

    public IReadOnlyList<HashSet<int>> FirstSets => _firstSets;

    public IReadOnlyList<LR0Item> LR0Items => _lr0Items;

    public IReadOnlyList<LR1Item> LR1Items => _lr1Items;

    public IReadOnlyList<HashSet<int>> LR0States => _lr0States;

    public IReadOnlyList<HashSet<int>> LR0Kernels => _lr0Kernels;

    public IReadOnlyList<HashSet<int>> LALRStates => _lalrStates;

    public IReadOnlyList<int[]> LRGotos => _lrGotos;

    public IReadOnlyList<int[]> GotoPrecedence => _gotoPrecedence;

    public IReadOnlyList<Production> Productions => _productions;

    // Concrete HashSet<int> rather than IReadOnlySet<int>: the latter is .NET 5+
    // and this class is `<Compile Link>`-shared into the netstandard2.0 source
    // generator. HashSet keeps Contains() available for Debug.cs / consumers.
    public HashSet<int> Terminals => _terminals;

    public HashSet<int> NonTerminals => _nonterminals;

    public IReadOnlyList<IDictionary<int, IList<LALRPropogation>>> LALRPropogations => _lalrPropogations;

    public IReadOnlyList<int> ProductionPrecedence => _productionPrecedence;

    public IReadOnlyList<Derivation> ProductionDerivation => _productionDerivation;

    public IReadOnlyList<GrammarConflict> Conflicts => _conflicts;

    public ParserTableBuilder(Grammar grammar)
    {
        _lrGotos = [];
        _gotoPrecedence = [];
        _lr0Items = [];
        _lr1Items = [];
        _lr0ItemIds = new Dictionary<LR0Item, int>();
        _lr1ItemIds = new Dictionary<LR1Item, int>();
        _lr0StateBuckets = new Dictionary<int, List<int>>();
        _lr0States = [];
        _lr0Kernels = [];
        _lalrStates = [];
        _terminals = [];
        _nonterminals = [];
        _lalrPropogations = [];
        _grammar = grammar;
        _productions = [];
        _productionDerivation = [];
        _productionPrecedence = [];
        _conflicts = [];
        var nTokens = _grammar.SymbolNames.Length;
        _firstSets = new HashSet<int>[nTokens];

        PopulateProductions();
        InitSymbols();
        GenerateLR0Items();
        ComputeFirstSets();
        ConvertLR0ItemsToKernels();
        var nLalrStates = InitLALRTables();
        CalculateLookAheads();

        _parseTable = new ParseTable(nLalrStates, nTokens);
        GenerateParseTable();
    }

    /// <summary>
    /// Adds a propogation to the propogation table
    /// </summary>
    private void AddPropogation(int nLR0SourceState, int nLR0SourceItem, int nLR0TargetState, int nLR0TargetItem)
    {
        while (_lalrPropogations.Count <= nLR0SourceState)
        {
            _lalrPropogations.Add(new Dictionary<int, IList<LALRPropogation>>());
        }

        var propogationsForState = _lalrPropogations[nLR0SourceState];
        if (!propogationsForState.TryGetValue(nLR0SourceItem, out var propogationList))
        {
            propogationList = new List<LALRPropogation>();
            propogationsForState[nLR0SourceItem] = propogationList;
        }

        propogationList.Add(new LALRPropogation(nLR0TargetItem, nLR0TargetState));
    }

    /// <summary>
    /// Gets the ID for a particular LR0 Item
    /// </summary>
    private int GetLR0ItemID(LR0Item item)
    {
        if (_lr0ItemIds.TryGetValue(item, out var nItemID))
        {
            return nItemID;
        }
        nItemID = _lr0Items.Count;
        _lr0Items.Add(item);
        _lr0ItemIds[item] = nItemID;
        return nItemID;
    }

    /// <summary>
    /// Gets the ID for a particular LR1 Item
    /// </summary>
    private int GetLR1ItemID(LR1Item item)
    {
        if (_lr1ItemIds.TryGetValue(item, out var nItemID))
        {
            return nItemID;
        }
        nItemID = _lr1Items.Count;
        _lr1Items.Add(item);
        _lr1ItemIds[item] = nItemID;
        return nItemID;
    }

    /// <summary>
    /// Gets the ID for a particular LR0 State
    /// </summary>
    private int GetLR0StateID(HashSet<int> state, ref bool bAdded)
    {
        // Bucket candidate states by an order-independent set hash so SetEquals only
        // runs against the (usually one) states that share a hash, instead of every
        // state built so far. Ids are still handed out in first-seen order.
        var hash = SetHash(state);
        if (_lr0StateBuckets.TryGetValue(hash, out var bucket))
        {
            foreach (var nCandidate in bucket)
            {
                if (_lr0States[nCandidate].SetEquals(state))
                {
                    return nCandidate;
                }
            }
        }
        else
        {
            bucket = new List<int>();
            _lr0StateBuckets[hash] = bucket;
        }

        var nStateID = _lr0States.Count;
        _lr0States.Add(state);
        bucket.Add(nStateID);
        bAdded = true;
        return nStateID;
    }

    /// <summary>
    /// Order-independent (commutative) hash of a state's LR0-item-id set, used only to
    /// bucket candidates for <see cref="GetLR0StateID"/>. Collisions are harmless —
    /// <see cref="HashSet{T}.SetEquals"/> stays the authority within a bucket.
    /// </summary>
    private static int SetHash(HashSet<int> set)
    {
        unchecked
        {
            var h = set.Count;
            foreach (var x in set)
            {
                // murmur-style avalanche per element, XOR-combined so the element
                // order (HashSet enumeration order) doesn't affect the result.
                var k = x * unchecked((int)0xcc9e2d51);
                k = (k << 15) | (int)((uint)k >> 17);
                k *= 0x1b873593;
                h ^= k;
            }
            return h;
        }
    }

    /// <summary>
    /// takes a set of LR0 Items and Produces all of the LR0 Items that are reachable by substitution
    /// </summary>
    private HashSet<int> LR0Closure(IEnumerable<int> items)
    {
        var closed = new HashSet<int>();
        // FIFO worklist via Queue (not List.RemoveAt(0), which shifts the whole list on
        // every pop) + a "seen" set for O(1) membership (was open.Contains, an O(n)
        // scan). FIFO order is preserved, so items are interned in the same order.
        var open = new Queue<int>();
        var seen = new HashSet<int>();
        foreach (var nSeed in items)
        {
            if (seen.Add(nSeed))
            {
                open.Enqueue(nSeed);
            }
        }

        while (open.Count > 0)
        {
            var nItem = open.Dequeue();
            var item = _lr0Items[nItem];
            closed.Add(nItem);

            var nProduction = 0;
            foreach (var production in _productions)
            {
                if ((item.Position < _productions[item.Production].Right.Length) && (production.Left == _productions[item.Production].Right[item.Position]))
                {
                    var newItem = new LR0Item(nProduction, 0);
                    var nNewItemID = GetLR0ItemID(newItem);
                    if (seen.Add(nNewItemID))
                    {
                        open.Enqueue(nNewItemID);
                    }
                }
                nProduction++;
            }
        }

        return closed;
    }

    /// <summary>
    /// takes a set of LR1 Items (LR0 items with lookaheads) and produces all of those LR1 items reachable by substitution
    /// </summary>
    private HashSet<int> LR1Closure(IEnumerable<int> items)
    {
        var closed = new HashSet<int>();
        // FIFO worklist + O(1) "seen" membership (see LR0Closure) — same interning order.
        var open = new Queue<int>();
        var seen = new HashSet<int>();
        foreach (var nSeed in items)
        {
            if (seen.Add(nSeed))
            {
                open.Enqueue(nSeed);
            }
        }

        while (open.Count > 0)
        {
            var nLR1Item = open.Dequeue();
            var lr1Item = _lr1Items[nLR1Item];
            var lr0Item = _lr0Items[lr1Item.LR0ItemID];
            closed.Add(nLR1Item);

            if (lr0Item.Position < _productions[lr0Item.Production].Right.Length)
            {
                var nToken = _productions[lr0Item.Production].Right[lr0Item.Position];
                if (_nonterminals.Contains(nToken))
                {
                    var argFirst = new List<int>();
                    for (var nIdx = lr0Item.Position + 1; nIdx < _productions[lr0Item.Production].Right.Length; nIdx++)
                    {
                        argFirst.Add(_productions[lr0Item.Production].Right[nIdx]);
                    }
                    var first = First(argFirst, lr1Item.LookAhead);
                    var nProduction = 0;
                    foreach (var production in _productions)
                    {
                        if (production.Left == nToken)
                        {
                            foreach (var nTokenFirst in first)
                            {
                                var newLR0Item = new LR0Item(nProduction, 0);
                                var nNewLR0ItemID = GetLR0ItemID(newLR0Item);
                                var newLR1Item = new LR1Item(nNewLR0ItemID, nTokenFirst);
                                var nNewLR1ItemID = GetLR1ItemID(newLR1Item);
                                if (seen.Add(nNewLR1ItemID))
                                {
                                    open.Enqueue(nNewLR1ItemID);
                                }
                            }
                        }
                        nProduction++;
                    }
                }
            }
        }

        return closed;
    }

    /// <summary>
    /// takes an LR0 state, and a tokenID, and produces the next state given the Item and productions of the grammar
    /// </summary>
    private int GotoLR0(int nState, int nTokenID, ref bool bAdded, ref int nPrecedence)
    {
        var gotoLR0 = new HashSet<int>();
        var state = _lr0States[nState];
        foreach (var nItem in state)
        {
            var item = _lr0Items[nItem];
            if (item.Position < _productions[item.Production].Right.Length && (_productions[item.Production].Right[item.Position] == nTokenID))
            {
                var newItem = new LR0Item(item.Production, item.Position + 1);
                var nNewItemID = GetLR0ItemID(newItem);
                gotoLR0.Add(nNewItemID);
                var nProductionPrecedence = _productionPrecedence[item.Production];
                if (nPrecedence < nProductionPrecedence)
                {
                    nPrecedence = nProductionPrecedence;
                }
            }
        }
        return gotoLR0.Count == 0 ? -1 : GetLR0StateID(LR0Closure(gotoLR0), ref bAdded);
    }

    /// <summary>
    /// Generates all of the LR 0 Items
    /// </summary>
    private void GenerateLR0Items()
    {
        var startState = new HashSet<int> { GetLR0ItemID(new LR0Item(0, 0)) };

        var bIgnore = false;
        // FIFO worklist via Queue (List.RemoveAt(0) shifts every element on each pop —
        // O(states^2) over the whole state set). Each state is enqueued exactly once,
        // gated by GotoLR0's bAdded (set only when GetLR0StateID mints a new state), so
        // no membership set is needed and processing order is unchanged.
        var open = new Queue<int>();
        open.Enqueue(GetLR0StateID(LR0Closure(startState), ref bIgnore));

        while (open.Count > 0)
        {
            var nState = open.Dequeue();
            while (_lrGotos.Count <= nState)
            {
                _lrGotos.Add(new int[_grammar.SymbolNames.Length]);
                _gotoPrecedence.Add(new int[_grammar.SymbolNames.Length]);
            }

            for (var nToken = 0; nToken < _grammar.SymbolNames.Length; nToken++)
            {
                var bAdded = false;
                var nPrecedence = int.MinValue;
                var nGoto = GotoLR0(nState, nToken, ref bAdded, ref nPrecedence);

                _lrGotos[nState][nToken] = nGoto;
                _gotoPrecedence[nState][nToken] = nPrecedence;

                if (bAdded)
                {
                    open.Enqueue(nGoto);
                }
            }
        }
    }

    /// <summary>
    /// Computes the set of first terminals for each Item in the grammar
    /// </summary>
    private void ComputeFirstSets()
    {
        var nCountTokens = _nonterminals.Count + _terminals.Count;

        for (var nIdx = 0; nIdx < nCountTokens; nIdx++)
        {
            _firstSets[nIdx] = [];
            if (_terminals.Contains(nIdx))
            {
                _firstSets[nIdx].Add(nIdx);
            }
        }

        foreach (var production in _productions)
        {
            if (production.Right.Length == 0)
            {
                _firstSets[production.Left].Add(-1);
            }
        }

        bool bDidSomething;
        do
        {
            bDidSomething = false;
            foreach (var production in _productions)
            {
                foreach (var nToken in production.Right)
                {
                    var bLookAhead = false;
                    foreach (var nTokenFirst in _firstSets[nToken])
                    {
                        if (nTokenFirst == -1)
                        {
                            bLookAhead = true;
                        }
                        else if (_firstSets[production.Left].Add(nTokenFirst))
                        {
                            bDidSomething = true;
                        }
                    }

                    if (!bLookAhead)
                    {
                        break;
                    }
                }
            }
        }
        while (bDidSomething);
    }

    /// <summary>
    /// returns the set of terminals that are possible to see next given an arbitrary list of tokens
    /// </summary>
    private HashSet<int> First(IEnumerable<int> tokens, int nTerminal)
    {
        var first = new HashSet<int>();
        foreach (var nToken in tokens)
        {
            var bLookAhead = false;
            foreach (var nTokenFirst in _firstSets[nToken])
            {
                if (nTokenFirst == -1)
                {
                    bLookAhead = true;
                }
                else
                {
                    first.Add(nTokenFirst);
                }
            }

            if (!bLookAhead)
            {
                return first;
            }
        }

        first.Add(nTerminal);
        return first;
    }

    /// <summary>
    /// Initializes the propogation table, and initial state of the LALR table
    /// </summary>
    private int InitLALRTables()
    {
        var nLR0State = 0;
        for (var i = 0; i < _lr0States.Count; i++)
        {
            _lalrStates.Add([]);
        }
        foreach (var lr0Kernel in _lr0Kernels)
        {
            var j = new HashSet<int>();
            foreach (var jLR0ItemID in lr0Kernel)
            {
                var lr1Item = new LR1Item(jLR0ItemID, -1);
                var nLR1ItemID = GetLR1ItemID(lr1Item);
                j.Add(nLR1ItemID);
            }
            var jPrime = LR1Closure(j);
            foreach (var jpLR1ItemID in jPrime)
            {
                var lr1Item = _lr1Items[jpLR1ItemID];
                var lr0Item = _lr0Items[lr1Item.LR0ItemID];

                if ((lr1Item.LookAhead != -1) || (nLR0State == 0))
                {
                    _lalrStates[nLR0State].Add(jpLR1ItemID);
                }

                if (lr0Item.Position < _productions[lr0Item.Production].Right.Length)
                {
                    var nToken = _productions[lr0Item.Production].Right[lr0Item.Position];
                    var lr0Successor = new LR0Item(lr0Item.Production, lr0Item.Position + 1);
                    var nLR0Successor = GetLR0ItemID(lr0Successor);
                    var nSuccessorState = _lrGotos[nLR0State][nToken];
                    if (lr1Item.LookAhead == -1)
                    {
                        AddPropogation(nLR0State, lr1Item.LR0ItemID, nSuccessorState, nLR0Successor);
                    }
                    else
                    {
                        var lalrItem = new LR1Item(nLR0Successor, lr1Item.LookAhead);
                        var nLALRItemID = GetLR1ItemID(lalrItem);
                        _lalrStates[nSuccessorState].Add(nLALRItemID);
                    }
                }
            }

            nLR0State++;
        }
        return _lalrStates.Count;
    }

    /// <summary>
    /// Calculates the states in the LALR table
    /// </summary>
    private void CalculateLookAheads()
    {
        bool bChanged;
        do
        {
            bChanged = false;
            var nState = 0;
            foreach (var statePropogations in _lalrPropogations)
            {
                var bStateChanged = false;
                foreach (var nLR1Item in _lalrStates[nState])
                {
                    var lr1Item = _lr1Items[nLR1Item];

                    if (statePropogations.TryGetValue(lr1Item.LR0ItemID, out var propogations))
                    {
                        foreach (var lalrPropogation in propogations)
                        {
                            var nGoto = lalrPropogation.LR0TargetState;
                            var item = new LR1Item(lalrPropogation.LR0TargetItem, lr1Item.LookAhead);
                            if (_lalrStates[nGoto].Add(GetLR1ItemID(item)))
                            {
                                bChanged = true;
                                bStateChanged = true;
                            }
                        }
                    }
                }

                if (bStateChanged)
                {
                    _lalrStates[nState] = LR1Closure(_lalrStates[nState]);
                }
                nState++;
            }
        }
        while (bChanged);
    }

    /// <summary>
    /// Initializes the tokens for the grammar
    /// </summary>
    private void InitSymbols()
    {
        for (var nSymbol = 0; nSymbol < _grammar.SymbolNames.Length; nSymbol++)
        {
            var symbol = nSymbol;
            var isTerminal = _productions.All(production => production.Left != symbol);

            if (isTerminal)
            {
                _terminals.Add(nSymbol);
            }
            else
            {
                _nonterminals.Add(nSymbol);
            }
        }
    }

    /// <summary>
    /// Converts an LR0 State to an LR0 Kernel consisting of only the 'initiating' LR0 Items in the state
    /// </summary>
    private void ConvertLR0ItemsToKernels()
    {
        foreach (var lr0State in _lr0States)
        {
            var lr0Kernel = new HashSet<int>();
            foreach (var nLR0Item in lr0State)
            {
                var item = _lr0Items[nLR0Item];
                if (item.Position != 0)
                {
                    lr0Kernel.Add(nLR0Item);
                }
                else if (_productions[item.Production].Left == 0)
                {
                    lr0Kernel.Add(nLR0Item);
                }
            }
            _lr0Kernels.Add(lr0Kernel);
        }
    }

    /// <summary>
    /// Generates the parse table given the lalr states, and grammar
    /// </summary>
    private void GenerateParseTable()
    {
        for (var nStateID = 0; nStateID < _lalrStates.Count; nStateID++)
        {
            var lalrState = _lalrStates[nStateID];

            for (var nToken = -1; nToken < _grammar.SymbolNames.Length; nToken++)
            {
                var actions = new List<Action>();
                if (nToken >= 0 && _lrGotos[nStateID][nToken] >= 0)
                {
                    actions.Add(new Action(ActionType.Shift, _lrGotos[nStateID][nToken]));
                }

                foreach (var nLR1ItemID in lalrState)
                {
                    var lr1Item = _lr1Items[nLR1ItemID];
                    var lr0Item = _lr0Items[lr1Item.LR0ItemID];

                    if ((lr0Item.Position == _productions[lr0Item.Production].Right.Length) && lr1Item.LookAhead == nToken)
                    {
                        var action = new Action(ActionType.Reduce, lr0Item.Production);
                        if (!actions.Contains(action))
                        {
                            actions.Add(action);
                        }
                    }
                }

                var nMaxPrecedence = int.MinValue;
                var importantActions = new List<Action>();
                foreach (var action in actions)
                {
                    var nActionPrecedence = int.MinValue;
                    if (action.ActionType == ActionType.Shift)
                    {
                        nActionPrecedence = _gotoPrecedence[nStateID][nToken]; //nToken will never be -1
                    }
                    else if (action.ActionType == ActionType.Reduce)
                    {
                        nActionPrecedence = _productionPrecedence[action.ActionParameter];
                    }

                    if (nActionPrecedence > nMaxPrecedence)
                    {
                        nMaxPrecedence = nActionPrecedence;
                        importantActions.Clear();
                        importantActions.Add(action);
                    }
                    else if (nActionPrecedence == nMaxPrecedence)
                    {
                        importantActions.Add(action);
                    }
                }

                if (importantActions.Count == 1)
                {
                    _parseTable.Actions[nStateID, nToken + 1] = importantActions[0];
                }
                else if (importantActions.Count > 1)
                {
                    Action? shiftAction = null;
                    var reduceActions = new List<Action>();
                    foreach (var action in importantActions)
                    {
                        if (action.ActionType == ActionType.Reduce)
                        {
                            reduceActions.Add(action);
                        }
                        else
                        {
                            shiftAction = action;
                        }
                    }

                    var derivation = _grammar.PrecedenceGroups[-nMaxPrecedence].Derivation;
                    if (derivation == Derivation.LeftMost && reduceActions.Count == 1)
                    {
                        _parseTable.Actions[nStateID, nToken + 1] = reduceActions[0];
                    }
                    else if (derivation == Derivation.RightMost && shiftAction.HasValue)
                    {
                        _parseTable.Actions[nStateID, nToken + 1] = shiftAction.Value;
                    }
                    else
                    {
                        var isShiftReduce = shiftAction.HasValue;
                        var errorType = isShiftReduce ? ActionType.ErrorSR : ActionType.ErrorRR;
                        _parseTable.Actions[nStateID, nToken + 1] = new Action(errorType, nToken);

                        var reduceIds = new int[reduceActions.Count];
                        for (var r = 0; r < reduceActions.Count; r++)
                        {
                            reduceIds[r] = reduceActions[r].ActionParameter;
                        }
                        _conflicts.Add(new GrammarConflict(
                            state: nStateID,
                            lookaheadSymbolId: nToken,
                            kind: isShiftReduce ? ConflictKind.ShiftReduce : ConflictKind.ReduceReduce,
                            shiftTargetState: shiftAction?.ActionParameter ?? -1,
                            reduceProductionIds: reduceIds));
                    }
                }
                else
                {
                    _parseTable.Actions[nStateID, nToken + 1] = new Action(ActionType.Error, nToken);
                }
            }
        }
    }

    /// <summary>
    /// helper function
    /// </summary>
    private void PopulateProductions()
    {
        var nPrecedence = 0;
        foreach (var oGroup in _grammar.PrecedenceGroups)
        {
            foreach (var oProduction in oGroup.Productions)
            {
                _productions.Add(oProduction);
                _productionPrecedence.Add(nPrecedence);
                _productionDerivation.Add(oGroup.Derivation);
            }
            nPrecedence--;
        }
    }
}
