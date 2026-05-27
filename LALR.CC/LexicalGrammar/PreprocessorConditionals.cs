using System;

namespace LALR.CC.LexicalGrammar;

/// <summary>
/// Configures the conditional-compilation engine inside
/// <see cref="PreprocessorTokenStream"/>. Built at codegen time by the
/// source generator from the YAML's <c>preprocessor.conditionals</c> block:
/// each role (if / ifdef / ifndef / else / endif) gets its lexer-emitted
/// directive symbol id, and <see cref="IsDefined"/> bridges back to the
/// user's macro table so the engine can evaluate <c>#ifdef NAME</c>.
/// </summary>
/// <remarks>
/// <para>
/// Unbound roles (no symbol mapped in YAML) pass <c>-1</c> — the engine
/// treats those tokens like any other and won't try to manage the
/// conditional stack on them.
/// </para>
/// <para>
/// <see cref="IsDefined"/> being <c>null</c> means "no conditional
/// compilation in this stream" — the engine short-circuits entirely and
/// behaves identically to a stream constructed without this struct.
/// Default-constructed (<c>default(PreprocessorConditionals)</c>) values
/// are exactly that: disabled.
/// </para>
/// </remarks>
public readonly struct PreprocessorConditionals
{
    /// <summary>
    /// Construct an enabled conditional configuration. Symbol ids of <c>-1</c>
    /// mean the corresponding role isn't bound — header guards need at least
    /// <paramref name="ifDefSymbol"/> or <paramref name="ifNDefSymbol"/> plus
    /// <paramref name="endIfSymbol"/>; the <paramref name="elseSymbol"/> and
    /// <paramref name="ifSymbol"/> are optional.
    /// </summary>
    public PreprocessorConditionals(
        int ifSymbol,
        int ifDefSymbol,
        int ifNDefSymbol,
        int elseSymbol,
        int endIfSymbol,
        Func<string, bool> isDefined)
    {
        ArgumentNullException.ThrowIfNull(isDefined);
        IfSymbol = ifSymbol;
        IfDefSymbol = ifDefSymbol;
        IfNDefSymbol = ifNDefSymbol;
        ElseSymbol = elseSymbol;
        EndIfSymbol = endIfSymbol;
        IsDefined = isDefined;
    }

    /// <summary>Lexer symbol id for <c>#if</c>. <c>-1</c> when unbound.</summary>
    public int IfSymbol { get; }

    /// <summary>Lexer symbol id for <c>#ifdef</c>. <c>-1</c> when unbound.</summary>
    public int IfDefSymbol { get; }

    /// <summary>Lexer symbol id for <c>#ifndef</c>. <c>-1</c> when unbound.</summary>
    public int IfNDefSymbol { get; }

    /// <summary>Lexer symbol id for <c>#else</c>. <c>-1</c> when unbound.</summary>
    public int ElseSymbol { get; }

    /// <summary>Lexer symbol id for <c>#endif</c>. <c>-1</c> when unbound.</summary>
    public int EndIfSymbol { get; }

    /// <summary>
    /// Resolves a macro name to whether it's currently defined. Called for
    /// <c>#ifdef NAME</c> and <c>#ifndef NAME</c> evaluation. <c>null</c>
    /// means conditional compilation is disabled for this stream.
    /// </summary>
    public Func<string, bool> IsDefined { get; }

    /// <summary>True when this configuration is wired up — i.e. the engine should run.</summary>
    public bool Enabled => IsDefined != null;
}
