using System.Collections.Generic;
using LALR.CC.Schema;

namespace LALR.CC.SourceGenerators;

/// <summary>
/// Walks a loaded <see cref="GrammarSchema"/> and surfaces structural errors
/// (unknown symbol references, missing root lexer state, duplicate symbol
/// names, mutually-exclusive lexer instructions, …) so the generator can
/// emit them as Roslyn diagnostics at build time. Mirrors the checks
/// <c>SchemaCompiler.Compile</c> performs at runtime — both code paths come
/// from the same data, but throwing at runtime is a much worse user
/// experience than a build-time squiggle.
/// </summary>
/// <remarks>
/// Deliberately lighter than the runtime compiler: skips IRx-pattern parsing
/// (would require linking the regex AST + parser) and skips LALR table-build
/// conflict detection (would require the whole parser). Both surface as
/// <c>SchemaCompilationException</c> / <c>GrammarConflictException</c> at
/// runtime if they slip through; reasonable follow-ups for a future slice
/// once the linked-source surface gets wider.
/// </remarks>
internal static class SchemaValidator
{
    // Mirrors PipeBytesLexer.RootState / PopState / Ignore — those constants
    // live in the runtime library, which the netstandard2.0 generator can't
    // reference, so we hard-code the well-known strings here.
    private const string RootState = "root";
    private const string PopInstruction = "#pop";
    private const string IgnoreInstruction = "ignore";

    public readonly struct ValidationError
    {
        public ValidationError(string path, string message)
        {
            Path = path;
            Message = message;
        }

        /// <summary>Path locator into the schema (e.g. <c>productions[1].rules[3].rhs[2]</c>).</summary>
        public string Path { get; }

        public string Message { get; }
    }

    public static List<ValidationError> Validate(GrammarSchema schema)
    {
        var errors = new List<ValidationError>();
        if (schema == null)
        {
            return errors;
        }

        var symbolSet = ValidateSymbols(schema, errors);
        var stateSet = ValidateLexerStructure(schema, errors);
        ValidateProductions(schema, symbolSet, errors);
        ValidateLexerRules(schema, symbolSet, stateSet, errors);
        ValidatePreprocessor(schema, symbolSet, errors);

        return errors;
    }

    /// <summary>
    /// Mirror of <c>SchemaCompiler.ValidatePreprocessor</c>: every declared
    /// directive symbol must exist in <see cref="GrammarSchema.Symbols"/>,
    /// every handler name must be non-empty. Surfaced as LALR0003 diagnostics
    /// by the generator so YAML config errors are caught at build time
    /// instead of becoming runtime <c>SchemaCompilationException</c>s.
    /// </summary>
    private static void ValidatePreprocessor(GrammarSchema schema, HashSet<string> symbols, List<ValidationError> errors)
    {
        var pre = schema.Preprocessor;
        if (pre == null)
        {
            return;
        }
        if (pre.Directives != null)
        {
            foreach (var kv in pre.Directives)
            {
                var symbol = kv.Key;
                var handler = kv.Value;
                var path = $"preprocessor.directives['{symbol}']";
                if (string.IsNullOrEmpty(symbol))
                {
                    errors.Add(new ValidationError("preprocessor.directives", "directive symbol is null or empty"));
                    continue;
                }
                if (!symbols.Contains(symbol))
                {
                    errors.Add(new ValidationError(path, $"symbol '{symbol}' is not in symbols[]"));
                }
                if (string.IsNullOrEmpty(handler))
                {
                    errors.Add(new ValidationError(path, "handler name is null or empty"));
                }
            }
        }
        if (pre.Conditionals != null)
        {
            foreach (var kv in pre.Conditionals)
            {
                var role = kv.Key;
                var symbol = kv.Value;
                var path = $"preprocessor.conditionals['{role}']";
                if (string.IsNullOrEmpty(role))
                {
                    errors.Add(new ValidationError("preprocessor.conditionals", "role name is null or empty"));
                    continue;
                }
                if (!IsKnownConditionalRole(role))
                {
                    errors.Add(new ValidationError(path,
                        "unknown role; expected one of if, ifdef, ifndef, else, elif, endif"));
                    continue;
                }
                if (string.IsNullOrEmpty(symbol))
                {
                    errors.Add(new ValidationError(path, "symbol is null or empty"));
                }
                else if (!symbols.Contains(symbol))
                {
                    errors.Add(new ValidationError(path, $"symbol '{symbol}' is not in symbols[]"));
                }
            }
        }
    }

    private static bool IsKnownConditionalRole(string role) => role switch
    {
        "if" or "ifdef" or "ifndef" or "else" or "elif" or "endif" => true,
        _ => false,
    };

    private static HashSet<string> ValidateSymbols(GrammarSchema schema, List<ValidationError> errors)
    {
        var set = new HashSet<string>(System.StringComparer.Ordinal);
        if (schema.Symbols == null || schema.Symbols.Count == 0)
        {
            errors.Add(new ValidationError("symbols", "must be non-empty (index 0 is the start symbol)"));
            return set;
        }
        for (var i = 0; i < schema.Symbols.Count; i++)
        {
            var name = schema.Symbols[i];
            if (string.IsNullOrEmpty(name))
            {
                errors.Add(new ValidationError($"symbols[{i}]", "is null or empty"));
                continue;
            }
            if (!set.Add(name))
            {
                errors.Add(new ValidationError($"symbols[{i}]", $"= '{name}' is a duplicate; symbol names must be unique"));
            }
        }
        return set;
    }

    private static HashSet<string> ValidateLexerStructure(GrammarSchema schema, List<ValidationError> errors)
    {
        var set = new HashSet<string>(System.StringComparer.Ordinal);
        if (schema.Lexer == null || schema.Lexer.Count == 0)
        {
            errors.Add(new ValidationError("lexer", "table is missing"));
            return set;
        }
        foreach (var key in schema.Lexer.Keys)
        {
            set.Add(key);
        }
        if (!set.Contains(RootState))
        {
            errors.Add(new ValidationError("lexer", $"table must contain a '{RootState}' state"));
        }
        return set;
    }

    private static void ValidateProductions(GrammarSchema schema, HashSet<string> symbols, List<ValidationError> errors)
    {
        var groupCount = schema.Productions?.Count ?? 0;
        if (groupCount == 0)
        {
            errors.Add(new ValidationError("productions", "must have at least one group"));
            return;
        }

        // Null-checked above (groupCount == 0 returns early); null-forgiving
        // on indexer access is safe.
        var groups = schema.Productions!;
        for (var gi = 0; gi < groupCount; gi++)
        {
            var group = groups[gi];
            if (group == null)
            {
                errors.Add(new ValidationError($"productions[{gi}]", "is null"));
                continue;
            }
            var rules = group.Rules;
            var ruleCount = rules?.Count ?? 0;
            if (ruleCount == 0)
            {
                errors.Add(new ValidationError($"productions[{gi}].rules", "must be non-empty"));
                continue;
            }
            for (var ri = 0; ri < ruleCount; ri++)
            {
                var rule = rules![ri];
                var path = $"productions[{gi}].rules[{ri}]";
                ValidateProduction(rule, symbols, path, errors);
            }
        }
    }

    private static void ValidateProduction(ProductionSchema rule, HashSet<string> symbols, string path, List<ValidationError> errors)
    {
        if (rule == null)
        {
            errors.Add(new ValidationError(path, "is null"));
            return;
        }
        if (string.IsNullOrEmpty(rule.Lhs))
        {
            errors.Add(new ValidationError($"{path}.lhs", "is missing"));
        }
        else if (!symbols.Contains(rule.Lhs))
        {
            errors.Add(new ValidationError($"{path}.lhs", $"= '{rule.Lhs}' is not in symbols[]"));
        }

        var rhs = rule.Rhs;
        var rhsCount = rhs?.Count ?? 0;
        for (var i = 0; i < rhsCount; i++)
        {
            var name = rhs![i];
            if (string.IsNullOrEmpty(name))
            {
                errors.Add(new ValidationError($"{path}.rhs[{i}]", "is null or empty"));
                continue;
            }
            if (!symbols.Contains(name))
            {
                errors.Add(new ValidationError($"{path}.rhs[{i}]", $"= '{name}' is not in symbols[]"));
            }
        }
    }

    private static void ValidateLexerRules(GrammarSchema schema, HashSet<string> symbols, HashSet<string> states, List<ValidationError> errors)
    {
        if (schema.Lexer == null)
        {
            return;
        }
        foreach (var kv in schema.Lexer)
        {
            var stateName = kv.Key;
            var rules = kv.Value;
            if (rules == null || rules.Count == 0)
            {
                errors.Add(new ValidationError($"lexer.{stateName}", "has no rules"));
                continue;
            }
            for (var i = 0; i < rules.Count; i++)
            {
                ValidateLexRule(rules[i], symbols, states, $"lexer.{stateName}[{i}]", errors);
            }
        }
    }

    private static void ValidateLexRule(LexRuleSchema rule, HashSet<string> symbols, HashSet<string> states, string path, List<ValidationError> errors)
    {
        if (rule == null)
        {
            errors.Add(new ValidationError(path, "is null"));
            return;
        }
        if (string.IsNullOrEmpty(rule.Symbol))
        {
            errors.Add(new ValidationError($"{path}.symbol", "is missing"));
        }
        else if (!symbols.Contains(rule.Symbol))
        {
            errors.Add(new ValidationError($"{path}.symbol", $"= '{rule.Symbol}' is not in symbols[]"));
        }
        if (string.IsNullOrEmpty(rule.Match))
        {
            errors.Add(new ValidationError($"{path}.match", "is missing"));
        }

        // Mutual-exclusion: at most one of (push, pop, action) may be set.
        var instructions = 0;
        if (!string.IsNullOrEmpty(rule.Push))
        {
            instructions++;
        }
        if (rule.Pop)
        {
            instructions++;
        }
        if (!string.IsNullOrEmpty(rule.Action))
        {
            instructions++;
        }
        if (instructions > 1)
        {
            errors.Add(new ValidationError(path, "at most one of `push`, `pop`, `action` may be set"));
        }

        if (!string.IsNullOrEmpty(rule.Push) && !states.Contains(rule.Push))
        {
            errors.Add(new ValidationError($"{path}.push", $"= '{rule.Push}' is not a defined lexer state"));
        }
        if (!string.IsNullOrEmpty(rule.Action)
            && !string.Equals(rule.Action, IgnoreInstruction, System.StringComparison.Ordinal))
        {
            errors.Add(new ValidationError($"{path}.action", $"= '{rule.Action}' is not recognized (only '{IgnoreInstruction}' is supported)"));
        }
    }
}
