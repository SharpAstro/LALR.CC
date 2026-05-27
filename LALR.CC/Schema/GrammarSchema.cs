// File is linked into the netstandard2.0 source-generator project (where
// <Nullable>enable</Nullable>) as well as the runtime library (where Nullable is
// disabled). The schema is a bag of public POCOs — null-state isn't a useful
// invariant here — so we disable nullable locally to keep one source for both.
#nullable disable

using System.Collections.Generic;
using LALR.CC.LexicalGrammar;

namespace LALR.CC.Schema;

/// <summary>
/// Declarative grammar definition shaped to match the YAML format we plan to ship in
/// Phase 2 (source generator) and Phase 3 (typed AST + visitors). For now this is the
/// in-memory shape — callers (or future deserializers) populate it directly and feed
/// it to <see cref="SchemaCompiler.Compile"/> to obtain a runtime <see cref="Grammar"/>
/// and lexer table.
/// </summary>
/// <remarks>
/// Properties use plain <c>set</c> rather than <c>init</c> so System.Text.Json /
/// YamlDotNet / any reflection-based deserializer round-trips cleanly. The schema is
/// consumed once at build/load time, then thrown away — mutation isn't a concern.
/// </remarks>
public sealed class GrammarSchema
{
    /// <summary>
    /// Symbol names. Index == symbol id; index 0 is the start symbol. Both terminals
    /// and non-terminals live in this list — the compiler infers which is which from
    /// productions and lexer rules.
    /// </summary>
    public List<string> Symbols { get; set; } = [];

    /// <summary>
    /// Production groups, ordered tightest-binding first. Each group's
    /// <see cref="Derivation"/> resolves shift-reduce / reduce-reduce ambiguity within
    /// the group.
    /// </summary>
    public List<ProductionGroupSchema> Productions { get; set; } = [];

    /// <summary>
    /// Lexer rules per state. The dictionary key is the state name; the runtime always
    /// starts in <see cref="PipeBytesLexer.RootState"/> ("root") so the table must
    /// contain that key.
    /// </summary>
    public Dictionary<string, List<LexRuleSchema>> Lexer { get; set; } = new();

    /// <summary>
    /// Optional metadata for the source generator (Phase 2). The runtime
    /// <see cref="SchemaCompiler"/> ignores this.
    /// </summary>
    public ActionsSchema Actions { get; set; }

    /// <summary>
    /// How the lexer reports <see cref="SourcePosition.Column"/>. Optional;
    /// when omitted, defaults to <see cref="ColumnMode.Codepoints"/> (one
    /// codepoint contributes 1 to the column — diagnostic-friendly for
    /// non-ASCII input). Set <see cref="ColumnMode.Bytes"/> to count UTF-8
    /// bytes instead.
    /// </summary>
    public ColumnMode? Columns { get; set; }

    /// <summary>
    /// Optional preprocessor declaration. When present, the source generator
    /// emits an <c>IPreprocessor</c> interface (one method per declared
    /// directive) and a <c>WrapPreprocessor</c> helper that wires the user's
    /// implementation into a <see cref="PipeBytesLexer"/>-fed token stream
    /// before parsing. The runtime <see cref="SchemaCompiler"/> doesn't act on
    /// this block — it exists purely to drive codegen and validation.
    /// </summary>
    /// <remarks>
    /// The preprocessor sits between the lexer and the parser as an
    /// <c>ISyncIterator&lt;Item&gt;</c> adapter (or async mirror): each token
    /// whose symbol id is a declared directive is consumed, its same-line
    /// args are buffered, and the user handler returns a list of tokens to
    /// inject in place of the directive line. Non-directive tokens optionally
    /// pass through a <c>Rewrite</c> hook for macro expansion. Nothing about
    /// the grammar changes — the parser sees only the cooked token stream.
    /// </remarks>
    public PreprocessorSchema Preprocessor { get; set; }
}

public sealed class ProductionGroupSchema
{
    public Derivation Derivation { get; set; } = Derivation.None;
    public List<ProductionSchema> Rules { get; set; } = [];
}

public sealed class ProductionSchema
{
    /// <summary>Left-hand-side symbol name. Must appear in <see cref="GrammarSchema.Symbols"/>.</summary>
    public string Lhs { get; set; }

    /// <summary>Right-hand-side symbol names. Empty list means an epsilon production.</summary>
    public List<string> Rhs { get; set; } = [];

    /// <summary>
    /// Name of a semantic action; resolved through the <c>actions</c> dictionary passed
    /// to <see cref="SchemaCompiler.Compile"/>. <see langword="null"/> or empty means
    /// "no rewriter; use the default <see cref="Reduction"/>".
    /// </summary>
    public string Action { get; set; }
}

public sealed class LexRuleSchema
{
    /// <summary>Symbol name to emit on match. Must appear in <see cref="GrammarSchema.Symbols"/>.</summary>
    public string Symbol { get; set; }

    /// <summary>
    /// Pattern to match, in the small regex-like dialect documented on
    /// <see cref="IRxParser.Parse"/>. Quoted-string literals from YAML/JSON come in
    /// here verbatim — escapes are interpreted by the regex parser, not the host.
    /// </summary>
    public string Match { get; set; }

    /// <summary>State name to push on match. Mutually exclusive with <see cref="Pop"/> and <see cref="Action"/>.</summary>
    public string Push { get; set; }

    /// <summary>True if matching this rule should pop one state off the stack.</summary>
    public bool Pop { get; set; }

    /// <summary>
    /// Built-in action keyword. Currently only <c>"ignore"</c> is supported (drops the
    /// matched token entirely). Mutually exclusive with <see cref="Push"/> and <see cref="Pop"/>.
    /// </summary>
    public string Action { get; set; }
}

public sealed class ActionsSchema
{
    /// <summary>Class name the source generator (Phase 2) will use for the actions partial class.</summary>
    public string ClassName { get; set; }
}

/// <summary>
/// Declarative preprocessor configuration. Each entry in <see cref="Directives"/>
/// maps a lexer-emitted directive token symbol (the *key* — must appear in
/// <see cref="GrammarSchema.Symbols"/>) to a handler method name (the *value*)
/// that the source generator turns into an <c>IPreprocessor</c> interface
/// method. Implementations of that interface receive the directive's same-line
/// args as <c>IReadOnlyList&lt;Item&gt;</c> and return the tokens to inject
/// in place of the directive.
/// </summary>
/// <remarks>
/// The directive token must be a single token emitted by the lexer (e.g. a
/// rule like <c>{ symbol: '#include', match: '\#include' }</c>); the
/// preprocessor adapter dispatches on token id, not on parsing of the
/// directive's textual form. Use single-token rules for each directive name
/// you want to recognize.
/// </remarks>
public sealed class PreprocessorSchema
{
    /// <summary>
    /// Directive token symbol → handler method name. The key is the symbol
    /// the lexer emits when the directive is matched (e.g. <c>"#include"</c>).
    /// The value is the C# method name on the generated <c>IPreprocessor</c>
    /// interface (e.g. <c>"onInclude"</c> → emitted as <c>OnInclude</c>);
    /// names follow the same camelCase → PascalCase convention as production
    /// actions.
    /// </summary>
    public Dictionary<string, string> Directives { get; set; } = new();

    /// <summary>
    /// Optional conditional-compilation directive map. Keys are the canonical
    /// role names (<c>if</c>, <c>ifdef</c>, <c>ifndef</c>, <c>else</c>,
    /// <c>endif</c>); values are the lexer-emitted directive token symbols
    /// they bind to (e.g. <c>"#if"</c>, <c>"#ifdef"</c>). When present, the
    /// runtime <c>PreprocessorTokenStream</c> tracks an <c>#if</c>/<c>#endif</c>
    /// stack itself — false branches drop subsequent tokens, nested
    /// conditionals respect depth, <c>#else</c> flips the current branch.
    /// The generated <c>IPreprocessor</c> interface grows a <c>IsDefined</c>
    /// method the engine calls to evaluate <c>#ifdef</c>/<c>#ifndef</c>.
    /// </summary>
    /// <remarks>
    /// Unset roles disable just that operator — e.g. a grammar can declare
    /// <c>ifdef</c>/<c>ifndef</c>/<c>endif</c> without <c>if</c> if it doesn't
    /// want expression-based conditionals. The runtime treats missing roles
    /// as "not a conditional directive" — they fall through to user-handler
    /// dispatch (or pass through unchanged).
    /// </remarks>
    public Dictionary<string, string> Conditionals { get; set; } = new();
}
