using System.Threading.Tasks;
using LALR.CC;
using LALR.CC.LexicalGrammar;
using Xunit;

namespace LALR.CC.Tests;

/// <summary>
/// Regression for the "CurlySuffix" pattern — a nonterminal optionally followed by a brace
/// suffix (<c>C -> T | T '{' '}'</c>) that ALSO appears followed by a brace-block in another
/// context (<c>Stmt -> 'f' T B</c>, <c>B -> '{' '}'</c>). This is the shape of a Zig typed
/// struct literal (<c>Point{ … }</c>, Zig's <c>CurlySuffixExpr &lt;- TypeExpr InitList?</c>)
/// living next to a function body (<c>fn f() Point { … }</c>). LALR(1) handles it: the value
/// "after T" state shifts the suffix <c>'{'</c> while the fn "after T" state shifts the block
/// <c>'{'</c> — distinct states, no conflict. The grammar mirrors dotcc's real shape: the
/// operand reaches the type through the unit chain <c>CurlySuffix -> Type -> ErrUnion ->
/// Suffix -> Primary</c>, with type-prefix <c>*T</c>, error-union <c>S '!' T</c>, postfix
/// <c>S '.' i</c>, an array bound (<c>Type -> '[' P ']' Type</c> — Type↔operand recursion),
/// a binary level, and Zig-like mixed precedence groups (leftmost/rightmost/none).
/// </summary>
public class CurlySuffixReproTests
{
    //  0 Start 1 Stmts 2 Stmt 3 P 4 Mul 5 Prefix 6 CurlySuffix 7 Type 8 ErrUnion 9 Suffix
    //  10 Primary 11 B   12 'f' 13 'i' 14 '{' 15 '}' 16 ';' 17 '*' 18 '.' 19 '!' 20 '[' 21 ']'
    private static Grammar G() => new(
        ["Start","Stmts","Stmt","P","Mul","Prefix","CurlySuffix","Type","ErrUnion","Suffix",
         "Primary","B","f","i","{","}",";","*",".","!","[","]"],
        new PrecedenceGroup(Derivation.None,
            new Production(0, 1),              // Start -> Stmts
            new Production(1, 2, 1),           // Stmts -> Stmt Stmts
            new Production(1, 2),              // Stmts -> Stmt
            new Production(2, 3, 16),          // Stmt  -> P ';'
            new Production(2, 12, 7, 11),      // Stmt  -> 'f' Type B
            new Production(3, 4),              // P -> Mul
            new Production(11, 14, 15)),       // B -> '{' '}'
        new PrecedenceGroup(Derivation.LeftMost,
            new Production(4, 4, 17, 5),       // Mul -> Mul '*' Prefix
            new Production(4, 5)),             // Mul -> Prefix
        new PrecedenceGroup(Derivation.RightMost,
            new Production(5, 6)),             // Prefix -> CurlySuffix
        new PrecedenceGroup(Derivation.None,
            new Production(6, 7),              // CurlySuffix -> Type
            new Production(6, 7, 14, 15)),     // CurlySuffix -> Type '{' '}'
        new PrecedenceGroup(Derivation.RightMost,
            new Production(7, 17, 7),          // Type -> '*' Type
            new Production(7, 20, 3, 21, 7),   // Type -> '[' P ']' Type
            new Production(7, 20, 21, 7),      // Type -> '[' ']' Type
            new Production(7, 8)),             // Type -> ErrUnion
        new PrecedenceGroup(Derivation.None,
            new Production(8, 9, 19, 7),       // ErrUnion -> Suffix '!' Type
            new Production(8, 9)),             // ErrUnion -> Suffix
        new PrecedenceGroup(Derivation.LeftMost,
            new Production(9, 9, 18, 13),      // Suffix -> Suffix '.' 'i'
            new Production(9, 10)),            // Suffix -> Primary
        new PrecedenceGroup(Derivation.None,
            new Production(10, 13)));          // Primary -> 'i'

    private static System.Collections.Generic.Dictionary<string, LexRule[]> Lex() => new()
    {
        { PipeBytesLexer.RootState, [
            new(12, new CharRx('f')),
            new(13, new CharRx('i')),
            new(14, new CharRx('{')),
            new(15, new CharRx('}')),
            new(16, new CharRx(';')),
            new(17, new CharRx('*')),
            new(18, new CharRx('.')),
            new(19, new CharRx('!')),
            new(20, new CharRx('[')),
            new(21, new CharRx(']')),
        ] },
    };

    [Theory]
    [InlineData("i;")]          // value, no brace-suffix
    [InlineData("i{};")]        // value WITH the brace-suffix (the typed-struct-literal shape)
    [InlineData("fi{}")]        // fn return-type followed by a block
    [InlineData("fi{}i{};")]    // both shapes in one input
    public async Task Parses(string input)
    {
        var parser = new Parser(G());   // throws GrammarConflictException here if the pattern conflicts
        using var lexer = PipeBytesLexer.FromString(input, Lex(), cancellationToken: TestContext.Current.CancellationToken);
        using var la = new AsyncLATokenIterator(lexer);
        var r = await parser.ParseInputAsync(la, new Debug(parser, null, null),
            errorMode: ParserErrorMode.Return, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(r.IsError, $"input '{input}' failed to parse: {r}");
    }
}
