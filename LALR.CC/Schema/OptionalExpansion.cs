// Linked into the netstandard2.0 source-generator project (Nullable enabled) as well as the runtime
// library (Nullable disabled), like GrammarSchema.cs: one source for both.
#nullable disable

using System.Collections.Generic;

namespace LALR.CC.Schema;

/// <summary>
/// The optional operator: a right-hand-side symbol written with a trailing <c>?</c> (<c>'pub'?</c>,
/// <c>Label?</c>) may be present or absent. <see cref="Expand"/> rewrites every rule that uses it into
/// the plain productions it stands for, BEFORE anything else reads the schema, so the table builder,
/// the emitters and the runtime compiler only ever see ordinary BNF.
/// </summary>
/// <remarks>
/// <para>
/// <b>Expansion by duplication.</b> A rule with <c>k</c> optional symbols becomes <c>2^k</c> rules, one
/// per subset of them present, all in the rule's own precedence group, with its action, in a fixed order
/// (every optional present first, then each later subset). That is exactly the grammar a hand-written set
/// of twins gives, so the automaton and every conflict resolution are the same as the hand-written form:
/// no helper nonterminal, no epsilon reduce, nothing for the table builder to resolve that it did not
/// resolve before. (Bison-style <c>option(X)</c> helpers add an early epsilon reduce, which is where new
/// conflicts come from.)
/// </para>
/// <para>
/// <b>One record shape.</b> Every variant keeps the action's full arity: each expanded rule carries a
/// <see cref="ProductionSchema.Slots"/> map from argument position to child index (<c>-1</c> for an
/// absent optional), and the parser hands the action its children padded back to that arity, with
/// <c>null</c> where the optional is absent. The generated record has one field per written symbol, and
/// the consumer tests <c>node.ArgN is null</c>.
/// </para>
/// <para>
/// <b>Spelling.</b> A name ending in <c>?</c> is optional when the name is not itself a symbol and the
/// name without the <c>?</c> is one, so a terminal named <c>?</c> stays a terminal and <c>??</c> is that
/// terminal made optional (quoted in YAML: <c>"??"</c>). At most <see cref="MaxOptionals"/> optionals per rule (16 variants).
/// </para>
/// </remarks>
public static class OptionalExpansion
{
    /// <summary>The most optional symbols one rule may carry (it becomes <c>2^MaxOptionals</c> rules).</summary>
    public const int MaxOptionals = 4;

    /// <summary>A rule the expansion refused, with a schema path locator like the validator's.</summary>
    public readonly struct Error
    {
        /// <summary>Creates an error at <paramref name="path"/>.</summary>
        public Error(string path, string message)
        {
            Path = path;
            Message = message;
        }

        /// <summary>The rule's locator, <c>productions[g].rules[r]</c>.</summary>
        public string Path { get; }

        /// <summary>What is wrong with it.</summary>
        public string Message { get; }
    }

    /// <summary>
    /// Expand every optional symbol of <paramref name="schema"/> in place (see the class remarks) and return
    /// the rules it refused, which it leaves as written. A schema with no optional symbol is untouched, and
    /// expanding an expanded schema is a no-op.
    /// </summary>
    public static List<Error> Expand(GrammarSchema schema)
    {
        var errors = new List<Error>();
        if (schema?.Productions == null)
        {
            return errors;
        }
        var symbols = new HashSet<string>(schema.Symbols ?? new List<string>(), System.StringComparer.Ordinal);
        for (var gi = 0; gi < schema.Productions.Count; gi++)
        {
            var group = schema.Productions[gi];
            if (group?.Rules == null)
            {
                continue;
            }
            var expanded = new List<ProductionSchema>(group.Rules.Count);
            var changed = false;
            for (var ri = 0; ri < group.Rules.Count; ri++)
            {
                var rule = group.Rules[ri];
                var optional = OptionalPositions(rule, symbols);
                if (optional.Count == 0)
                {
                    expanded.Add(rule);
                    continue;
                }
                if (optional.Count > MaxOptionals)
                {
                    errors.Add(new Error($"productions[{gi}].rules[{ri}]",
                        $"{optional.Count} optional symbols in one rule; at most {MaxOptionals} (it would become 2^{optional.Count} rules)"));
                    expanded.Add(rule);
                    continue;
                }
                expanded.AddRange(Variants(rule, optional));
                changed = true;
            }
            if (changed)
            {
                group.Rules = expanded;
            }
        }
        return errors;
    }

    /// <summary>The positions of <paramref name="rule"/>'s right-hand side written as optional.</summary>
    private static List<int> OptionalPositions(ProductionSchema rule, HashSet<string> symbols)
    {
        var positions = new List<int>();
        if (rule?.Rhs == null)
        {
            return positions;
        }
        for (var i = 0; i < rule.Rhs.Count; i++)
        {
            if (BaseName(rule.Rhs[i], symbols) != null)
            {
                positions.Add(i);
            }
        }
        return positions;
    }

    /// <summary>The symbol <paramref name="name"/> makes optional, or null when it is not an optional spelling:
    /// it must end in <c>?</c>, not be a symbol itself, and name a symbol without the <c>?</c>.</summary>
    public static string BaseName(string name, ISet<string> symbols)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 2 || name[name.Length - 1] != '?' || symbols.Contains(name))
        {
            return null;
        }
        var baseName = name.Substring(0, name.Length - 1);
        return symbols.Contains(baseName) ? baseName : null;
    }

    /// <summary>One rule per subset of <paramref name="optional"/> present: all present first, then in
    /// descending order of the subset's bit mask (an optional earlier in the rule is the higher bit).</summary>
    private static IEnumerable<ProductionSchema> Variants(ProductionSchema rule, List<int> optional)
    {
        var arity = rule.Rhs.Count;
        var subsets = 1 << optional.Count;
        for (var mask = subsets - 1; mask >= 0; mask--)
        {
            var rhs = new List<string>(arity);
            var slots = new List<int>(arity);
            var next = 0;
            for (var i = 0; i < arity; i++)
            {
                var k = optional.IndexOf(i);
                if (k < 0)
                {
                    rhs.Add(rule.Rhs[i]);
                    slots.Add(next++);
                    continue;
                }
                var present = (mask & (1 << (optional.Count - 1 - k))) != 0;
                if (present)
                {
                    rhs.Add(rule.Rhs[i].Substring(0, rule.Rhs[i].Length - 1));
                    slots.Add(next++);
                }
                else
                {
                    slots.Add(-1);
                }
            }
            yield return new ProductionSchema
            {
                Lhs = rule.Lhs,
                Rhs = rhs,
                Action = rule.Action,
                // The all-present variant needs no map: its children already are the full arity.
                Slots = mask == subsets - 1 ? null : slots,
            };
        }
    }
}
