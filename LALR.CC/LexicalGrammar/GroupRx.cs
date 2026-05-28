using System;
using System.Collections.Generic;
using System.Linq;

namespace LALR.CC.LexicalGrammar;

public readonly struct GroupRx : IRx
{
    private readonly IRx[] _items;
    private readonly Multiplicity _multiplicity;

    public GroupRx(Multiplicity multiplicity, params IRx[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Length == 0)
        {
            throw new ArgumentException("Items should not be empty!", nameof(items));
        }

        _items = items;
        _multiplicity = multiplicity;
    }

    internal IReadOnlyList<IRx> Items => _items;
    internal Multiplicity Multiplicity => _multiplicity;

    public string Pattern => _items.Length == 1 && (_items[0] is ISingleCharRx || _multiplicity == Multiplicity.Once)
        ? Render(_items[0]) + _multiplicity
        : $"({string.Concat(_items.Select(Render))}){_multiplicity.Pattern}";

    /// <summary>
    /// Render a child as it should appear inside this group's pattern
    /// string. Alternation has lower precedence than concatenation, so
    /// an <see cref="AlternationRx"/> child needs explicit parentheses
    /// when it sits among concat siblings — otherwise round-tripping
    /// <c>Parse(rx.Pattern)</c> would re-bind the <c>|</c> at the outer
    /// scope (`a + (b|c) + d` would format as `ab|cd`, then parse as
    /// alternation of `ab` vs `cd`).
    /// </summary>
    private static string Render(IRx child)
        => child is AlternationRx alt ? $"({alt.Pattern})" : child.Pattern;

    public override string ToString() => Pattern;
}
