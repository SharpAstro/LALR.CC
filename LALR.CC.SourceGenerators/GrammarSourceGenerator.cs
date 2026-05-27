using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using LALR.CC.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using YamlDotNet.Core;

[assembly: InternalsVisibleTo("LALR.CC.Tests")]

namespace LALR.CC.SourceGenerators;

/// <summary>
/// Roslyn incremental source generator that turns <c>*.lalr.yaml</c> files (added
/// to the consumer's csproj as <c>&lt;AdditionalFiles&gt;</c>) into a static partial
/// class exposing a populated <c>GrammarSchema</c>. Consumers feed the schema to
/// <c>SchemaCompiler.Compile</c> at runtime.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class GrammarSourceGenerator : IIncrementalGenerator
{
    public const string YamlFileSuffix = ".lalr.yaml";
    private const string DefaultNamespace = "Generated";

    private static readonly DiagnosticDescriptor InvalidYamlDescriptor = new(
        id: "LALR0001",
        title: "Invalid LALR grammar YAML",
        messageFormat: "{0}: {1}",
        category: "LALR.CC.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// Two productions share the same <c>action</c> name but bind a different number
    /// of RHS symbols. Phase 3 emits one record per action (visitor in slice 2 calls
    /// it via a single dispatch path) — that only works if every occurrence has the
    /// same arity. Reported per-conflict so a multi-clash YAML produces multiple
    /// distinct diagnostics rather than a single "first one wins" message.
    /// </summary>
    private static readonly DiagnosticDescriptor ActionArityConflictDescriptor = new(
        id: "LALR0002",
        title: "Action arity conflict",
        messageFormat: "{0}: {1}",
        category: "LALR.CC.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// Structural error in the loaded schema — unknown symbol references in
    /// productions or lexer rules, missing root state, duplicate symbol names,
    /// mutually-exclusive lexer instructions, etc. Mirrors the runtime
    /// <c>SchemaCompilationException</c> checks; surfacing them at build time
    /// turns "boom on first parse" into a build squiggle with a path locator.
    /// </summary>
    private static readonly DiagnosticDescriptor InvalidSchemaDescriptor = new(
        id: "LALR0003",
        title: "Invalid LALR grammar schema",
        messageFormat: "{0}: {1}: {2}",
        category: "LALR.CC.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// Phase 5 / compiler-compiler: an unresolved shift-reduce or reduce-reduce
    /// conflict landed in the parse table after building it at compile time.
    /// Equivalent to the runtime <c>GrammarConflictException</c> the parser
    /// throws when constructed against a conflicting grammar — surfacing it
    /// here turns "boom on first new Parser(g)" into a Roslyn build error
    /// linked to the YAML file. Resolve by placing the offending productions
    /// in a <c>PrecedenceGroup</c> with <c>derivation: leftmost</c> /
    /// <c>derivation: rightmost</c>, or by restructuring the grammar.
    /// </summary>
    private static readonly DiagnosticDescriptor GrammarConflictDescriptor = new(
        id: "LALR0004",
        title: "Unresolved LALR(1) parse-table conflict",
        messageFormat: "{0}: {1}",
        category: "LALR.CC.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// Phase 5 / slice 6: a <c>match:</c> regex couldn't be parsed by the
    /// build-time regex parser. Mirrors the runtime <see cref="System.FormatException"/>
    /// the lexer-rule compiler throws when it hits the same input — surfacing it
    /// as a Roslyn diagnostic turns "boom on first lex" into a build-time
    /// squiggle with the offending pattern + position.
    /// </summary>
    private static readonly DiagnosticDescriptor InvalidLexerRegexDescriptor = new(
        id: "LALR0005",
        title: "Invalid LALR lexer regex",
        messageFormat: "{0}: lexer.{1}[{2}].match \"{3}\": {4}",
        category: "LALR.CC.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The consumer's RootNamespace (csproj <RootNamespace>) lands as a
        // build_property in AnalyzerConfigOptionsProvider.GlobalOptions.
        var rootNamespaceProvider = context.AnalyzerConfigOptionsProvider
            .Select(static (options, _) =>
                options.GlobalOptions.TryGetValue("build_property.RootNamespace", out var ns) && !string.IsNullOrEmpty(ns)
                    ? ns
                    : DefaultNamespace);

        var grammarFiles = context.AdditionalTextsProvider
            .Where(static text => text.Path.EndsWith(YamlFileSuffix, StringComparison.OrdinalIgnoreCase))
            .Combine(rootNamespaceProvider);

        context.RegisterSourceOutput(grammarFiles, Emit);
    }

    private static void Emit(SourceProductionContext context, (AdditionalText Text, string RootNamespace) input)
    {
        var (additionalText, rootNamespace) = input;
        var path = additionalText.Path;
        var content = additionalText.GetText(context.CancellationToken)?.ToString();

        GrammarSchema? schema;
        try
        {
            schema = YamlGrammarLoader.Load(content);
        }
        catch (YamlException yamlEx)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidYamlDescriptor,
                LocationFor(additionalText, yamlEx),
                Path.GetFileName(path),
                yamlEx.Message));
            return;
        }
        catch (Exception ex)
        {
            // Defence in depth: any other deserialiser-level failure becomes a
            // diagnostic instead of crashing the build.
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidYamlDescriptor,
                Location.None,
                Path.GetFileName(path),
                ex.Message));
            return;
        }

        if (schema == null)
        {
            // Empty file — emit nothing rather than a class with an empty schema.
            return;
        }

        // LALR0003: structural-validation pass before emit. Unknown symbol
        // references / missing root state / duplicate symbols / etc would all
        // throw at runtime in SchemaCompiler.Compile; reporting them here
        // turns those into build-time diagnostics with path locators. We still
        // emit the schema so the user can see what was generated — only the
        // grammar's *meaning* is broken, not the C# code we produce.
        var validationErrors = SchemaValidator.Validate(schema);
        foreach (var err in validationErrors)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidSchemaDescriptor,
                Location.Create(path, default, default),
                Path.GetFileName(path),
                err.Path,
                err.Message));
        }

        var className = DeriveClassName(path);
        var schemaSource = CodeEmitter.Emit(schema, rootNamespace, className);
        context.AddSource(className + ".g.cs", SourceText.From(schemaSource, Encoding.UTF8));

        // Phase 3 / slice 1: emit a record per production with an action name. The
        // visitor + wiring lands in a follow-up slice; for now the records are
        // surfaced so the user can see (and review) the AST shape before we commit
        // to it.
        var astResult = AstEmitter.Emit(schema, rootNamespace, className);
        foreach (var error in astResult.Errors)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ActionArityConflictDescriptor,
                Location.None,
                Path.GetFileName(path),
                error.Message));
        }
        if (astResult.Source != null)
        {
            context.AddSource(className + ".Ast.g.cs", SourceText.From(astResult.Source, Encoding.UTF8));
        }

        // Phase 5 / slice 3: compiler-compiler — run SchemaCompiler +
        // ParserTableBuilder at build time and emit the populated Grammar +
        // ParseTable as C# literals. Only proceed if structural validation
        // (LALR0003) passed: validation errors above mean we can't trust the
        // schema enough to build a parse table from it. Runs *before* the
        // visitor emitter so we know whether to wire BuildParser<T>(visitor)
        // (slice 4) into the visitor file: that overload references the
        // pre-baked Definition / ParseTable, so it'd dangle without them.
        var tablesEmitted = false;
        if (validationErrors.Count == 0)
        {
            var emit = TablesEmitter.Emit(schema, rootNamespace, className);
            if (emit.HasSchemaError)
            {
                // Slipped past SchemaValidator — surface as LALR0003 so the user
                // gets a single consistent diagnostic class for schema problems.
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidSchemaDescriptor,
                    Location.Create(path, default, default),
                    Path.GetFileName(path),
                    "schema",
                    emit.SchemaError));
            }
            else if (emit.HasConflicts)
            {
                foreach (var conflict in emit.Conflicts!)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        GrammarConflictDescriptor,
                        Location.Create(path, default, default),
                        Path.GetFileName(path),
                        FormatConflict(conflict, schema)));
                }
            }
            else if (emit.HasSource)
            {
                context.AddSource(className + ".Tables.g.cs", SourceText.From(emit.Source!, Encoding.UTF8));
                tablesEmitted = true;
            }
        }

        // Phase 3 / slice 2: the visitor interface + actions-dictionary wiring.
        // Only emitted when at least one production carries an action; otherwise
        // we'd ship an empty IVisitor + empty dictionary, which is just noise.
        // Phase 5 / slice 4: when the Tables file was also emitted, the visitor
        // file additionally exposes a pre-baked BuildParser<T>(visitor) factory.
        var visitorSource = VisitorEmitter.Emit(schema, rootNamespace, className, tablesEmitted);
        if (visitorSource != null)
        {
            context.AddSource(className + ".Visitor.g.cs", SourceText.From(visitorSource, Encoding.UTF8));
        }

        // Preprocessor surface: emit IPreprocessor + BuildPreprocessor +
        // WrapPreprocessor when the YAML declares a preprocessor: block.
        // Skipped silently when absent (the default) — no consumer code
        // depends on the file existing, only consumers that explicitly
        // implement IPreprocessor reference it. Suppressed on LALR0003
        // because a malformed schema can leave directive symbol ids
        // unresolved; PreprocessorEmitter already skips unresolved entries
        // but emitting half a file is worse than emitting none.
        if (validationErrors.Count == 0)
        {
            var preprocessorSource = PreprocessorEmitter.Emit(schema, rootNamespace, className);
            if (preprocessorSource != null)
            {
                context.AddSource(className + ".Preprocessor.g.cs", SourceText.From(preprocessorSource, Encoding.UTF8));
            }
        }

        // Phase 5 / slice 6: pre-bake the lexer table. Only attempt when LALR0003
        // structural validation already passed — otherwise schema-level breakage
        // (unknown symbol references in lexer rules, missing root state) would
        // get a confusing pile of LALR0005 noise on top of the real problem.
        if (validationErrors.Count == 0)
        {
            var lexerEmit = LexerEmitter.Emit(schema, rootNamespace, className);
            if (lexerEmit.HasErrors)
            {
                foreach (var err in lexerEmit.Errors!)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        InvalidLexerRegexDescriptor,
                        Location.Create(path, default, default),
                        Path.GetFileName(path),
                        err.State,
                        err.RuleIndex,
                        err.Match,
                        err.Message));
                }
            }
            else if (lexerEmit.HasSource)
            {
                context.AddSource(className + ".Lexer.g.cs", SourceText.From(lexerEmit.Source!, Encoding.UTF8));
            }
            // Skipped (DidEmit == false) means LALR0003 already owns the diagnosis;
            // we just don't emit a Lexer.g.cs in that case.
        }
    }

    /// <summary>
    /// One-line conflict description for the LALR0004 diagnostic message.
    /// Resolves symbol ids back to schema-level names so the message reads
    /// in the same vocabulary the YAML uses.
    /// </summary>
    private static string FormatConflict(GrammarConflict c, GrammarSchema schema)
    {
        string SymName(int id) => id < 0
            ? "<EOF>"
            : (schema.Symbols != null && id < schema.Symbols.Count ? schema.Symbols[id] : $"#{id}");
        var lookahead = SymName(c.LookaheadSymbolId);
        return c.Kind switch
        {
            ConflictKind.ShiftReduce =>
                $"shift-reduce conflict at LALR(1) state {c.State} on lookahead '{lookahead}': shift to state {c.ShiftTargetState} vs. reduce productions [{string.Join(", ", c.ReduceProductionIds)}]",
            ConflictKind.ReduceReduce =>
                $"reduce-reduce conflict at LALR(1) state {c.State} on lookahead '{lookahead}': competing reductions [{string.Join(", ", c.ReduceProductionIds)}]",
            _ => $"unknown conflict kind {c.Kind} at state {c.State}",
        };
    }

    /// <summary>
    /// <c>arithmetic.lalr.yaml</c> → <c>Arithmetic</c>. Strips the suffix and any
    /// directory components, then sanitises the remaining identifier to a valid C#
    /// PascalCase name (digits at the start get an underscore prefix, dashes/dots
    /// become underscores).
    /// </summary>
    internal static string DeriveClassName(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(YamlFileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - YamlFileSuffix.Length);
        }
        var sb = new StringBuilder(name.Length);
        var capitaliseNext = true;
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(capitaliseNext ? char.ToUpperInvariant(ch) : ch);
                capitaliseNext = false;
            }
            else
            {
                // dash, dot, underscore, anything else: split point but no character emitted
                capitaliseNext = true;
            }
        }
        if (sb.Length == 0)
        {
            return "Grammar";
        }
        if (char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }
        return sb.ToString();
    }

    private static Location LocationFor(AdditionalText text, YamlException yamlEx)
    {
        var mark = YamlGrammarLoader.TryGetMark(yamlEx);
        if (mark.Line == 0 && mark.Column == 0)
        {
            return Location.None;
        }
        // YamlException marks are 1-based; Roslyn LinePosition is 0-based.
        var line = mark.Line > 0 ? (int)mark.Line - 1 : 0;
        var column = mark.Column > 0 ? (int)mark.Column - 1 : 0;
        var pos = new LinePosition(line, column);
        var span = new LinePositionSpan(pos, pos);
        return Location.Create(text.Path, default, span);
    }
}
