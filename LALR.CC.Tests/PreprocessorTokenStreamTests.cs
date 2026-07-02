using System;
using System.Collections.Generic;
using LALR.CC;
using LALR.CC.LexicalGrammar;
using LALR.CC.Schema;
using Xunit;

namespace LALR.CC.Tests;

/// <summary>
/// Unit tests for <see cref="PreprocessorTokenStream"/>: directive dispatch,
/// same-line arg collection, rewrite hook, and the schema validation pass
/// added to <see cref="SchemaCompiler"/>. These cover the framework's half of
/// the preprocessor pipeline — end-to-end #include/#define behaviour lives in
/// the CMinus example.
/// </summary>
public class PreprocessorTokenStreamTests
{
    private const int IdSym = 1;
    private const int NumSym = 2;
    private const int DirectiveSym = 3;
    private const int InjectedSym = 4;
    private const int IfSym = 10;
    private const int IfDefSym = 11;
    private const int IfNDefSym = 12;
    private const int ElseSym = 13;
    private const int EndIfSym = 14;
    private const int ElifDefSym = 15;
    private const int ElifNDefSym = 16;

    private static Item Tok(int id, string content, int line) =>
        new(id, content, new SourcePosition(line, 1, 0));

    private sealed class ListIterator : ISyncIterator<Item>
    {
        private readonly List<Item> _items;
        private int _index = -1;
        public ListIterator(IEnumerable<Item> items) { _items = new List<Item>(items); }
        public Item Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;
        public bool MoveNext() => ++_index < _items.Count;
        public void Reset() => _index = -1;
        public bool SupportsResetting => true;
        public void Dispose() { }
    }

    /// <summary>
    /// Empty directive table + null rewrite = pure passthrough. Every token in
    /// the inner stream is emitted unchanged, in order.
    /// </summary>
    [Fact]
    public void EmptyDirectives_NoRewrite_PassesThrough()
    {
        var inner = new ListIterator([
            Tok(IdSym, "a", 1),
            Tok(NumSym, "1", 1),
            Tok(IdSym, "b", 2),
        ]);
        using var pp = new PreprocessorTokenStream(
            inner,
            new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>());
        var seen = Drain(pp);
        Assert.Equal(3, seen.Count);
        Assert.Equal("a", seen[0].Content);
        Assert.Equal("1", seen[1].Content);
        Assert.Equal("b", seen[2].Content);
    }

    /// <summary>
    /// Directive handler runs on the matched token; same-line args are
    /// collected; next-line tokens stay outside the directive's scope.
    /// Handler's returned tokens replace the directive line in the output.
    /// </summary>
    [Fact]
    public void DirectiveDispatch_CollectsSameLineArgs_InjectsReplacement()
    {
        var inner = new ListIterator([
            Tok(DirectiveSym, "#include", 1),
            Tok(IdSym, "foo", 1),       // same line as directive → arg
            Tok(NumSym, "42", 1),       // same line → arg
            Tok(IdSym, "x", 2),         // next line → stays in stream
        ]);
        IReadOnlyList<Item> capturedArgs = null;
        var directives = new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>
        {
            [DirectiveSym] = args =>
            {
                capturedArgs = args;
                return new[] { new Item(InjectedSym, "INJECTED") };
            },
        };
        using var pp = new PreprocessorTokenStream(inner, directives);
        var seen = Drain(pp);
        // Args: ["foo", "42"]
        Assert.NotNull(capturedArgs);
        Assert.Equal(2, capturedArgs.Count);
        Assert.Equal("foo", capturedArgs[0].Content);
        Assert.Equal("42", capturedArgs[1].Content);
        // Output: [INJECTED, x]
        Assert.Equal(2, seen.Count);
        Assert.Equal(InjectedSym, seen[0].ID);
        Assert.Equal("INJECTED", seen[0].Content);
        Assert.Equal("x", seen[1].Content);
    }

    /// <summary>
    /// Directive that returns no tokens drops the directive line entirely —
    /// standard cpp <c>#define</c> behaviour. Next-line tokens still flow.
    /// </summary>
    [Fact]
    public void DirectiveReturningEmpty_DropsLine()
    {
        var inner = new ListIterator([
            Tok(DirectiveSym, "#define", 1),
            Tok(IdSym, "MAX", 1),
            Tok(NumSym, "100", 1),
            Tok(IdSym, "after", 2),
        ]);
        var directives = new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>
        {
            [DirectiveSym] = _ => Array.Empty<Item>(),
        };
        using var pp = new PreprocessorTokenStream(inner, directives);
        var seen = Drain(pp);
        Assert.Single(seen);
        Assert.Equal("after", seen[0].Content);
    }

    /// <summary>
    /// Rewrite hook substitutes non-directive tokens. Verifies the macro-expansion
    /// path that <see cref="Examples.CMinus"/> uses for object-like #defines.
    /// </summary>
    [Fact]
    public void RewriteHook_ExpandsTokens()
    {
        var inner = new ListIterator([
            Tok(IdSym, "MAX", 1),
            Tok(IdSym, "other", 1),
        ]);
        IEnumerable<Item> Rewrite(Item t) => (string)t.Content == "MAX"
            ? new[] { new Item(NumSym, "100") }
            : new[] { t };
        using var pp = new PreprocessorTokenStream(
            inner,
            new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>(),
            Rewrite);
        var seen = Drain(pp);
        Assert.Equal(2, seen.Count);
        Assert.Equal(NumSym, seen[0].ID);
        Assert.Equal("100", seen[0].Content);
        Assert.Equal("other", seen[1].Content);
    }

    /// <summary>
    /// A directive at EOF (no args after it) still dispatches with an empty
    /// args list. Inner-exhaustion shouldn't crash arg collection.
    /// </summary>
    [Fact]
    public void DirectiveAtEof_DispatchesWithEmptyArgs()
    {
        var inner = new ListIterator([
            Tok(DirectiveSym, "#define", 1),
        ]);
        IReadOnlyList<Item> capturedArgs = null;
        var directives = new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>
        {
            [DirectiveSym] = args => { capturedArgs = args; return Array.Empty<Item>(); },
        };
        using var pp = new PreprocessorTokenStream(inner, directives);
        var seen = Drain(pp);
        Assert.NotNull(capturedArgs);
        Assert.Empty(capturedArgs);
        Assert.Empty(seen);
    }

    [Fact]
    public void SchemaCompiler_RejectsUnknownDirectiveSymbol()
    {
        var schema = MinimalSchema();
        schema.Preprocessor = new PreprocessorSchema
        {
            Directives = { ["#unknown"] = "onUnknown" },
        };
        var ex = Assert.Throws<SchemaCompilationException>(() =>
            SchemaCompiler.Compile(schema));
        Assert.Contains("'#unknown'", ex.Message);
        Assert.Contains("not in symbols[]", ex.Message);
    }

    [Fact]
    public void SchemaCompiler_RejectsEmptyHandlerName()
    {
        var schema = MinimalSchema();
        schema.Symbols.Add("#include");
        schema.Lexer[PipeBytesLexer.RootState].Add(
            new LexRuleSchema { Symbol = "#include", Match = "#include" });
        schema.Preprocessor = new PreprocessorSchema
        {
            Directives = { ["#include"] = "" },
        };
        var ex = Assert.Throws<SchemaCompilationException>(() =>
            SchemaCompiler.Compile(schema));
        Assert.Contains("handler name", ex.Message);
    }

    [Fact]
    public void SchemaCompiler_AcceptsValidPreprocessorBlock()
    {
        var schema = MinimalSchema();
        schema.Symbols.Add("#include");
        schema.Lexer[PipeBytesLexer.RootState].Add(
            new LexRuleSchema { Symbol = "#include", Match = "#include" });
        schema.Preprocessor = new PreprocessorSchema
        {
            Directives = { ["#include"] = "onInclude" },
        };
        // Should not throw.
        var (_, _) = SchemaCompiler.Compile(schema);
    }

    /// <summary>
    /// Smallest schema that passes the rest of SchemaCompiler's checks —
    /// a one-rule grammar so the preprocessor test can layer on top without
    /// fighting unrelated validation errors.
    /// </summary>
    private static GrammarSchema MinimalSchema() => new()
    {
        Symbols = ["S", "i"],
        Productions =
        [
            new ProductionGroupSchema
            {
                Derivation = Derivation.None,
                Rules =
                [
                    new ProductionSchema { Lhs = "S", Rhs = ["i"] },
                ],
            },
        ],
        Lexer = new Dictionary<string, List<LexRuleSchema>>
        {
            [PipeBytesLexer.RootState] =
            [
                new LexRuleSchema { Symbol = "i", Match = "[0-9]+" },
            ],
        },
    };

    private static List<Item> Drain(PreprocessorTokenStream pp)
    {
        var result = new List<Item>();
        while (pp.MoveNext())
        {
            result.Add(pp.Current);
        }
        return result;
    }

    /// <summary>
    /// Build a <see cref="PreprocessorConditionals"/> wired to the test-suite
    /// symbol-id constants and a backing macro set for <c>IsDefined</c>.
    /// </summary>
    private static PreprocessorConditionals Conditionals(HashSet<string> defined) =>
        new(IfSym, IfDefSym, IfNDefSym, ElseSym, EndIfSym, defined.Contains,
            elifDefSymbol: ElifDefSym, elifNDefSymbol: ElifNDefSym);

    [Fact]
    public void Ifdef_WhenDefined_EmitsBranch()
    {
        var inner = new ListIterator([
            Tok(IfDefSym, "#ifdef", 1), Tok(IdSym, "X", 1),
            Tok(IdSym, "inside", 2),
            Tok(EndIfSym, "#endif", 3),
            Tok(IdSym, "outside", 4),
        ]);
        using var pp = new PreprocessorTokenStream(
            inner,
            new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>(),
            null,
            Conditionals(["X"]));
        var seen = Drain(pp);
        Assert.Equal(2, seen.Count);
        Assert.Equal("inside", seen[0].Content);
        Assert.Equal("outside", seen[1].Content);
    }

    [Fact]
    public void Ifdef_WhenNotDefined_SuppressesBranch()
    {
        var inner = new ListIterator([
            Tok(IfDefSym, "#ifdef", 1), Tok(IdSym, "X", 1),
            Tok(IdSym, "inside", 2),
            Tok(EndIfSym, "#endif", 3),
            Tok(IdSym, "outside", 4),
        ]);
        using var pp = new PreprocessorTokenStream(
            inner,
            new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>(),
            null,
            Conditionals([])); // X not defined
        var seen = Drain(pp);
        Assert.Single(seen);
        Assert.Equal("outside", seen[0].Content);
    }

    [Fact]
    public void HeaderGuardPattern_SecondInclude_SkipsBody()
    {
        // Simulates two passes of: #ifndef GUARD / #define GUARD / body / #endif
        // First pass: GUARD undefined → emit body + define directive
        // Second pass: GUARD now defined → skip everything until #endif
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var directives = new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>
        {
            [DirectiveSym] = args =>
            {
                if (args.Count > 0 && args[0].Content is string name) { defined.Add(name); }
                return Array.Empty<Item>();
            },
        };

        // First pass — GUARD undefined.
        var firstPass = new ListIterator([
            Tok(IfNDefSym, "#ifndef", 1), Tok(IdSym, "GUARD", 1),
            Tok(DirectiveSym, "#define", 2), Tok(IdSym, "GUARD", 2),
            Tok(IdSym, "body_token", 3),
            Tok(EndIfSym, "#endif", 4),
        ]);
        using var pp1 = new PreprocessorTokenStream(firstPass, directives, null, Conditionals(defined));
        var seen1 = Drain(pp1);
        Assert.Single(seen1);
        Assert.Equal("body_token", seen1[0].Content);
        Assert.Contains("GUARD", defined);

        // Second pass — GUARD now defined → body should be suppressed.
        var secondPass = new ListIterator([
            Tok(IfNDefSym, "#ifndef", 1), Tok(IdSym, "GUARD", 1),
            Tok(DirectiveSym, "#define", 2), Tok(IdSym, "GUARD", 2),
            Tok(IdSym, "body_token", 3),
            Tok(EndIfSym, "#endif", 4),
        ]);
        using var pp2 = new PreprocessorTokenStream(secondPass, directives, null, Conditionals(defined));
        var seen2 = Drain(pp2);
        Assert.Empty(seen2);
    }

    [Fact]
    public void NestedIfdef_TracksDepth()
    {
        // #ifdef A (true)
        //   token_a
        //   #ifdef B (false)
        //     token_b   ← suppressed
        //   #endif
        //   token_a2    ← emitted (A still true)
        // #endif
        var inner = new ListIterator([
            Tok(IfDefSym, "#ifdef", 1), Tok(IdSym, "A", 1),
            Tok(IdSym, "token_a", 2),
            Tok(IfDefSym, "#ifdef", 3), Tok(IdSym, "B", 3),
            Tok(IdSym, "token_b", 4),
            Tok(EndIfSym, "#endif", 5),
            Tok(IdSym, "token_a2", 6),
            Tok(EndIfSym, "#endif", 7),
        ]);
        using var pp = new PreprocessorTokenStream(
            inner,
            new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>(),
            null,
            Conditionals(["A"]));
        var seen = Drain(pp);
        Assert.Equal(2, seen.Count);
        Assert.Equal("token_a", seen[0].Content);
        Assert.Equal("token_a2", seen[1].Content);
    }

    [Fact]
    public void Else_FlipsCurrentBranch()
    {
        // #ifdef X (false)
        //   a        ← suppressed
        // #else
        //   b        ← emitted
        // #endif
        var inner = new ListIterator([
            Tok(IfDefSym, "#ifdef", 1), Tok(IdSym, "X", 1),
            Tok(IdSym, "a", 2),
            Tok(ElseSym, "#else", 3),
            Tok(IdSym, "b", 4),
            Tok(EndIfSym, "#endif", 5),
        ]);
        using var pp = new PreprocessorTokenStream(
            inner,
            new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>(),
            null,
            Conditionals([])); // X not defined → first branch false
        var seen = Drain(pp);
        Assert.Single(seen);
        Assert.Equal("b", seen[0].Content);
    }

    [Fact]
    public void NestedConditionalsInsideFalseBranch_DontResurrect()
    {
        // #ifdef X (false; whole outer suppressed)
        //   #ifdef Y (false in suppression; depth tracking only)
        //     a
        //   #else      ← flips Y's branch in the stack, but X is still false
        //     b
        //   #endif
        // #endif
        // → nothing emitted; both a and b are inside the outer false branch
        var inner = new ListIterator([
            Tok(IfDefSym, "#ifdef", 1), Tok(IdSym, "X", 1),
            Tok(IfDefSym, "#ifdef", 2), Tok(IdSym, "Y", 2),
            Tok(IdSym, "a", 3),
            Tok(ElseSym, "#else", 4),
            Tok(IdSym, "b", 5),
            Tok(EndIfSym, "#endif", 6),
            Tok(EndIfSym, "#endif", 7),
        ]);
        using var pp = new PreprocessorTokenStream(
            inner,
            new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>(),
            null,
            Conditionals(["Y"])); // X NOT defined; Y is — but X gate is outer
        var seen = Drain(pp);
        Assert.Empty(seen);
    }

    [Fact]
    public void IfLiteral_OneIsTrue_ZeroIsFalse()
    {
        // #if 1 / a / #endif → emits a
        // #if 0 / b / #endif → suppresses b
        var inner = new ListIterator([
            Tok(IfSym, "#if", 1), Tok(NumSym, "1", 1),
            Tok(IdSym, "a", 2),
            Tok(EndIfSym, "#endif", 3),
            Tok(IfSym, "#if", 4), Tok(NumSym, "0", 4),
            Tok(IdSym, "b", 5),
            Tok(EndIfSym, "#endif", 6),
            Tok(IdSym, "c", 7),
        ]);
        using var pp = new PreprocessorTokenStream(
            inner,
            new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>(),
            null,
            Conditionals([]));
        var seen = Drain(pp);
        Assert.Equal(2, seen.Count);
        Assert.Equal("a", seen[0].Content);
        Assert.Equal("c", seen[1].Content);
    }

    [Fact]
    public void Elifdef_SelectsArm_WhenDefinedAndNoPriorArmEmitted()
    {
        // #ifdef X (false) / a / #elifdef Y (defined) / b / #else / c / #endif
        // → the elifdef arm wins; the else is locked off.
        var inner = new ListIterator([
            Tok(IfDefSym, "#ifdef", 1), Tok(IdSym, "X", 1),
            Tok(IdSym, "a", 2),
            Tok(ElifDefSym, "#elifdef", 3), Tok(IdSym, "Y", 3),
            Tok(IdSym, "b", 4),
            Tok(ElseSym, "#else", 5),
            Tok(IdSym, "c", 6),
            Tok(EndIfSym, "#endif", 7),
        ]);
        using var pp = new PreprocessorTokenStream(
            inner,
            new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>(),
            null,
            Conditionals(["Y"]));
        var seen = Drain(pp);
        Assert.Single(seen);
        Assert.Equal("b", seen[0].Content);
    }

    [Fact]
    public void Elifndef_SelectsArm_WhenNotDefined()
    {
        // #ifdef X (false) / a / #elifndef Z (Z undefined → true) / b / #endif
        var inner = new ListIterator([
            Tok(IfDefSym, "#ifdef", 1), Tok(IdSym, "X", 1),
            Tok(IdSym, "a", 2),
            Tok(ElifNDefSym, "#elifndef", 3), Tok(IdSym, "Z", 3),
            Tok(IdSym, "b", 4),
            Tok(EndIfSym, "#endif", 5),
        ]);
        using var pp = new PreprocessorTokenStream(
            inner,
            new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>(),
            null,
            Conditionals([]));
        var seen = Drain(pp);
        Assert.Single(seen);
        Assert.Equal("b", seen[0].Content);
    }

    [Fact]
    public void Elifdef_LockedOut_WhenPriorArmEmitted()
    {
        // #ifdef X (true) / a / #elifdef Y (defined, but the chain already
        // emitted) / b / #endif → only a; the arm-selection lock matches #elif.
        var inner = new ListIterator([
            Tok(IfDefSym, "#ifdef", 1), Tok(IdSym, "X", 1),
            Tok(IdSym, "a", 2),
            Tok(ElifDefSym, "#elifdef", 3), Tok(IdSym, "Y", 3),
            Tok(IdSym, "b", 4),
            Tok(EndIfSym, "#endif", 5),
        ]);
        using var pp = new PreprocessorTokenStream(
            inner,
            new Dictionary<int, Func<IReadOnlyList<Item>, IEnumerable<Item>>>(),
            null,
            Conditionals(["X", "Y"]));
        var seen = Drain(pp);
        Assert.Single(seen);
        Assert.Equal("a", seen[0].Content);
    }
}
