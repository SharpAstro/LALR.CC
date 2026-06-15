using System;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace ZigSpike;

/// <summary>
/// Spike harness for the Zig LALR(1) grammar translation (slice 1). Building the
/// project already proves the grammar generates clean LALR(1) tables (a conflict
/// would abort the build with <c>GrammarConflictException</c>). This driver goes
/// further and checks ACCEPTANCE: it runs real Zig snippets through the generated
/// <c>BytesLexer -> Parser.ParseInput</c> pipeline (with the generated
/// <c>IdentityVisitor</c>), and also confirms that constructs the grammar should
/// reject (e.g. the non-associative compare chain <c>a &lt; b &lt; c</c>) do fail.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var accept = new (string Name, string Src)[]
        {
            ("fn + arithmetic", "fn add(a: i32, b: i32) i32 { return a + b * 2; }"),
            ("top-level const/var + pub fn",
                "const Pi = 3.14;\npub fn main() void {\n    const x = Pi + 1.0;\n    return;\n}"),
            ("pointer/optional types, call, field, index, assign",
                "fn f(p: *u8, q: ?T) void {\n" +
                "    const x = p[0];\n" +
                "    const y = foo.bar(p, 3) + @intCast(x);\n" +
                "    var z = a.b.c;\n" +
                "    z = y;\n" +
                "    return;\n}"),
            ("error-union return, if/else, while",
                "fn g(n: i32) !i32 {\n" +
                "    var i = 0;\n" +
                "    while (i < n) {\n" +
                "        if (i == 3) { return i; } else { i = i + 1; }\n" +
                "    }\n" +
                "    return n;\n}"),
            ("deref / optional-unwrap postfix",
                "fn h(p: *T) T { return p.*.field.?; }"),
        };

        var reject = new (string Name, string Src)[]
        {
            ("non-associative compare (a < b < c)", "fn bad(a: i32) i32 { return a < a < a; }"),
            ("stray operator as statement", "fn h() void { + }"),
        };

        int failures = 0;

        Console.WriteLine("== should ACCEPT ==");
        foreach (var (name, src) in accept)
        {
            if (TryParse(src, out var err)) { Console.WriteLine($"  [ok]   {name}"); }
            else { Console.WriteLine($"  [FAIL] {name}: {err}"); failures++; }
        }

        Console.WriteLine("== should REJECT ==");
        foreach (var (name, src) in reject)
        {
            if (!TryParse(src, out var err)) { Console.WriteLine($"  [ok]   {name} (rejected: {err})"); }
            else { Console.WriteLine($"  [FAIL] {name}: parsed but should have been rejected"); failures++; }
        }

        Console.WriteLine(failures == 0 ? "\nALL GOOD" : $"\n{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static bool TryParse(string src, out string error)
    {
        error = "";
        try
        {
            // Fresh parser + lexer + token stream per snippet (ParseInput consumes the stream).
            var parser = Zig.BuildParser(Zig.IdentityVisitor.Instance);
            using var lexer = BytesLexer.FromString(src, Zig.BuildLexer());
            using var tokens = new SyncLATokenIterator(lexer);
            parser.ParseInput(tokens);
            return true;
        }
        catch (ParseErrorException ex) { error = ex.Message.Split('\n')[0]; return false; }
    }
}
