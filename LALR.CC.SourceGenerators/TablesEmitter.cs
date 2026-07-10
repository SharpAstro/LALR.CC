using System.Collections.Generic;
using System.Text;
using LALR.CC;
using LALR.CC.Schema;

namespace LALR.CC.SourceGenerators;

/// <summary>
/// Phase 5 / slice 3: emits <c>&lt;Name&gt;.Tables.g.cs</c> with a populated
/// <see cref="Grammar"/> + <see cref="ParseTable"/> that the source generator
/// computed at build time by running <see cref="SchemaCompiler"/> +
/// <see cref="ParserTableBuilder"/> against the YAML schema.
///
/// Win for consumers: the runtime <see cref="ParserTableBuilder"/> (LR0/LR1
/// closures, LALR propagation, parse-table generation — several KB of code) is
/// trimmable from the AOT image because nothing in their app calls it.
///
/// Conflicts in the grammar surface as <c>LALR0004</c> Roslyn diagnostics at
/// build time instead of <see cref="GrammarConflictException"/> at first
/// <c>new Parser(grammar)</c>.
/// </summary>
internal static class TablesEmitter
{
    /// <summary>
    /// Build the tables and emit the generated source. Returns
    /// <c>(source, conflicts)</c>: <c>source</c> is null when the schema
    /// itself doesn't compile (caller should have already reported LALR0003
    /// for that); <c>conflicts</c> is the list of unresolved S/R + R/R cells
    /// the caller should turn into LALR0004 diagnostics.
    /// </summary>
    public static EmitResult Emit(GrammarSchema schema, string namespaceName, string className)
    {
        // Build the Grammar from the schema. We can't link SchemaCompiler.Compile
        // into the netstandard2.0 generator because it touches LexicalGrammar.Item
        // (via the actions dictionary type) and IRxParser/LexRule (via BuildLexer).
        // We only need the parser side at build time — the lexer half is left to
        // the runtime path. Inline the parser-side schema walk here.
        Grammar grammar;
        try
        {
            grammar = BuildGrammarFromSchema(schema);
        }
        catch (SchemaWalkException ex)
        {
            // The structural validator (SchemaValidator / LALR0003) should have
            // caught most schema problems already; if we still hit one here,
            // something slipped through. Surface as SchemaFailure so the
            // generator-level handler wraps it in an LALR0003 diagnostic
            // and skips emit.
            return EmitResult.SchemaFailure(ex.Message);
        }

        var builder = new ParserTableBuilder(grammar);
        if (builder.Conflicts.Count > 0)
        {
            // Don't emit Tables.g.cs when the grammar has conflicts — the
            // emitted parse-table cells in conflict positions would be Error
            // cells, and the consumer would just get a parse error at runtime.
            // Better to let the build fail with LALR0004 diagnostics so the
            // user fixes the grammar.
            return EmitResult.GrammarConflicts(builder.Conflicts);
        }

        var source = Render(grammar, builder.ParseTable, namespaceName, className);
        return EmitResult.Success(source);
    }

    /// <summary>
    /// Parser-side schema walker. Mirrors <c>SchemaCompiler.BuildGrammar</c>
    /// but without the rewriter-action plumbing (we pass null at compile time
    /// anyway) and without the lexer side. Always uses the bare
    /// <c>Production(int left, params int[] right)</c> ctor — visitor-aware
    /// rewriters wire in at runtime via the existing visitor pipeline.
    /// </summary>
    private static Grammar BuildGrammarFromSchema(GrammarSchema schema)
    {
        if (schema.Symbols is null || schema.Symbols.Count == 0)
        {
            throw new SchemaWalkException("symbols must be non-empty (index 0 is the start symbol)");
        }

        var symbolIds = new Dictionary<string, int>(schema.Symbols.Count);
        var symbolNames = new global::LALR.CC.LexicalGrammar.SymbolName[schema.Symbols.Count];
        for (var i = 0; i < schema.Symbols.Count; i++)
        {
            var name = schema.Symbols[i];
            if (string.IsNullOrEmpty(name))
            {
                throw new SchemaWalkException($"symbols[{i}] is null or empty");
            }
            if (symbolIds.ContainsKey(name))
            {
                throw new SchemaWalkException($"symbols[{i}] = '{name}' is a duplicate");
            }
            symbolIds[name] = i;
            symbolNames[i] = new global::LALR.CC.LexicalGrammar.SymbolName(i, name);
        }

        var groupCount = schema.Productions?.Count ?? 0;
        if (groupCount == 0)
        {
            throw new SchemaWalkException("productions must have at least one group");
        }

        var groups = new PrecedenceGroup[groupCount];
        for (var gi = 0; gi < groupCount; gi++)
        {
            // schema.Productions just had its count bounded above; null-forgive
            // the indexer access so the netstandard2.0 generator's nullable flow
            // doesn't gripe.
            var groupSchema = schema.Productions![gi];
            if (groupSchema is null)
            {
                throw new SchemaWalkException($"productions[{gi}] is null");
            }
            var ruleCount = groupSchema.Rules?.Count ?? 0;
            if (ruleCount == 0)
            {
                throw new SchemaWalkException($"productions[{gi}].rules must be non-empty");
            }
            var rules = new Production[ruleCount];
            for (var ri = 0; ri < ruleCount; ri++)
            {
                var rule = groupSchema.Rules![ri];
                if (rule is null)
                {
                    throw new SchemaWalkException($"productions[{gi}].rules[{ri}] is null");
                }
                if (string.IsNullOrEmpty(rule.Lhs))
                {
                    throw new SchemaWalkException($"productions[{gi}].rules[{ri}].lhs is missing");
                }
                if (!symbolIds.TryGetValue(rule.Lhs, out var lhs))
                {
                    throw new SchemaWalkException($"productions[{gi}].rules[{ri}].lhs = '{rule.Lhs}' is not in symbols[]");
                }
                var rhsCount = rule.Rhs?.Count ?? 0;
                var rhs = new int[rhsCount];
                for (var i = 0; i < rhsCount; i++)
                {
                    var sym = rule.Rhs![i];
                    if (string.IsNullOrEmpty(sym))
                    {
                        throw new SchemaWalkException($"productions[{gi}].rules[{ri}].rhs[{i}] is null or empty");
                    }
                    if (!symbolIds.TryGetValue(sym, out var id))
                    {
                        throw new SchemaWalkException($"productions[{gi}].rules[{ri}].rhs[{i}] = '{sym}' is not in symbols[]");
                    }
                    rhs[i] = id;
                }
                rules[ri] = new Production(lhs, rhs);
            }
            groups[gi] = new PrecedenceGroup(groupSchema.Derivation, rules);
        }

        return new Grammar(symbolNames, groups);
    }

    private static string Render(Grammar grammar, ParseTable parseTable, string namespaceName, string className)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by LALR.CC.SourceGenerators (Phase 5 / pre-baked tables).");
        sb.AppendLine("// The Grammar and ParseTable were computed at build time by SchemaCompiler +");
        sb.AppendLine("// ParserTableBuilder; the consumer's runtime constructs Parser directly from these");
        sb.AppendLine("// literals, so the table-build code can be trimmed from the AOT image.");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using LALR.CC;");
        sb.AppendLine("using LALR.CC.LexicalGrammar;");
        sb.AppendLine();
        sb.Append("namespace ").Append(namespaceName).AppendLine(";");
        sb.AppendLine();
        sb.Append("public static partial class ").Append(className).AppendLine();
        sb.AppendLine("{");

        EmitGrammarDefinition(sb, grammar);
        EmitParseTable(sb, parseTable);
        EmitBuildParser(sb);

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void EmitGrammarDefinition(StringBuilder sb, Grammar grammar)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// The grammar in normalised form: symbols indexed by id, productions");
        sb.AppendLine("    /// flattened into precedence groups. The bare <c>Production(int, params int[])</c>");
        sb.AppendLine("    /// ctor is used here — semantic-action rewriters are wired in at");
        sb.AppendLine("    /// <see cref=\"BuildParser\"/> time when a visitor is supplied.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static readonly global::LALR.CC.Grammar Definition = new global::LALR.CC.Grammar(");

        // Symbols
        sb.Append("        new global::LALR.CC.LexicalGrammar.SymbolName[]");
        sb.Append(" { ");
        for (var i = 0; i < grammar.SymbolNames.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append("new(").Append(grammar.SymbolNames[i].ID).Append(", ").Append(StringLit(grammar.SymbolNames[i].Name)).Append(")");
        }
        sb.AppendLine(" },");

        // Precedence groups
        for (var gi = 0; gi < grammar.PrecedenceGroups.Length; gi++)
        {
            var group = grammar.PrecedenceGroups[gi];
            var productions = new List<Production>();
            foreach (var prod in group.Productions)
            {
                productions.Add(prod);
            }

            sb.Append("        new global::LALR.CC.PrecedenceGroup(global::LALR.CC.Derivation.")
              .Append(group.Derivation).AppendLine(",");
            for (var pi = 0; pi < productions.Count; pi++)
            {
                var prod = productions[pi];
                sb.Append("            new global::LALR.CC.Production(")
                  .Append(prod.Left)
                  .Append(", new int[] { ");
                for (var ri = 0; ri < prod.Right.Length; ri++)
                {
                    if (ri > 0) sb.Append(", ");
                    sb.Append(prod.Right[ri]);
                }
                sb.Append(" })");
                sb.AppendLine(pi == productions.Count - 1 ? ")" + (gi == grammar.PrecedenceGroups.Length - 1 ? ");" : ",") : ",");
            }
        }
        sb.AppendLine();
    }

    private static void EmitParseTable(StringBuilder sb, ParseTable parseTable)
    {
        var states = parseTable.States;
        var tokenCols = parseTable.Tokens;

        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Pre-baked LALR(1) parse table. Computed at build time by");
        sb.AppendLine("    /// <see cref=\"global::LALR.CC.ParserTableBuilder\"/>; the");
        sb.AppendLine("    /// consumer never runs that code, so the trimmer can drop it.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    /// <remarks>");
        sb.AppendLine("    /// The table is encoded as two flat, constant primitive arrays (the action");
        sb.AppendLine("    /// type + its parameter, row-major over states × tokens) and rebuilt into the");
        sb.AppendLine("    /// <c>Action[,]</c> by a small loop. This is deliberate, not incidental: a");
        sb.AppendLine("    /// large <c>Action[,]</c> element-initialiser makes the C# compiler emit one");
        sb.AppendLine("    /// temporary local per cell, and a big grammar's table (thousands of states ×");
        sb.AppendLine("    /// hundreds of tokens = 10^5+ cells) overflows the Mono/WASM interpreter's");
        sb.AppendLine("    /// 16-bit per-method frame — \"locals size too big\", which breaks the parser");
        sb.AppendLine("    /// under Blazor/wasm even though NativeAOT and the desktop CLR cope. A");
        sb.AppendLine("    /// constant primitive-array initialiser instead lowers to a single");
        sb.AppendLine("    /// <c>RuntimeHelpers.InitializeArray</c> over a <c>.data</c> RVA blob (no");
        sb.AppendLine("    /// per-cell locals), so the static-init frame stays tiny on every runtime.");
        sb.AppendLine("    /// </remarks>");
        // NOTE: the two backing arrays MUST be declared before the ParseTable field —
        // C# runs static field initialisers in declaration order, and
        // BuildParseTable() reads them, so ParseTable-first would see them still null.
        sb.Append("    private static readonly byte[] _actionTypes = new byte[] { ");
        for (var s = 0; s < states; s++)
        {
            for (var c = 0; c < tokenCols; c++)
            {
                if (s != 0 || c != 0) sb.Append(", ");
                sb.Append((int)parseTable.Actions[s, c].ActionType);
            }
        }
        sb.AppendLine(" };");

        sb.Append("    private static readonly int[] _actionParams = new int[] { ");
        for (var s = 0; s < states; s++)
        {
            for (var c = 0; c < tokenCols; c++)
            {
                if (s != 0 || c != 0) sb.Append(", ");
                sb.Append(parseTable.Actions[s, c].ActionParameter);
            }
        }
        sb.AppendLine(" };");
        sb.AppendLine();

        sb.AppendLine("    public static readonly global::LALR.CC.ParseTable ParseTable = BuildParseTable();");
        sb.AppendLine();

        sb.AppendLine("    private static global::LALR.CC.ParseTable BuildParseTable()");
        sb.AppendLine("    {");
        sb.Append("        const int states = ").Append(states).AppendLine(";");
        sb.Append("        const int tokens = ").Append(tokenCols).AppendLine(";");
        sb.AppendLine("        var actions = new global::LALR.CC.Action[states, tokens];");
        sb.AppendLine("        for (int s = 0, k = 0; s < states; s++)");
        sb.AppendLine("        {");
        sb.AppendLine("            for (int c = 0; c < tokens; c++, k++)");
        sb.AppendLine("            {");
        sb.AppendLine("                actions[s, c] = new global::LALR.CC.Action((global::LALR.CC.ActionType)_actionTypes[k], _actionParams[k]);");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        return new global::LALR.CC.ParseTable(actions);");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void EmitBuildParser(StringBuilder sb)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Construct a <see cref=\"global::LALR.CC.Parser\"/> against");
        sb.AppendLine("    /// the pre-baked <see cref=\"Definition\"/> + <see cref=\"ParseTable\"/>.");
        sb.AppendLine("    /// Skips <see cref=\"global::LALR.CC.ParserTableBuilder\"/>");
        sb.AppendLine("    /// entirely — the table was built at compile time.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    /// <remarks>");
        sb.AppendLine("    /// This factory is action-free: any production in the YAML that named an");
        sb.AppendLine("    /// <c>action:</c> will fall back to the default <c>Reduction</c> at parse");
        sb.AppendLine("    /// time. To wire a visitor's rewriters in, use the");
        sb.AppendLine("    /// <c>BuildParser&lt;T&gt;(IVisitor&lt;T&gt;)</c> overload (emitted alongside");
        sb.AppendLine("    /// the visitor surface in <c>&lt;Name&gt;.Visitor.g.cs</c>) — same pre-baked");
        sb.AppendLine("    /// <see cref=\"ParseTable\"/>, productions get rewriter delegates spliced in.");
        sb.AppendLine("    /// </remarks>");
        sb.AppendLine("    public static global::LALR.CC.Parser BuildParser() =>");
        sb.AppendLine("        new global::LALR.CC.Parser(Definition, ParseTable);");
    }

    /// <summary>
    /// Produce a C# string literal, including surrounding quotes and full
    /// escaping. Mirrors <c>CodeEmitter.StringLit</c>; we don't share because
    /// pulling Roslyn into TablesEmitter is overkill — the symbols here are
    /// always grammar names (no control chars, no quotes, mostly ASCII).
    /// </summary>
    private static string StringLit(string value)
    {
        if (value is null) return "\"\"";
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20 || ch == 0x7f)
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>
/// Result of attempting to emit pre-baked tables for a grammar schema.
/// Exactly one of <see cref="Source"/>, <see cref="SchemaError"/>, or
/// <see cref="Conflicts"/> is non-null on a returned instance — callers
/// dispatch on the matching <c>Has*</c> flag.
/// </summary>
internal readonly struct EmitResult
{
    public string? Source { get; }
    public string? SchemaError { get; }
    public IReadOnlyList<GrammarConflict>? Conflicts { get; }

    public bool HasSource => Source != null;
    public bool HasSchemaError => SchemaError != null;
    public bool HasConflicts => Conflicts != null && Conflicts.Count > 0;

    private EmitResult(string? source, string? schemaError, IReadOnlyList<GrammarConflict>? conflicts)
    {
        Source = source;
        SchemaError = schemaError;
        Conflicts = conflicts;
    }

    public static EmitResult Success(string source) => new(source, null, null);
    public static EmitResult SchemaFailure(string message) => new(null, message, null);
    public static EmitResult GrammarConflicts(IReadOnlyList<GrammarConflict> conflicts) => new(null, null, conflicts);
}

/// <summary>
/// Internal exception type for the parser-side schema walker in
/// <see cref="TablesEmitter.BuildGrammarFromSchema"/>. Mirrors
/// <c>SchemaCompilationException</c> in shape but lives in the generator
/// project so we don't have to link <c>SchemaCompiler.cs</c> (which has
/// LexicalGrammar dependencies the netstandard2.0 generator can't satisfy).
/// </summary>
internal sealed class SchemaWalkException : System.Exception
{
    public SchemaWalkException(string message) : base(message) { }
}
