using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LALR.CC.Schema;

namespace LALR.CC.SourceGenerators;

/// <summary>
/// Phase 5 / slice 6: emits <c>&lt;Name&gt;.Lexer.g.cs</c> with a pre-baked
/// lexer table — one <see cref="LexicalGrammar.LexRule"/> array per state plus
/// a <c>BuildLexer()</c> factory wrapping them in the
/// <c>Dictionary&lt;string, LexRule[]&gt;</c> shape <c>PipeBytesLexer</c>
/// consumes. Mirrors what the runtime <c>SchemaCompiler.BuildLexer</c> +
/// <see cref="IRxParser"/> would have produced, but at compile time — so
/// the runtime regex parser + DFA-builder + UTF-8 lowering pass are all
/// trimmable from the consumer's AOT image.
///
/// Bad regex syntax in a <c>match:</c> string surfaces as <c>LALR0005</c>
/// instead of a runtime <c>SchemaCompilationException</c>; the result struct
/// returns the per-rule errors so <see cref="GrammarSourceGenerator"/> can
/// turn them into properly-located diagnostics.
/// </summary>
internal static class LexerEmitter
{
    private const string LexRuleFqn = "global::LALR.CC.LexicalGrammar.LexRule";
    private const string PipeBytesLexerFqn = "global::LALR.CC.LexicalGrammar.PipeBytesLexer";
    private const string ColumnModeFqn = "global::LALR.CC.LexicalGrammar.ColumnMode";

    public static EmitResult Emit(GrammarSchema schema, string namespaceName, string className)
    {
        if (schema?.Symbols is null || schema.Symbols.Count == 0 ||
            schema.Lexer is null || schema.Lexer.Count == 0)
        {
            // Same posture as TablesEmitter: structural problems are LALR0003 territory,
            // not LALR0005. We just decline to emit and let the existing diagnostics
            // surface.
            return EmitResult.Skipped();
        }

        // Build a symbol-name → id map matching how SchemaCompiler walks symbols.
        var symbolIds = new Dictionary<string, int>(schema.Symbols.Count);
        for (var i = 0; i < schema.Symbols.Count; i++)
        {
            var name = schema.Symbols[i];
            if (string.IsNullOrEmpty(name) || symbolIds.ContainsKey(name))
            {
                // Same skip rule as above — let LALR0003 own duplicate / empty symbol
                // diagnostics; we'd just blow up on the indexer otherwise.
                return EmitResult.Skipped();
            }
            symbolIds[name] = i;
        }

        var errors = new List<RegexError>();

        // Pre-emit each state's rule expressions so we can bail on regex errors
        // before producing any source. State order is deterministic by walking
        // the dictionary in insertion order (YamlDotNet preserves it for mapping
        // nodes), which matches how SchemaCompiler.BuildLexer iterates.
        var states = new List<(string Name, string[] Rules)>();
        foreach (var entry in schema.Lexer)
        {
            var stateName = entry.Key;
            var rules = entry.Value;
            if (rules is null || rules.Count == 0)
            {
                return EmitResult.Skipped();
            }
            var compiled = new string[rules.Count];
            for (var i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                if (rule is null || string.IsNullOrEmpty(rule.Symbol)
                    || !symbolIds.TryGetValue(rule.Symbol, out var symbolId)
                    || string.IsNullOrEmpty(rule.Match))
                {
                    return EmitResult.Skipped();
                }

                string patternExpr;
                try
                {
                    patternExpr = RegexEmitter.Emit(rule.Match);
                }
                catch (RegexFormatException ex)
                {
                    errors.Add(new RegexError(stateName, i, rule.Match, ex.Message));
                    continue;
                }

                string? lookaheadExpr = null;
                if (!string.IsNullOrEmpty(rule.Lookahead))
                {
                    try
                    {
                        lookaheadExpr = RegexEmitter.Emit(rule.Lookahead);
                    }
                    catch (RegexFormatException ex)
                    {
                        errors.Add(new RegexError(stateName, i, rule.Lookahead, ex.Message));
                        continue;
                    }
                }

                var instruction = ResolveInstruction(rule, schema);
                compiled[i] = "new " + LexRuleFqn + "(" + symbolId.ToString(CultureInfo.InvariantCulture)
                    + ", " + patternExpr + ", " + (instruction is null ? "null" : StringLit(instruction))
                    + (lookaheadExpr is null ? "" : ", " + lookaheadExpr) + ")";
            }
            states.Add((stateName, compiled));
        }

        if (errors.Count > 0)
        {
            return EmitResult.RegexErrors(errors);
        }

        // Default to Codepoints when the schema doesn't specify; that's the
        // diagnostic-friendly choice and matches the lexer ctor's default.
        var columnMode = schema.Columns ?? LALR.CC.LexicalGrammar.ColumnMode.Codepoints;
        var source = Render(states, namespaceName, className, columnMode);
        return EmitResult.Success(source);
    }

    /// <summary>
    /// Resolve the lexer-rule instruction string the same way
    /// <c>SchemaCompiler.BuildLexRule</c> does: <c>push:</c> wins, then
    /// <c>pop:</c> (becomes <c>PipeBytesLexer.PopState</c>), then
    /// <c>action: ignore</c> (becomes <c>PipeBytesLexer.Ignore</c>).
    /// Mutual exclusion is checked at LALR0003 time — here we just take
    /// the first set instruction in priority order.
    /// </summary>
    private static string? ResolveInstruction(LexRuleSchema rule, GrammarSchema schema)
    {
        if (!string.IsNullOrEmpty(rule.Push))
        {
            return rule.Push;
        }
        if (rule.Pop)
        {
            return PipeBytesLexerFqn + ".PopState";
        }
        // SchemaCompiler.IgnoreActionKeyword == "ignore" — inlined here because
        // SchemaCompiler.cs isn't linked into the netstandard2.0 generator
        // (it depends on LexicalGrammar.Item via the actions dictionary type).
        if (string.Equals(rule.Action, "ignore", System.StringComparison.Ordinal))
        {
            return PipeBytesLexerFqn + ".Ignore";
        }
        return null;
    }

    private static string Render(List<(string Name, string[] Rules)> states, string namespaceName, string className,
        LALR.CC.LexicalGrammar.ColumnMode columnMode)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by LALR.CC.SourceGenerators (Phase 5 / pre-baked lexer).");
        sb.AppendLine("// The LexRule[] arrays were built at compile time by RegexEmitter walking the");
        sb.AppendLine("// YAML's regex dialect; the runtime IRxParser + DfaCompiler + Utf8DfaLowering");
        sb.AppendLine("// passes are unreachable from a consumer that only calls BuildLexer().");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.Append("namespace ").Append(namespaceName).AppendLine(";");
        sb.AppendLine();
        sb.Append("public static partial class ").Append(className).AppendLine();
        sb.AppendLine("{");

        // Schema's `columns:` value, surfaced as a constant so consumers can
        // pass it to the lexer ctor without re-reading Schema. Default is
        // Codepoints (one codepoint = one column — diagnostic-friendly for
        // non-ASCII grammars). Set `columns: bytes` in the YAML to opt into
        // UTF-8 byte counting instead.
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Column-counting mode declared in the YAML schema (or the default,");
        sb.AppendLine("    /// <c>Codepoints</c>, when the schema doesn't specify). Pass to the");
        sb.AppendLine("    /// lexer constructor: <c>BytesLexer.FromString(input, BuildLexer(),");
        sb.AppendLine("    /// columnMode: LexerColumnMode)</c>.");
        sb.AppendLine("    /// </summary>");
        sb.Append("    public const ").Append(ColumnModeFqn).Append(" LexerColumnMode = ")
          .Append(ColumnModeFqn).Append('.').Append(columnMode).AppendLine(";");
        sb.AppendLine();

        // One static readonly LexRule[] field per state. Defining them as fields
        // (instead of inline literals inside BuildLexer) means BuildLexer can be
        // called repeatedly without re-allocating the rule arrays — only the
        // wrapping Dictionary is fresh.
        for (var si = 0; si < states.Count; si++)
        {
            var state = states[si];
            sb.Append("    private static readonly ").Append(LexRuleFqn).Append("[] _lex_")
              .Append(SafeIdent(state.Name)).AppendLine(" = new[]");
            sb.AppendLine("    {");
            for (var ri = 0; ri < state.Rules.Length; ri++)
            {
                sb.Append("        ").Append(state.Rules[ri]);
                sb.AppendLine(ri == state.Rules.Length - 1 ? "" : ",");
            }
            sb.AppendLine("    };");
            sb.AppendLine();
        }

        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Pre-baked lexer table — one <c>LexRule[]</c> per state, keyed by state name.");
        sb.AppendLine("    /// Equivalent to what <c>SchemaCompiler.Compile(Schema).Lexer</c> would build at");
        sb.AppendLine("    /// runtime, but the regex parsing + IRx tree construction happened at compile");
        sb.AppendLine("    /// time so <see cref=\"global::LALR.CC.Schema.IRxParser\"/> and");
        sb.AppendLine("    /// the schema-compiler's lexer pass are trimmable from the consumer's AOT image.");
        sb.AppendLine("    /// </summary>");
        sb.Append("    public static Dictionary<string, ").Append(LexRuleFqn).AppendLine("[]> BuildLexer() =>");
        sb.Append("        new Dictionary<string, ").Append(LexRuleFqn).AppendLine("[]>(StringComparer.Ordinal)");
        sb.AppendLine("        {");
        for (var si = 0; si < states.Count; si++)
        {
            sb.Append("            [\"").Append(EscapeStringContent(states[si].Name))
              .Append("\"] = _lex_").Append(SafeIdent(states[si].Name));
            sb.AppendLine(si == states.Count - 1 ? "," : ",");
        }
        sb.AppendLine("        };");

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Sanitise a YAML state name to a C# identifier suffix. Replaces anything
    /// that isn't a letter / digit / underscore with underscore. Doesn't worry
    /// about leading-digit issues since these are only field name suffixes
    /// (already prefixed with <c>_lex_</c>).
    /// </summary>
    private static string SafeIdent(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'
                ? c : '_');
        }
        return sb.ToString();
    }

    private static string StringLit(string value)
    {
        if (value is null)
        {
            return "null";
        }
        // Some instructions are emitted directly as code (PipeBytesLexer.PopState
        // / .Ignore) — those slip through as literal C# expressions, so we only
        // wrap when the string doesn't look like a member access. Cheap heuristic
        // is fine here; the schema validator already vetted the inputs.
        if (value.Contains("."))
        {
            return value;
        }
        return "\"" + EscapeStringContent(value) + "\"";
    }

    private static string EscapeStringContent(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "";
        }
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// One bad <c>match:</c> regex caught by the build-time parser. Grouped per
    /// rule so a multi-conflict YAML produces multiple distinct LALR0005
    /// diagnostics rather than one collapsed message.
    /// </summary>
    public readonly struct RegexError
    {
        public RegexError(string state, int ruleIndex, string match, string message)
        {
            State = state;
            RuleIndex = ruleIndex;
            Match = match;
            Message = message;
        }
        public string State { get; }
        public int RuleIndex { get; }
        public string Match { get; }
        public string Message { get; }
    }

    public readonly struct EmitResult
    {
        public string? Source { get; }
        public IReadOnlyList<RegexError>? Errors { get; }
        public bool DidEmit { get; }

        public bool HasSource => Source != null;
        public bool HasErrors => Errors != null && Errors.Count > 0;

        private EmitResult(string? source, IReadOnlyList<RegexError>? errors, bool didEmit)
        {
            Source = source;
            Errors = errors;
            DidEmit = didEmit;
        }

        public static EmitResult Success(string source) => new EmitResult(source, null, true);
        public static EmitResult RegexErrors(IReadOnlyList<RegexError> errors) => new EmitResult(null, errors, true);
        /// <summary>
        /// Schema is malformed in a way the LALR0003 path will catch; we don't
        /// want to layer LALR0005 on top of that. Returns "no source, no errors,
        /// didn't try" so the generator just moves on.
        /// </summary>
        public static EmitResult Skipped() => new EmitResult(null, null, false);
    }
}
