using System;
using System.Collections.Generic;
using System.Globalization;

namespace LALR.CC.LexicalGrammar;

/// <summary>
/// Constant-expression evaluator for <c>#if</c> / <c>#elif</c> arguments.
/// Built as a small recursive-descent parser over the collected directive
/// args, supporting the C preprocessor's standard expression sub-language:
/// integer literals, <c>defined(NAME)</c> / <c>defined NAME</c>, arithmetic,
/// comparison, logical, bitwise, and ternary operators, with parentheses.
/// </summary>
/// <remarks>
/// <para>
/// Object-like macro expansion happens via the optional <c>rewrite</c>
/// callback — typically the host <c>IPreprocessor.Rewrite</c> hook. Each
/// arg token is run through it before parsing so <c>#if VERSION &gt;= 2</c>
/// (where <c>VERSION</c> is a <c>#define</c>'d to <c>2</c>) evaluates as
/// <c>2 &gt;= 2</c> = true. Function-like macros in <c>#if</c> are
/// not yet supported — those are a rare-enough edge case to defer.
/// </para>
/// <para>
/// Identifiers that aren't <c>defined</c> sub-expressions and aren't
/// numbers evaluate to <c>0</c> — matches the C standard rule "any
/// identifier remaining after macro expansion has the value 0".
/// </para>
/// <para>
/// All arithmetic uses <c>long</c>. Integer overflow follows C# semantics
/// (wrap on signed overflow). Division by zero throws — same surface as
/// <c>checked</c> arithmetic; callers see the exception bubble up.
/// </para>
/// </remarks>
public static class PreprocessorExpressionEvaluator
{
    /// <summary>
    /// Evaluate <paramref name="args"/> as a C preprocessor constant
    /// expression. Returns the truthy result (non-zero → true). The
    /// optional <paramref name="rewrite"/> callback is applied to each
    /// token before parsing — pass the host <c>IPreprocessor.Rewrite</c>
    /// hook so object-like macros expand in #if expressions.
    /// </summary>
    public static bool Evaluate(
        IReadOnlyList<Item> args,
        Func<string, bool> isDefined,
        Func<Item, IEnumerable<Item>> rewrite = null)
    {
        if (args == null || args.Count == 0)
        {
            return false;
        }

        // Pre-expand object-like macros. `defined(X)` and `defined X` MUST
        // be evaluated against the original name (real C — defined is
        // checked BEFORE macro expansion). We special-case that here: keep
        // a copy of the original tokens and skip rewrite for the operand
        // of a `defined` directive. Implementation: a single-pass walk
        // that splices in non-defined tokens' rewrites.
        var expanded = new List<Item>();
        for (var i = 0; i < args.Count; i++)
        {
            var t = args[i];
            var content = t.Content as string;
            if (content == "defined")
            {
                // Keep `defined` and its argument (one ID or `( ID )`) verbatim.
                expanded.Add(t);
                i++;
                if (i < args.Count && (args[i].Content as string) == "(")
                {
                    expanded.Add(args[i]); // (
                    i++;
                    if (i < args.Count) { expanded.Add(args[i]); } // NAME
                    i++;
                    if (i < args.Count && (args[i].Content as string) == ")") { expanded.Add(args[i]); }
                }
                else if (i < args.Count)
                {
                    expanded.Add(args[i]); // bare NAME
                }
                continue;
            }
            if (rewrite != null)
            {
                foreach (var et in rewrite(t)) { if (et != null) { expanded.Add(et); } }
            }
            else
            {
                expanded.Add(t);
            }
        }

        var parser = new Parser(expanded, isDefined);
        var value = parser.ParseTernary();
        return value != 0;
    }

    private sealed class Parser
    {
        private readonly IReadOnlyList<Item> _tokens;
        private readonly Func<string, bool> _isDefined;
        private int _pos;

        public Parser(IReadOnlyList<Item> tokens, Func<string, bool> isDefined)
        {
            _tokens = tokens;
            _isDefined = isDefined;
        }

        private string Peek() => _pos < _tokens.Count ? _tokens[_pos].Content as string : null;
        private string Consume()
        {
            var s = _pos < _tokens.Count ? _tokens[_pos].Content as string : null;
            _pos++;
            return s;
        }
        private bool TryConsume(string s)
        {
            if (Peek() == s) { _pos++; return true; }
            return false;
        }

        // Precedence ladder (low → high), matches the C preprocessor:
        //   ternary > || > && > | > ^ > & > == != > < > <= >= > << >> > + -
        //   > * / % > unary (! ~ + -) > primary

        public long ParseTernary()
        {
            var cond = ParseLOr();
            if (TryConsume("?"))
            {
                var thenV = ParseTernary();
                if (!TryConsume(":")) { return cond != 0 ? thenV : 0; }
                var elseV = ParseTernary();
                return cond != 0 ? thenV : elseV;
            }
            return cond;
        }

        private long ParseLOr()
        {
            var l = ParseLAnd();
            while (TryConsume("||"))
            {
                var r = ParseLAnd();
                l = (l != 0 || r != 0) ? 1 : 0;
            }
            return l;
        }

        private long ParseLAnd()
        {
            var l = ParseBOr();
            while (TryConsume("&&"))
            {
                var r = ParseBOr();
                l = (l != 0 && r != 0) ? 1 : 0;
            }
            return l;
        }

        private long ParseBOr()
        {
            var l = ParseBXor();
            while (Peek() == "|" && !LookaheadIs("||"))
            {
                _pos++;
                var r = ParseBXor();
                l = l | r;
            }
            return l;
        }

        private long ParseBXor()
        {
            var l = ParseBAnd();
            while (TryConsume("^"))
            {
                var r = ParseBAnd();
                l = l ^ r;
            }
            return l;
        }

        private long ParseBAnd()
        {
            var l = ParseEqu();
            while (Peek() == "&" && !LookaheadIs("&&"))
            {
                _pos++;
                var r = ParseEqu();
                l = l & r;
            }
            return l;
        }

        private long ParseEqu()
        {
            var l = ParseRel();
            while (true)
            {
                if (TryConsume("==")) { l = l == ParseRel() ? 1 : 0; }
                else if (TryConsume("!=")) { l = l != ParseRel() ? 1 : 0; }
                else { return l; }
            }
        }

        private long ParseRel()
        {
            var l = ParseShift();
            while (true)
            {
                if (TryConsume("<=")) { l = l <= ParseShift() ? 1 : 0; }
                else if (TryConsume(">=")) { l = l >= ParseShift() ? 1 : 0; }
                else if (Peek() == "<" && !LookaheadIs("<<")) { _pos++; l = l < ParseShift() ? 1 : 0; }
                else if (Peek() == ">" && !LookaheadIs(">>")) { _pos++; l = l > ParseShift() ? 1 : 0; }
                else { return l; }
            }
        }

        private long ParseShift()
        {
            var l = ParseAdd();
            while (true)
            {
                if (TryConsume("<<")) { l = l << (int)ParseAdd(); }
                else if (TryConsume(">>")) { l = l >> (int)ParseAdd(); }
                else { return l; }
            }
        }

        private long ParseAdd()
        {
            var l = ParseMul();
            while (true)
            {
                if (TryConsume("+")) { l = l + ParseMul(); }
                else if (TryConsume("-")) { l = l - ParseMul(); }
                else { return l; }
            }
        }

        private long ParseMul()
        {
            var l = ParseUnary();
            while (true)
            {
                if (TryConsume("*")) { l = l * ParseUnary(); }
                else if (TryConsume("/")) { l = l / ParseUnary(); }
                else if (TryConsume("%")) { l = l % ParseUnary(); }
                else { return l; }
            }
        }

        private long ParseUnary()
        {
            if (TryConsume("!")) { return ParseUnary() == 0 ? 1 : 0; }
            if (TryConsume("~")) { return ~ParseUnary(); }
            if (TryConsume("-")) { return -ParseUnary(); }
            if (TryConsume("+")) { return ParseUnary(); }
            return ParsePrimary();
        }

        private long ParsePrimary()
        {
            var t = Peek();
            if (t == null) { return 0; }
            if (t == "(")
            {
                _pos++;
                var v = ParseTernary();
                TryConsume(")");
                return v;
            }
            if (t == "defined")
            {
                _pos++;
                return ParseDefined();
            }
            // Integer literal?
            if (TryParseInt(t, out var intVal))
            {
                _pos++;
                return intVal;
            }
            // Unresolved identifier → 0 (matches the C standard rule).
            _pos++;
            return 0;
        }

        private long ParseDefined()
        {
            string name;
            if (TryConsume("("))
            {
                name = Consume();
                TryConsume(")");
            }
            else
            {
                name = Consume();
            }
            return _isDefined(name) ? 1 : 0;
        }

        private bool LookaheadIs(string twoChar)
        {
            // Hand-rolled tiny lookahead: when `&` appears alone we may need
            // to distinguish from `&&` if the lexer emitted them as one or
            // two tokens. Within the directive arg stream (collected from
            // the byte lexer) `&&` should already be one token; this guard
            // is defense-in-depth.
            return _pos + 1 < _tokens.Count
                && (_tokens[_pos].Content as string) == twoChar.Substring(0, 1)
                && (_tokens[_pos + 1].Content as string) == twoChar.Substring(1, 1);
        }

        private static bool TryParseInt(string s, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) { return false; }
            // Hex: 0x prefix
            if (s.Length > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
            {
                return long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            }
            // Strip trailing C int suffixes (u, U, l, L, ll, LL, ul, UL, ull, ULL).
            var end = s.Length;
            while (end > 0 && (s[end - 1] is 'u' or 'U' or 'l' or 'L')) { end--; }
            if (end == 0) { return false; }
            return long.TryParse(s.AsSpan(0, end), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }
}
