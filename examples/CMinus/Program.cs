#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LALR.CC.LexicalGrammar;

namespace Examples.CMinus;

/// <summary>
/// C-minus demo. Compiles a multi-file C99 subset through the full pipeline:
/// <c>BytesLexer → PreprocessorTokenStream (with #if/#ifdef conditionals,
/// #include, #define, #undef) → SyncLATokenIterator → Parser → visitor</c>,
/// and emits a self-contained .NET 10 file-based program that you can directly
/// <c>dotnet run</c>. The same C-minus sources also compile under <c>gcc -std=c99</c>
/// — the grammar is a strict subset of real C, so portable across both back ends.
/// </summary>
/// <remarks>
/// Translation rules:
/// <list type="bullet">
///   <item>C-minus <c>int</c>/<c>float</c>/<c>double</c>/<c>void</c> → C# <c>int</c>/<c>float</c>/<c>double</c>/<c>void</c></item>
///   <item>C-minus <c>char</c> → C# <c>byte</c> (so <c>char*</c> arithmetic walks bytes)</item>
///   <item>C-minus string literals <c>"foo"</c> → <c>L("foo\0"u8)</c> where <c>L</c> pins
///     the UTF-8 RVA data and returns <c>byte*</c>. NUL-terminated for C compat.</item>
///   <item>C-minus <c>malloc</c>/<c>free</c> → runtime helpers backed by
///     <see cref="System.Runtime.InteropServices.NativeMemory"/></item>
///   <item>C-minus <c>printf("%d %f %s", x, y, s)</c> → fluent
///     <c>Printf(fmt).Arg(x).Arg(y).Arg(s).Done()</c> — pointers compose cleanly
///     because each <c>Arg</c> overload takes the typed argument directly (avoids
///     the boxing problem of <c>params object[]</c>).</item>
///   <item>Functions → <c>static unsafe</c> local functions at top level</item>
///   <item>Forward declarations (prototypes in headers) → empty emit (C# hoists)</item>
/// </list>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// Synthetic system headers. Resolved by <see cref="CPreprocessor.OnInclude"/>
    /// alongside any <c>.h</c> files found in the sources directory. Real gcc
    /// uses its own <c>&lt;stdio.h&gt;</c>; our pipeline uses these inline
    /// versions so the parser knows the function signatures. Behaviour at call
    /// sites (printf/malloc/free) is identical via the runtime helpers.
    /// </summary>
    private static readonly Dictionary<string, string> SystemHeaders = new(StringComparer.Ordinal)
    {
        ["stdio.h"] = """
            #ifndef _CMINUS_STDIO_H
            #define _CMINUS_STDIO_H
            int printf(char* fmt, ...);
            #endif
            """,
        ["stdlib.h"] = """
            #ifndef _CMINUS_STDLIB_H
            #define _CMINUS_STDLIB_H
            void* malloc(int size);
            void free(void* p);
            #endif
            """,
    };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var mode = ParseMode(args, out var outDir);
        if (mode == Mode.Help)
        {
            PrintHelp();
            return 0;
        }

        var sourcesDir = ResolveSourcesDir();
        if (sourcesDir is null)
        {
            Console.Error.WriteLine("examples/CMinus/sources/ not found — run from the repo root or rebuild the example.");
            return 2;
        }

        // Each .c file in sources/ is a compilation unit. .h files are
        // pulled in via #include only; we don't compile them directly.
        var compilationUnits = Directory.EnumerateFiles(sourcesDir, "*.c")
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToArray();
        if (compilationUnits.Length == 0)
        {
            Console.Error.WriteLine($"no .c files in {sourcesDir}");
            return 2;
        }

        // Combined header table: user .h files + synthetic system headers.
        // User headers win on name collision — same as real C's quoted
        // include search-path rules (local first, then system).
        var includeMap = new Dictionary<string, string>(SystemHeaders, StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(sourcesDir, "*.h"))
        {
            includeMap[Path.GetFileName(path)] = File.ReadAllText(path);
        }

        var emitter = new CSharpEmitter();
        var parser = Cminus.BuildParser(emitter);
        var lexerTable = Cminus.BuildLexer();

        // Process each compilation unit with its own preprocessor instance.
        // Real C: macros and includes are unit-scoped (don't leak across .c
        // files); we mirror that by giving each unit a fresh CPreprocessor.
        var allFunctions = new StringBuilder();
        var mainArity = -1;
        foreach (var unitPath in compilationUnits)
        {
            var source = File.ReadAllText(unitPath);
            var pre = new CPreprocessor(lexerTable, includeMap);
            using var lexer = BytesLexer.FromString(source, lexerTable);
            using var preproc = Cminus.WrapPreprocessor(lexer, pre);
            using var tokens = new SyncLATokenIterator(preproc);

            var result = parser.ParseInput(tokens, debugger: null, trimReductions: true);
            if (result.IsError)
            {
                Console.Error.WriteLine($"parse failed in {Path.GetFileName(unitPath)}: {result}");
                return 2;
            }

            if (allFunctions.Length > 0)
            {
                allFunctions.AppendLine();
            }
            allFunctions.Append((string)result.Content);

            if (emitter.MainArity >= 0)
            {
                mainArity = emitter.MainArity;
                emitter.ResetMainArity();
            }
        }

        if (mainArity < 0)
        {
            Console.Error.WriteLine("no `main` function defined in any compilation unit.");
            return 2;
        }

        // Emit. Two shapes: the file-based-program shell (single .cs file with
        // the `#:property` directive — `dotnet run <file>` works directly) or
        // the csproj-paired shell (Program.cs + Examples.CMinus.Generated.csproj
        // written to disk; `dotnet build/run` from the dir works).
        switch (mode)
        {
            case Mode.Run:
                Console.WriteLine(BuildShell(mainArity, allFunctions.ToString(), fileBased: true));
                return 0;

            case Mode.Csproj:
            case Mode.Build:
            {
                Directory.CreateDirectory(outDir);
                var program = BuildShell(mainArity, allFunctions.ToString(), fileBased: false);
                var csproj = BuildGeneratedCsproj();
                File.WriteAllText(Path.Combine(outDir, "Program.cs"), program);
                File.WriteAllText(Path.Combine(outDir, "Examples.CMinus.Generated.csproj"), csproj);
                Console.WriteLine($"Generated {outDir}/Program.cs + Examples.CMinus.Generated.csproj");
                if (mode == Mode.Build)
                {
                    Console.WriteLine($"Building {outDir} …");
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "dotnet",
                        WorkingDirectory = outDir,
                    };
                    psi.ArgumentList.Add("build");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("Release");
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc!.WaitForExit();
                    if (proc.ExitCode != 0)
                    {
                        return proc.ExitCode;
                    }
                    Console.WriteLine($"OK. Run with: dotnet {outDir}/bin/Release/net10.0/Examples.CMinus.Generated.dll [args]");
                }
                return 0;
            }

            default:
                // Mode.Help already handled at top.
                return 1;
        }
    }

    private enum Mode { Csproj, Run, Build, Help }

    /// <summary>
    /// Parse CLI args. Recognized: <c>--run</c> (file-based program to stdout),
    /// <c>--build</c> (csproj + build), <c>-h</c>/<c>--help</c>, and an optional
    /// <c>--out PATH</c> for the output directory (default <c>./cminus-out</c>).
    /// Anything else is silently ignored (no positional args expected — the
    /// demo always compiles the files in <c>sources/</c>).
    /// </summary>
    private static Mode ParseMode(string[] args, out string outDir)
    {
        outDir = "cminus-out";
        var mode = Mode.Csproj;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--run": mode = Mode.Run; break;
                case "--build": mode = Mode.Build; break;
                case "-h":
                case "--help": mode = Mode.Help; break;
                case "--out":
                    if (i + 1 < args.Length) { outDir = args[++i]; }
                    break;
            }
        }
        return mode;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Examples.CMinus — transpile examples/CMinus/sources/*.{c,h} to .NET.

            Modes:
              (default)         Write Program.cs + Examples.CMinus.Generated.csproj
                                to ./cminus-out (or --out DIR). Doesn't build.
              --build           As default, then run `dotnet build -c Release`.
              --run             Write a single .NET 10 file-based program to stdout
                                (with the #:property AllowUnsafeBlocks directive).
                                Pipe to a .cs file, then `dotnet run <file>`.
              --out DIR         Output directory for csproj/--build modes.
              -h / --help       This message.

            Same C-minus sources also compile under `gcc -std=c99 sources/*.c`.
            """);
    }

    private static string BuildGeneratedCsproj() => """
        <Project Sdk="Microsoft.NET.Sdk">

          <!-- Generated by Examples.CMinus. Drops the runtime helpers + the
               transpiled C-minus functions into a normal .NET 10 SDK-style
               project. `dotnet run` from this directory just works. -->
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <RootNamespace>CMinusGenerated</RootNamespace>
            <AssemblyName>Examples.CMinus.Generated</AssemblyName>
            <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
            <Nullable>disable</Nullable>
          </PropertyGroup>

        </Project>
        """;

    /// <summary>
    /// Find the <c>sources/</c> directory containing the C-minus source files.
    /// Tries the runtime output dir's adjacent <c>sources/</c> first (set up
    /// via csproj <c>Content CopyToOutputDirectory</c>), then walks up looking
    /// for the in-repo path. Returns <c>null</c> if neither is found —
    /// caller should fail with a clear message.
    /// </summary>
    private static string? ResolveSourcesDir()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "sources");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }
        // Dev fallback: walk up looking for examples/CMinus/sources.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var rel = Path.Combine(dir.FullName, "examples", "CMinus", "sources");
            if (Directory.Exists(rel))
            {
                return rel;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Wrap the emitted function list in a .NET 10 shell. When
    /// <paramref name="fileBased"/> is true, the output prepends a
    /// <c>#:property AllowUnsafeBlocks=true</c> directive so the file can be
    /// run directly with <c>dotnet run &lt;file.cs&gt;</c>. Otherwise the
    /// directive is omitted — the file is intended to live inside a csproj
    /// that sets <c>&lt;AllowUnsafeBlocks&gt;true&lt;/AllowUnsafeBlocks&gt;</c>
    /// via property group.
    /// </summary>
    private static string BuildShell(int mainArity, string emittedFnList, bool fileBased)
    {
        var header = fileBased
            ? "#:property AllowUnsafeBlocks=true\n\n"
            : string.Empty;
        var entry = mainArity switch
        {
            0 => "return main();",
            1 => "return main(args.Length);",
            2 =>
                """
                unsafe
                {
                    // Real C convention: argv[0] = program path, argv[1..] = user args,
                    // argc = total count. Our .NET host receives only the user args in
                    // `args`, so we synthesize argv[0] from the running assembly's
                    // location to match what gcc/clang would pass.
                    int argc = args.Length + 1;
                    byte** argv = (byte**)NativeMemory.Alloc((nuint)argc * (nuint)sizeof(byte*));
                    static byte* EncodeUtf8Nul(string s)
                    {
                        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
                        var slot = (byte*)NativeMemory.Alloc((nuint)(bytes.Length + 1));
                        for (int k = 0; k < bytes.Length; k++) { slot[k] = bytes[k]; }
                        slot[bytes.Length] = 0;
                        return slot;
                    }
                    argv[0] = EncodeUtf8Nul(
                        System.Environment.ProcessPath ?? System.AppContext.BaseDirectory);
                    for (int i = 0; i < args.Length; i++)
                    {
                        argv[i + 1] = EncodeUtf8Nul(args[i]);
                    }
                    try
                    {
                        return main(argc, argv);
                    }
                    finally
                    {
                        for (int i = 0; i < argc; i++) { NativeMemory.Free(argv[i]); }
                        NativeMemory.Free(argv);
                    }
                }
                """,
            _ => throw new InvalidOperationException(
                $"C-minus `main` must have 0, 1, or 2 parameters; got {mainArity}. "
                + "Supported signatures: main(), main(int argc), main(int argc, char** argv)."),
        };

        // $$"""...""" raw-string with {{...}} interpolation so the many literal
        // braces in the runtime helpers don't have to be escaped.
        return $$"""
            {{header}}// <auto-generated>
            // Emitted by Examples.CMinus from cminus.lalr.yaml + the sources in
            // examples/CMinus/sources/.
            // </auto-generated>
            using System;
            using System.Runtime.InteropServices;
            using System.Runtime.CompilerServices;

            {{entry}}

            // String-literal lowering: pin the UTF-8 literal's RVA data and
            // return its base byte*. The literal lives in the assembly's
            // read-only data section, so the pointer is valid for the program
            // lifetime — no heap allocation, no GC pinning needed.
            static unsafe byte* L(ReadOnlySpan<byte> u8) =>
                (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(u8));

            // malloc returns void* (real C semantics); user code must cast to
            // the target pointer type the same way they would in C++ (or
            // strict-C with explicit casts).
            static unsafe void* Malloc(int size) => NativeMemory.Alloc((nuint)size);
            static unsafe void Free(void* p) => NativeMemory.Free(p);

            // Fluent-builder printf — works around the fact that C# can't put
            // raw pointers into `params object[]`. Each Arg overload takes a
            // typed value, parses the next % spec from the format string at
            // call time, formats accordingly. The builder is a ref struct so
            // it stays stack-only and zero-alloc. The PrintfBuilder type
            // declaration lives at the BOTTOM of the file — C# requires
            // top-level statements (and local functions) to precede any type
            // declarations, so we forward-reference it from Printf() and let
            // hoisting resolve it at compile time.
            static unsafe PrintfBuilder Printf(byte* fmt) => new PrintfBuilder(fmt);

            // ---- user functions (also static unsafe local functions) ----

            {{emittedFnList}}

            // ---- PrintfBuilder type declaration must come last ----

            unsafe ref struct PrintfBuilder
            {
                private byte* _fmt;
                public PrintfBuilder(byte* fmt) { _fmt = fmt; }

                public PrintfBuilder Arg(int v)
                {
                    var spec = ConsumeUntilSpec();
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    switch (spec)
                    {
                        case (byte)'d': case (byte)'i': Console.Write(v); break;
                        case (byte)'x': Console.Write(v.ToString("x", ci)); break;
                        case (byte)'X': Console.Write(v.ToString("X", ci)); break;
                        case (byte)'c': Console.Write((char)v); break;
                        // ISO C: %f default precision is 6. Match real printf's
                        // formatting so identical sources produce identical
                        // output under our pipeline and under gcc/clang.
                        case (byte)'f': Console.Write(((double)v).ToString("F6", ci)); break;
                        case (byte)'e': Console.Write(((double)v).ToString("E6", ci)); break;
                        case (byte)'g': Console.Write(((double)v).ToString("G", ci)); break;
                        default: Console.Write(v); break;
                    }
                    return this;
                }

                public PrintfBuilder Arg(double v)
                {
                    var spec = ConsumeUntilSpec();
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    switch (spec)
                    {
                        case (byte)'f': Console.Write(v.ToString("F6", ci)); break;
                        case (byte)'e': Console.Write(v.ToString("E6", ci)); break;
                        case (byte)'g': Console.Write(v.ToString("G", ci)); break;
                        case (byte)'d': case (byte)'i': Console.Write((int)v); break;
                        default: Console.Write(v.ToString("F6", ci)); break;
                    }
                    return this;
                }

                // Float gets promoted to double (real C vararg rule).
                public PrintfBuilder Arg(float v) => Arg((double)v);

                public PrintfBuilder Arg(byte* v)
                {
                    var spec = ConsumeUntilSpec();
                    if (spec == (byte)'s' && v != null)
                    {
                        int len = 0;
                        while (v[len] != 0) { len++; }
                        Console.Write(System.Text.Encoding.UTF8.GetString(v, len));
                    }
                    else if (v == null)
                    {
                        Console.Write("(null)");
                    }
                    else
                    {
                        // Wrong spec for a pointer — print the address.
                        Console.Write(((IntPtr)v).ToString("X"));
                    }
                    return this;
                }

                public int Done()
                {
                    // Write the trailing literal portion after the last % spec.
                    while (*_fmt != 0)
                    {
                        if (*_fmt == (byte)'%' && _fmt[1] == (byte)'%')
                        {
                            Console.Write('%');
                            _fmt += 2;
                            continue;
                        }
                        WriteUtf8Codepoint(ref _fmt);
                    }
                    return 0; // mimic real printf's return-int signature
                }

                private byte ConsumeUntilSpec()
                {
                    while (*_fmt != 0)
                    {
                        if (*_fmt == (byte)'%')
                        {
                            _fmt++;
                            if (*_fmt == (byte)'%')
                            {
                                Console.Write('%');
                                _fmt++;
                                continue;
                            }
                            // Skip flags / width / precision / length modifiers.
                            // v1: minimal pass-through, just grab the conversion.
                            while (*_fmt != 0 && IsFlagOrWidthChar(*_fmt))
                            {
                                _fmt++;
                            }
                            var spec = *_fmt;
                            if (spec != 0) { _fmt++; }
                            return spec;
                        }
                        WriteUtf8Codepoint(ref _fmt);
                    }
                    return 0;
                }

                private static bool IsFlagOrWidthChar(byte b) =>
                    b is (byte)'-' or (byte)'+' or (byte)' ' or (byte)'#' or (byte)'0'
                       or (byte)'.' or (byte)'l' or (byte)'L' or (byte)'h' or (byte)'z'
                       or >= (byte)'1' and <= (byte)'9';

                private static void WriteUtf8Codepoint(ref byte* p)
                {
                    // Single-byte ASCII fast path covers virtually all printf
                    // format strings. Multi-byte runs through Console.Write of
                    // a string slice, which is correct for any UTF-8.
                    byte b = *p;
                    if (b < 0x80)
                    {
                        Console.Write((char)b);
                        p++;
                        return;
                    }
                    int len = 1;
                    if ((b & 0xE0) == 0xC0) { len = 2; }
                    else if ((b & 0xF0) == 0xE0) { len = 3; }
                    else if ((b & 0xF8) == 0xF0) { len = 4; }
                    Console.Write(System.Text.Encoding.UTF8.GetString(p, len));
                    p += len;
                }
            }
            """;
    }

    /// <summary>
    /// IPreprocessor implementation. Maintains the macro table that <c>#define</c>
    /// populates, <c>#undef</c> mutates, and both <see cref="Rewrite"/> and
    /// <see cref="IsDefined"/> consult. Resolves <c>#include</c> against the
    /// shared header map (user + system headers).
    /// </summary>
    private sealed class CPreprocessor : Cminus.IPreprocessor
    {
        private readonly Dictionary<string, LexRule[]> _lexerTable;
        private readonly Dictionary<string, string> _files;
        private readonly Dictionary<string, List<Item>> _macros = new(StringComparer.Ordinal);

        public CPreprocessor(Dictionary<string, LexRule[]> lexerTable, Dictionary<string, string> files)
        {
            _lexerTable = lexerTable;
            _files = files;
        }

        public IEnumerable<Item> OnInclude(IReadOnlyList<Item> args)
        {
            if (args.Count == 0)
            {
                Console.Error.WriteLine("preprocessor: #include with no argument");
                return Array.Empty<Item>();
            }
            var raw = (string)args[0].Content;
            if (raw is null || raw.Length < 2 || raw[0] != '"' || raw[^1] != '"')
            {
                Console.Error.WriteLine($"preprocessor: #include arg '{raw}' is not a quoted filename");
                return Array.Empty<Item>();
            }
            var name = raw[1..^1];
            if (!_files.TryGetValue(name, out var source))
            {
                Console.Error.WriteLine($"preprocessor: #include '{name}' not resolvable (not in sources/ or system headers)");
                return Array.Empty<Item>();
            }

            // Recursive include: fresh lexer + preprocessor wrapper sharing
            // `this` so macros / header guards mutate the same state.
            using var subLexer = BytesLexer.FromString(source, _lexerTable);
            using var subPreproc = Cminus.WrapPreprocessor(subLexer, this);
            var tokens = new List<Item>();
            while (subPreproc.MoveNext())
            {
                tokens.Add(subPreproc.Current);
            }
            return tokens;
        }

        public IEnumerable<Item> OnDefine(IReadOnlyList<Item> args)
        {
            if (args.Count < 1)
            {
                Console.Error.WriteLine("preprocessor: #define with no name");
                return Array.Empty<Item>();
            }
            var name = (string)args[0].Content;
            if (string.IsNullOrEmpty(name))
            {
                Console.Error.WriteLine("preprocessor: #define name is empty");
                return Array.Empty<Item>();
            }
            // Empty body (e.g. `#define FOO`) is allowed — defines FOO as a marker.
            _macros[name] = args.Count > 1 ? args.Skip(1).ToList() : new List<Item>();
            return Array.Empty<Item>();
        }

        public IEnumerable<Item> OnUndef(IReadOnlyList<Item> args)
        {
            if (args.Count > 0 && args[0].Content is string name)
            {
                _macros.Remove(name);
            }
            return Array.Empty<Item>();
        }

        public IEnumerable<Item> Rewrite(Item token)
        {
            if (token?.Content is string text && _macros.TryGetValue(text, out var body))
            {
                return body;
            }
            return new[] { token };
        }

        public bool IsDefined(string name) => name != null && _macros.ContainsKey(name);
    }

    /// <summary>
    /// Visitor: every AST node returns a C# source-code snippet. Container
    /// nodes (functions, blocks, statement lists) concatenate children's
    /// snippets; leaves emit primitive C# (identifiers, literals, operators).
    /// </summary>
    private sealed class CSharpEmitter : Cminus.IVisitor<string>
    {
        // Separator used by ArgsCons to keep argument boundaries visible to
        // the Call visitor — needed for printf special-casing. U+0001 is a
        // control char that can't appear inside real C identifiers/literals
        // that survived to this stage, so splitting on it is unambiguous.
        // Regular (non-printf) calls just replace it with ", " in the final
        // emit.
        private const char ArgSep = '';

        /// <summary>
        /// Arity of the C-minus <c>main</c> function (0, 1, or 2), or -1 if
        /// the program has no <c>main</c>. Set during AST visit. Reset by the
        /// driver between compilation units via <see cref="ResetMainArity"/>.
        /// </summary>
        public int MainArity { get; private set; } = -1;

        public void ResetMainArity() => MainArity = -1;

        // ===== Function-level =====
        public string Visit(Cminus.FuncDef n)
        {
            var type = (string)n.Arg0.Content;
            var name = (string)n.Arg1.Content;
            var pars = (string)n.Arg3.Content;
            var body = (string)n.Arg5.Content;
            if (name == "main")
            {
                MainArity = CountCommas(pars) + 1;
            }
            return $"static unsafe {type} {name}({pars})\n{body}";
        }

        public string Visit(Cminus.FuncDefNoArgs n)
        {
            var type = (string)n.Arg0.Content;
            var name = (string)n.Arg1.Content;
            var body = (string)n.Arg4.Content;
            if (name == "main")
            {
                MainArity = 0;
            }
            return $"static unsafe {type} {name}()\n{body}";
        }

        // Forward declarations: real C needs them for prototypes in headers.
        // C# doesn't (methods are hoisted), so we emit nothing — the AST
        // record's existence is enough to confirm we parsed the prototype
        // syntax correctly. Headers full of prototypes become empty strings,
        // which concatenate harmlessly into the final emit.
        public string Visit(Cminus.ProtoDef n) => string.Empty;
        public string Visit(Cminus.ProtoDefNoArgs n) => string.Empty;

        public string Visit(Cminus.FnsCons n) =>
            (string)n.Arg0.Content + (((string)n.Arg0.Content).Length > 0 ? "\n\n" : "") + (string)n.Arg1.Content;

        public string Visit(Cminus.FnsOne n) =>
            (string)n.Arg0.Content;

        // ===== Params =====
        public string Visit(Cminus.Param n) =>
            $"{(string)n.Arg0.Content} {(string)n.Arg1.Content}";

        public string Visit(Cminus.ParamsCons n) =>
            $"{(string)n.Arg0.Content}, {(string)n.Arg2.Content}";

        public string Visit(Cminus.ParamsOne n) =>
            (string)n.Arg0.Content;

        // Varargs marker `, ...` at end of param list → C# `params object[] _va`.
        // The user's printf wouldn't actually USE this — printf is special-cased
        // at the call site. But to make the prototype parse, we emit a
        // placeholder so the C# compiler accepts the declaration. (For demo,
        // the only function that takes `...` is printf, which we intercept.)
        public string Visit(Cminus.ParamsVararg n) =>
            $"{(string)n.Arg0.Content}, params object[] _va";

        // ===== Types =====
        public string Visit(Cminus.TypeInt n) => "int";
        public string Visit(Cminus.TypeChar n) => "byte";      // char* → byte* for real byte arithmetic
        public string Visit(Cminus.TypeFloat n) => "float";
        public string Visit(Cminus.TypeDouble n) => "double";
        public string Visit(Cminus.TypeVoid n) => "void";
        public string Visit(Cminus.TypePtr n) => $"{(string)n.Arg0.Content}*";

        // ===== Block / statements =====
        public string Visit(Cminus.Block n) =>
            "{\n" + IndentEach((string)n.Arg1.Content) + "}\n";

        public string Visit(Cminus.BlockEmpty n) => "{ }\n";

        public string Visit(Cminus.StmtsCons n) =>
            (string)n.Arg0.Content + (string)n.Arg1.Content;

        public string Visit(Cminus.StmtsOne n) =>
            (string)n.Arg0.Content;

        public string Visit(Cminus.StmtIf n) =>
            $"if ({(string)n.Arg2.Content}) {(string)n.Arg4.Content}";

        public string Visit(Cminus.StmtIfElse n) =>
            $"if ({(string)n.Arg2.Content}) {(string)n.Arg4.Content}else {(string)n.Arg6.Content}";

        public string Visit(Cminus.StmtWhile n) =>
            $"while ({(string)n.Arg2.Content}) {(string)n.Arg4.Content}";

        public string Visit(Cminus.StmtReturn n) =>
            $"return {(string)n.Arg1.Content};\n";

        public string Visit(Cminus.StmtReturnVoid n) =>
            "return;\n";

        public string Visit(Cminus.StmtDecl n) =>
            $"{(string)n.Arg0.Content};\n";

        public string Visit(Cminus.StmtExpr n) =>
            // C# rejects parenthesized assignments as statements (CS0201);
            // strip the outer parens our binary-op emitters wrap on.
            $"{StripOuterParens((string)n.Arg0.Content)};\n";

        // ===== Declarations =====
        public string Visit(Cminus.Decl n) =>
            $"{(string)n.Arg0.Content} {(string)n.Arg1.Content}";

        public string Visit(Cminus.DeclInit n) =>
            $"{(string)n.Arg0.Content} {(string)n.Arg1.Content} = {(string)n.Arg3.Content}";

        // ===== Expressions — paren-heavy to stay precedence-safe =====
        public string Visit(Cminus.Assign n) =>
            $"({(string)n.Arg0.Content} = {(string)n.Arg2.Content})";

        public string Visit(Cminus.Lor n) =>
            $"({(string)n.Arg0.Content} != 0 || {(string)n.Arg2.Content} != 0)";

        public string Visit(Cminus.Land n) =>
            $"({(string)n.Arg0.Content} != 0 && {(string)n.Arg2.Content} != 0)";

        public string Visit(Cminus.Eq n) =>
            $"({(string)n.Arg0.Content} == {(string)n.Arg2.Content})";

        public string Visit(Cminus.Neq n) =>
            $"({(string)n.Arg0.Content} != {(string)n.Arg2.Content})";

        public string Visit(Cminus.Lt n) =>
            $"({(string)n.Arg0.Content} < {(string)n.Arg2.Content})";

        public string Visit(Cminus.Gt n) =>
            $"({(string)n.Arg0.Content} > {(string)n.Arg2.Content})";

        public string Visit(Cminus.Le n) =>
            $"({(string)n.Arg0.Content} <= {(string)n.Arg2.Content})";

        public string Visit(Cminus.Ge n) =>
            $"({(string)n.Arg0.Content} >= {(string)n.Arg2.Content})";

        public string Visit(Cminus.Add n) =>
            $"({(string)n.Arg0.Content} + {(string)n.Arg2.Content})";

        public string Visit(Cminus.Sub n) =>
            $"({(string)n.Arg0.Content} - {(string)n.Arg2.Content})";

        public string Visit(Cminus.Mul n) =>
            $"({(string)n.Arg0.Content} * {(string)n.Arg2.Content})";

        public string Visit(Cminus.Div n) =>
            $"({(string)n.Arg0.Content} / {(string)n.Arg2.Content})";

        // Cast `(Type)expr` — emit the same in C# (works for primitive
        // numeric conversions and pointer-type casts). The inner expr was
        // already parenthesized by its visitor; the outer cast paren makes
        // it unambiguous.
        public string Visit(Cminus.Cast n) =>
            $"(({(string)n.Arg1.Content}){(string)n.Arg3.Content})";

        public string Visit(Cminus.Deref n) =>
            $"(*{(string)n.Arg1.Content})";

        public string Visit(Cminus.AddrOf n) =>
            $"(&{(string)n.Arg1.Content})";

        public string Visit(Cminus.Neg n) =>
            $"(-{(string)n.Arg1.Content})";

        // Call — special-cased for printf so we can route to the fluent
        // PrintfBuilder. Pointer args (char*) don't fit into params object[];
        // PrintfBuilder.Arg has typed overloads instead.
        public string Visit(Cminus.Call n)
        {
            var callee = (string)n.Arg0.Content;
            var argsRaw = (string)n.Arg2.Content;
            if (callee == "Printf")
            {
                var args = argsRaw.Split(ArgSep);
                var sb = new StringBuilder();
                sb.Append("Printf(").Append(args[0]).Append(')');
                for (int i = 1; i < args.Length; i++)
                {
                    sb.Append(".Arg(").Append(args[i]).Append(')');
                }
                sb.Append(".Done()");
                return sb.ToString();
            }
            // Non-printf: replace U+0001 separator with comma-space.
            return $"{callee}({argsRaw.Replace(ArgSep.ToString(), ", ")})";
        }

        public string Visit(Cminus.CallNoArgs n)
        {
            var callee = (string)n.Arg0.Content;
            if (callee == "Printf")
            {
                // printf() with no args is unusual; emit as printf with empty fmt.
                return "Printf(L(\"\\0\"u8)).Done()";
            }
            return $"{callee}()";
        }

        // ArgsCons uses U+0001 instead of ", " so Call can split args back out
        // for printf specialization. Regular calls get the substitution done
        // at Call emit time.
        public string Visit(Cminus.ArgsCons n) =>
            $"{(string)n.Arg0.Content}{ArgSep}{(string)n.Arg2.Content}";

        public string Visit(Cminus.ArgsOne n) =>
            (string)n.Arg0.Content;

        public string Visit(Cminus.Var n) =>
            MapBuiltin((string)n.Arg0.Content);

        public string Visit(Cminus.Num n) =>
            (string)n.Arg0.Content;

        // Float literal — C# accepts the same syntax (1.5, 3.14e10) directly.
        // No decoration needed; passthrough.
        public string Visit(Cminus.Flt n) =>
            (string)n.Arg0.Content;

        // String literal — lower to a NUL-terminated UTF-8 RVA pointer via
        // the L() helper. The raw token content is `"..."` (including quotes);
        // strip them, escape any quote/backslash in the body, append \0.
        public string Visit(Cminus.Str n)
        {
            var raw = (string)n.Arg0.Content;
            if (raw is null || raw.Length < 2)
            {
                return "L(\"\\0\"u8)";
            }
            var body = raw[1..^1];
            return $"L(\"{EscapeForUtf8Literal(body)}\\0\"u8)";
        }

        public string Visit(Cminus.Paren n) =>
            $"({(string)n.Arg1.Content})";

        /// <summary>
        /// Map well-known C-minus identifiers to runtime helpers. <c>printf</c>
        /// → <c>Printf</c> is intercepted specially in <see cref="Visit(Cminus.Call)"/>
        /// to route through the fluent <c>PrintfBuilder</c>.
        /// </summary>
        private static string MapBuiltin(string name) => name switch
        {
            "malloc" => "Malloc",
            "free" => "Free",
            "printf" => "Printf",
            _ => name,
        };

        /// <summary>
        /// Prepare a C-minus string-literal body for inclusion inside a C#
        /// <c>"..."u8</c>. C and C# share most escape conventions
        /// (<c>\n</c> = newline, <c>\t</c> = tab, <c>\\</c> = backslash,
        /// etc.), so user-side escape sequences pass through unchanged —
        /// the C# compiler will interpret <c>\n</c> the same way the C
        /// compiler would. We only insert escapes for:
        /// <list type="bullet">
        ///   <item>literal CR/LF/TAB chars (rare — our lexer pattern
        ///   <c>"[^"]*"</c> allows multi-line strings, but C#'s non-verbatim
        ///   form doesn't)</item>
        ///   <item>literal <c>"</c> (defensive — the lexer pattern already
        ///   prevents quotes inside strings, so this can't actually trigger,
        ///   but cheap insurance)</item>
        /// </list>
        /// A backslash followed by ANY char is copied verbatim — we trust
        /// the user wrote a valid C escape sequence. (Invalid escapes pass
        /// through to the C# compiler, which may complain about unknown
        /// escapes like <c>\q</c>.)
        /// </summary>
        private static string EscapeForUtf8Literal(string body)
        {
            var sb = new StringBuilder(body.Length);
            for (int i = 0; i < body.Length; i++)
            {
                var c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    // Copy the escape sequence verbatim; C# and C interpret it the same.
                    sb.Append('\\').Append(body[i + 1]);
                    i++;
                    continue;
                }
                switch (c)
                {
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '"': sb.Append("\\\""); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static int CountCommas(string s)
        {
            var count = 0;
            foreach (var c in s)
            {
                if (c == ',')
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Peel one layer of outer parens if they enclose the whole string —
        /// used by <see cref="Visit(Cminus.StmtExpr)"/> so the emitted form
        /// is a bare assignment/call, not a parenthesized expression (C#
        /// CS0201).
        /// </summary>
        private static string StripOuterParens(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 2 || s[0] != '(' || s[^1] != ')')
            {
                return s;
            }
            var depth = 0;
            for (var i = 0; i < s.Length - 1; i++)
            {
                if (s[i] == '(')
                {
                    depth++;
                }
                else if (s[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return s;
                    }
                }
            }
            return s.Substring(1, s.Length - 2);
        }

        private static string IndentEach(string block)
        {
            if (string.IsNullOrEmpty(block))
            {
                return block;
            }
            var sb = new StringBuilder(block.Length + 32);
            var first = true;
            foreach (var line in block.Split('\n'))
            {
                if (!first)
                {
                    sb.Append('\n');
                }
                first = false;
                if (line.Length == 0)
                {
                    continue;
                }
                sb.Append("    ").Append(line);
            }
            if (block.EndsWith('\n'))
            {
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
