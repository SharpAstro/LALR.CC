# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

LALR(1) parser-table generator and runtime in C#, originally adapted from Phillip Voyle's CodeProject article ([archive](https://web.archive.org/web/20241115071153/http://www.codeproject.com/Articles/252399/LALR-Parse-Table-Generation-in-Csharp)). The parsing loop follows the GOLD parser pseudocode. License: CPOL.

The repo was modernized to **.NET 10 / C# 14 / AOT** in 2026; older articles describing the codebase predate that and may be misleading on details.

## Common commands

```bash
# Build the whole solution (runtime + generator + Bootstrap{,.Stage1} + TestProject + Tui + every example)
dotnet build LALR.CC.sln -c Debug          # or -c Release

# Run all unit tests (xUnit v3 on Microsoft.Testing.Platform — 345 cases)
dotnet test LALR.CC.Tests/LALR.CC.Tests.csproj -c Debug

# Run the test project directly (also via MTP — equivalent, faster startup)
dotnet run --project LALR.CC.Tests/LALR.CC.Tests.csproj -c Debug

# Run a subset of tests (xUnit v3 on MTP exposes the standard MTP --filter-* options)
dotnet run --project LALR.CC.Tests/LALR.CC.Tests.csproj -c Debug -- --filter-method "*PipeRuneIterator*"

# Demo executables (each invokes the parser end-to-end)
dotnet run --project Bootstrap/Bootstrap.csproj                     --no-build -c Debug   # stage 0: BNF parsed by inline grammar
dotnet run --project Bootstrap.Stage1/Bootstrap.Stage1.csproj       --no-build -c Debug   # stage 1: BNF parsed via YAML pipeline
dotnet run --project TestProject/TestProject.csproj                 --no-build -c Debug   # arithmetic with constant folding
dotnet run --project examples/Calculator/Examples.Calculator.csproj --no-build -c Debug   # minimal YAML demo
dotnet run --project examples/Json/Examples.Json.csproj             --no-build -c Debug   # real JSON via visitor
dotnet run --project examples/Latex/Examples.Latex.csproj           --no-build -c Debug   # math → Unicode plain text
dotnet run --project examples/CMinus/Examples.CMinus.csproj         --no-build -c Debug   # C99 → unsafe C# (--run | --build | --help)
dotnet run --project Tui/LALR.CC.Tui.csproj         --no-build -c Debug    # interactive grammar debugger (lalr-tui)

# AOT publish (verifies library + every AOT-flagged consumer stay AOT-clean)
dotnet publish Bootstrap/Bootstrap.csproj                            -c Release
dotnet publish Bootstrap.Stage1/Bootstrap.Stage1.csproj              -c Release
dotnet publish TestProject/TestProject.csproj                        -c Release
```

`Tui` is intentionally JIT-only (`<PublishAot>false</PublishAot>` — YamlDotNet runtime deserializer needs reflection, and Tui's purpose is loading arbitrary `*.lalr.yaml` at runtime, so the runtime YAML dependency is unavoidable). Phase 5's pre-baked tables are for *shippable* grammar consumers, not for the Tui live editor.

The demo binaries print step-by-step parse traces in Debug; in Release the `[Conditional("DEBUG")]` traces drop out and only the final accept/reject line is printed.

## Solution layout

| Project | Type | Purpose |
|---|---|---|
| `LALR.CC/` | Library (net10.0, `IsAotCompatible=true`, `IsTrimmable=true`) | Grammar model, LALR(1) parse-table generator (`ParserTableBuilder`), runtime parser (`Parser`), lexer infrastructure (`PipeBytesLexer`, `IRx` combinators, byte-DFA compiler), `Item` value type, `Schema/` POCOs + `SchemaCompiler`. **The published NuGet package**. |
| `LALR.CC.SourceGenerators/` | Roslyn analyzer (netstandard2.0, `IsRoslynComponent=true`) | YAML grammar source generator. Reads `*.lalr.yaml` AdditionalFiles at build time, emits `<ClassName>.g.cs` (schema), `<ClassName>.Ast.g.cs` (record-per-action), and `<ClassName>.Visitor.g.cs` (typed `IVisitor` interface + `BuildActions`). YamlDotNet is build-time only (`PrivateAssets="all"`). |
| `Bootstrap/` | Exe (`PublishAot=true`) | **Stage 0**: hand-codes the BNF meta-grammar in C# and parses a BNF source string. Reference implementation — depends only on the runtime library, no generator, no YAML. End-to-end smoke test for the byte-DFA lexer path. |
| `Bootstrap.Stage1/` | Exe (`PublishAot=true`) | **Stage 1**: same BNF meta-grammar, but defined in `bnf.lalr.yaml` and consumed via the source generator + a typed `IVisitor` implementation. CI diffs stage0 ↔ stage1 Accept output for byte-identical parity. |
| `TestProject/` | Exe (`PublishAot=true`) | Arithmetic-expression grammar with operator precedence + constant-folding rewriters. Uses an inline tokenizer instead of `PipeBytesLexer` to show that any `IAsyncIterator<Item>` plugs in. |
| `Tui/` (assembly `lalr-tui`) | Exe (`PublishAot=false`) | Interactive terminal debugger for live-edited `*.lalr.yaml` grammars. Loads YAML, runs `SchemaCompiler`, builds a `Parser`, displays grammar / lexer / token-stream / parse-table cells in a `Console.Lib` dock layout. AOT-off because YamlDotNet runtime deserializer needs reflection — and Tui's whole purpose is runtime YAML loading. |
| `examples/Calculator/` | Exe | Smallest end-to-end YAML-pipeline demo: a calculator grammar with three visitor methods. Mirrors the README quick-start. |
| `examples/Json/` | Exe | Real JSON parser via the YAML pipeline. ~50-line `IVisitor` builds `Dictionary<string, object>` / `List<object>` / primitives. |
| `examples/Latex.Grammar/` | Library | Shared LaTeX-math grammar: runs the source generator once on `latex.lalr.yaml` and exposes the resulting `Latex` partial class (Schema, AST records, `IVisitor<T>`). Both LaTeX consumers `ProjectReference` this library — one grammar, multiple visitors. |
| `examples/Latex/` | Exe (`PublishAot=true`) | Wikipedia-style LaTeX math via the shared `Latex.Grammar` library. Visitor renders to Unicode plain text. (Box-layout / sixel renderer for the same grammar lives in the sibling `sharpastro/Console.Lib` repo under `examples/LatexConsole/`.) |
| `examples/CMinus/` | Exe | C99-subset compiler: multi-file `.c`/`.h` sources transpiled to unsafe AOT-clean C#. Exercises the YAML-declared **preprocessor framework** (`#include`, `#define`/`#undef`, full `#if`/`#ifdef`/`#ifndef`/`#else`/`#endif` conditional compilation). Demonstrates strings (`char*` as `byte*`), float+double, varargs `printf` via a fluent `PrintfBuilder`, cast expressions, forward declarations from real `.h` headers with `#ifndef` guards, and `char** argv` UTF-8 encoding. Same sources compile under `clang -std=c99` for byte-identical output. Three driver modes: default (emit csproj+`.cs`), `--build` (emit + dotnet build), `--run` (single .NET 10 file-based program). |
| `LALR.CC.Tests/` | xUnit v3 (Microsoft.Testing.Platform runner; `OutputType=Exe`) | 345 tests covering regex-AST builders, byte/codepoint DFAs, lexer/parser pipeline, diagnostics, schema layer, source generator (incl. end-to-end "emit → compile → load → parse"), the preprocessor framework (directive dispatch, conditional compilation, header guards), and parser-semantics regressions. |

Shared MSBuild settings (`TargetFramework=net10.0`, `LangVersion=14`, deterministic build, etc.) live in `Directory.Build.props`. Don't put them in individual csprojs. NuGet metadata, symbol packages, SourceLink, and the bundled-analyzer pack target live on `LALR.CC.csproj` (the only `IsPackable=true` project).

## Architecture: how a parse happens

The pipeline has three stages plus the parser. Each implements/consumes `IAsyncIterator<T>` / `IAsyncLAIterator<T>` (the latter adds one-token lookahead):

1. **Bytes-to-tokens lexer** — `LexicalGrammar/PipeBytesLexer.cs`. Owns a `PipeReader`, drives one **UTF-8 byte DFA per lexer state** over the raw byte stream, emits `Item` tokens. The DFA per state is built at construction by `DfaCompiler.CompileMany` (typed `IRx` AST → codepoint NFA → codepoint DFA via Thompson + subset construction; see `LexicalGrammar/Dfa/DfaCompiler.cs`) and then `Utf8DfaLowering.Lower` (codepoint DFA → byte DFA via the standard UTF-8 chain split; see `LexicalGrammar/Dfa/Utf8DfaLowering.cs`). Each `LexRule(int symbolId, IRx pattern, string instruction)` carries its own action: `#pop`, `#ignore`, or push a named state. Longest match with first-rule-wins on ties. Bytes are UTF-8 decoded to a string only at token boundaries.
2. **Token lookahead** — `LexicalGrammar/AsyncLATokenIterator.cs`. Wraps any `IAsyncIterator<Item>` to add one-token lookahead.
3. **Parser** — `Parser.cs` + `ParserTableBuilder.cs`. The parser table is built by `ParserTableBuilder` (pure C#, no `LexicalGrammar` dependency — Phase 5 / slice 1 splits this out so the same algorithm can be linked into the netstandard2.0 source generator and run at compile time). The build pipeline: `PopulateProductions → InitSymbols → GenerateLR0Items → ComputeFirstSets → ConvertLR0ItemsToKernels → InitLALRTables → CalculateLookAheads → GenerateParseTable`. `Parser`'s constructor instantiates the builder and exposes its introspection state (FirstSets, LR0/LR1 items, kernels, gotos, propogations, Productions, Terminals/NonTerminals, Conflicts) via `IReadOnly*` passthrough properties. `Parser.ParseInputAsync` then drives the standard shift/reduce/accept/error loop, consulting `ParseTable.Actions[state, tokenId+1]`.

`LexicalGrammar/PipeRuneIterator.cs` is still in the codebase as a standalone codepoint iterator (`IAsyncLAIterator<int>`) for callers that want UTF-8-decoded codepoints rather than tokens, but no part of the parse pipeline uses it anymore — it's an alternative input path, not a building block of `PipeBytesLexer`.

**Optional preprocessor stage** — grammars that declare a `preprocessor:` block in YAML get a `PreprocessorTokenStream` (`LexicalGrammar/PreprocessorTokenStream.cs`) slotted between the lexer and the LA iterator. It's an `ISyncIterator<Item>` that (a) recognises directive tokens by symbol id, collects their same-line args via `SourcePosition.Line`, dispatches to user-supplied `OnX(IReadOnlyList<Item> args)` handlers that return tokens to inject in place of the directive line; (b) optionally routes non-directive tokens through a user-supplied `Rewrite(Item)` hook for macro expansion; (c) handles built-in conditional directives (`#if`/`#ifdef`/`#ifndef`/`#else`/`#endif`) with a depth-tracked `_branchStack`+`_falseDepth` engine — false branches suppress all tokens and user handlers while still tracking depth so nested conditionals match correctly. The conditional engine is wired by a `PreprocessorConditionals` struct that the source generator emits from the YAML's `conditionals:` block. Schema POCOs in `LALR.CC/Schema/GrammarSchema.cs` (`PreprocessorSchema.Directives` + `Conditionals`); generator emit in `LALR.CC.SourceGenerators/PreprocessorEmitter.cs` (produces `IPreprocessor` interface + `BuildPreprocessor` dispatch + `WrapPreprocessor` factory + the `IsDefined` interface method when conditionals are declared). `examples/CMinus/` is the end-to-end consumer.

Grammars are described by:
- `SymbolName[]` — terminal and non-terminal names; index = symbol ID (the LHS=0 symbol is the start symbol).
- `Production` — `Left` (int symbol ID), `Right` (int[] of symbol IDs), optional `Func<int, Item[], object>` rewriter.
- `PrecedenceGroup` — list of productions sharing precedence and a `Derivation` (`None` / `LeftMost` / `RightMost`) that resolves S/R and R/R conflicts. Groups are ordered by descending precedence.
- `Grammar` aggregates the symbol table and the precedence groups.

`Item` (`LexicalGrammar/Item.cs`) is the unifying value type for both terminal tokens and reduced non-terminals. Its `ContentType` is `Scalar` (raw lexer payload), `Reduction` (a `Reduction` with child `Item`s), or `Nested` (single child `Item`). Tokens that bubble up through `IsError` propagate error state through reductions automatically.

`Debug.cs` is a tracer wrapped around a `Parser`; almost every method is `[Conditional("DEBUG")]`. Pass `Console.Write` / `Console.Error.Write` actions to its constructor to emit traces; pass `null` to silence it.

## Conventions and constraints to respect

- **Library is AOT-compatible (`IsAotCompatible=true`)**. No reflection beyond what BCL collections do; no `dynamic`; no runtime-codegen serializers. If you add a feature, keep it allocation-light and reflection-free, and run `dotnet publish` on `Bootstrap/` to verify no AOT warnings.
- **`Conditional("DEBUG")` calls compile to no-ops in Release**. Don't put side-effecting work inside `Dump*` / `Flush` methods.
- **Immutable structs everywhere** in the grammar/parser-table model (`LR0Item`, `LR1Item`, `LALRPropogation`, `Action`, `ParseTable`, `Production`, `PrecedenceGroup`, `Grammar`, `SymbolName`, `CharRx`, etc.). Match that style for any new value types — `readonly struct` with primary constructors and `IEquatable<T>` where equality is meaningful.
- **Read-only collection types on public properties.** Default to `IReadOnlyList<T>` / `IReadOnlyCollection<T>` / `IReadOnlySet<T>` (or concrete `HashSet<T>` when targeting netstandard2.0 — `IReadOnlySet<T>` is .NET 5+) over `IList<T>` / `ICollection<T>` / `ISet<T>`. The `Parser` introspection surface is the canonical example — all of `FirstSets`, `LR0Items`, `Productions`, `Terminals`, `Conflicts`, etc., expose read-only types because external consumers (`Debug.cs`, `lalr-tui`) only ever read.
- **`Parser`'s constructor fails fast on unresolved grammar conflicts.** Any S/R or R/R conflict left after the precedence-group derivation pass throws `GrammarConflictException` (with `Conflicts` enumerating each offending state + lookahead + competing productions). Don't catch and ignore — fix the grammar by adding the colliding productions to a `PrecedenceGroup` with `Derivation.LeftMost` / `Derivation.RightMost`, or by restructuring.
- **`Item` carries a `SourcePosition` (1-based `Line`, 1-based `Column`, absolute `ByteOffset`).** `PipeBytesLexer` / `BytesLexer` populate it; the parser reduces propagate the leftmost child's position (epsilon reductions take the lookahead's position). `default(SourcePosition)` is `Unknown` (`Line==0`); `Item.EOF` and items built without a position both return Unknown. **`Column` defaults to codepoint counting** (one codepoint = one column — diagnostic-friendly for non-ASCII grammars); pass `ColumnMode.Bytes` to a lexer ctor or set `columns: bytes` in the YAML schema to opt back into UTF-8 byte counting. `ByteOffset` is always raw bytes regardless of mode.
- **`PipeBytesLexer` fails fast on unrecognized bytes by default.** Constructor / factories take a `LexerErrorMode` (`Throw` default, `EmitAndStop`, `EmitAndSkip`) and an `errorSymbolId`. `Throw` raises `LexerException` with the offending byte, `SourcePosition`, and lexer state name. The two emit modes require a non-negative `errorSymbolId` and emit an `Item` with `IsError==true` and `Content` set to the hex byte (e.g. `"\x7E"`). Existing callers that passed `cancellationToken` positionally must switch to named-arg form (`cancellationToken: ct`) since the parameter sits after the new mode/id pair.
- **`Parser.ParseInputAsync` mirrors that pattern.** New `errorMode` parameter (`ParserErrorMode.Throw` default, `Return` keeps the legacy "return offending Item" behavior). On `ActionType.Error`, Throw mode raises `ParseErrorException` carrying the offending `Item`, the LALR(1) state, and the set of symbol ids that *would* have been valid at this state — derived by scanning the parse-table row for non-error cells. Also takes `CancellationToken cancellationToken = default` and checks it once per loop iteration. Same named-arg note: `cancellationToken: ct` is the supported call pattern.
- **Bug fix while in there:** the parse-error path used `state < 0 ? state : -state` to mark `Item.State` negative — but `-0 == 0`, so an error at the initial state left `IsError==false`. Now uses `-(state+1)` so every error item is genuinely marked as error. Don't depend on the old behavior anywhere.
- **Schema layer in `LALR.CC/Schema/`** is Phase 1 of the YAML-grammars-and-source-generator arc. `GrammarSchema` + `ProductionGroupSchema` + `ProductionSchema` + `LexRuleSchema` are plain POCOs (settable, deserializer-friendly) describing a grammar declaratively. `IRxParser.Parse(string)` turns a small regex dialect (literals, escapes `\n \r \t \\ \. \[ \] etc`, classes `[a-z]`, `[^abc]`, quantifiers `? + * {n} {n,m} {n,}`, groups `(…)`, **no alternation** — express alternatives as multiple lexer rules) into the typed `IRx` AST. `SchemaCompiler.Compile(schema, actions?)` returns `(Grammar, Dictionary<string, LexRule[]>)`, validates symbol references, lexer-instruction mutual-exclusion, missing root state, etc., surfacing every failure as `SchemaCompilationException` with a path-style locator (`productions[1].rules[3].rhs[2]`).
- **Source generator in `LALR.CC.SourceGenerators/`** is Phase 2: a Roslyn `IIncrementalGenerator` (netstandard2.0, `IsRoslynComponent=true`) that reads `*.lalr.yaml` AdditionalFiles, deserializes them via YamlDotNet (build-time only — `PrivateAssets="all"`, never ships to the user binary), and emits a `static partial class XYZ { static GrammarSchema Schema { get; } = new() { … }; }` in the consumer's `RootNamespace`. Class name is derived from the YAML filename (`arithmetic.lalr.yaml` → `Arithmetic`). YAML parse errors surface as Roslyn diagnostic `LALR0001` with file/line locators. The generator references the runtime library's types in *emitted* C# source only — it never compiles against them. Tests in `LALR.CC.Tests/SourceGenerators/` drive the generator via `CSharpGeneratorDriver` and include an end-to-end "emit → compile → load → parse" integration test. Generator consumers using `<ProjectReference OutputItemType="Analyzer">` won't get YamlDotNet automatically (it's PrivateAssets); add `<PackageReference Include="YamlDotNet" />` in the consumer's csproj for now (proper analyzer-DLL packaging comes when we publish a NuGet).
- **`PrintableList<T>`** in tests is a `List<T>` with `ToString` for friendlier failure messages — use it (not raw `List<T>`) when authoring `MemberData` rows that contain collections.

## Test framework gotchas (xUnit v3)

Tests use **xUnit v3 (3.2.x)** running on **Microsoft.Testing.Platform**, with `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` and `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` enabled. The project sets `<OutputType>Exe</OutputType>` because xunit.v3 emits its own `Main`. There is no `Microsoft.NET.Test.Sdk`, no `xunit.runner.visualstudio`, and no NUnit-era VSTest adapter — `dotnet test` and `dotnet run` both route through MTP.

Patterns to follow when adding or editing tests:

- `[Fact]` for parameterless tests; `[Theory]` + `[InlineData(...)]` for inline-parameterized cases. There is **no** `ExpectedResult`; pass the expected value as the last `InlineData` argument and assert it inside the test body with `Assert.Equal(expected, actual)`.
- For `[InlineData]` arguments typed as `int[]` (or any non-`object[]` array), spell the literal as `new int[] { ... }` — xUnit's analyzer (xUnit1010) rejects `new[] { 'a', 'b' }` because the inferred element type is `char`, not `int`.
- `[MemberData(nameof(Source))]` for runtime-generated cases; the source is a static method or property returning `IEnumerable<object[]>` (untyped) or `TheoryData<...>` (typed). Each row's element order matches the test method's parameter order. Throwing cases go in a separate source and assert with `Assert.Throws<T>` — there is no `TestCaseData.Throws(...)` equivalent.
- For exception assertions, use `Assert.Throws<T>(() => ...)` (sync) or `await Assert.ThrowsAsync<T>(async () => ...)` (async). Don't try to assert exceptions on a `Task<T>` returned synchronously without awaiting it inside `ThrowsAsync`.
- Pass `TestContext.Current.CancellationToken` to async APIs that accept a `CancellationToken` (xUnit1051). `TestContext.Current` is a static accessor unique to xUnit v3.
- xUnit `Assert.Equal(expected, actual)` argument order is **opposite** to NUnit's `Assert.That(actual, Is.EqualTo(expected))`. Watch the diagnostics — getting it backwards still passes, just shows confusing diff output on failures.

## Examples are stress tests, not safe demos

The grammars under `examples/` exist to **exercise the parser/generator pipeline against shapes that haven't come up before** — not just to look pretty. When an example uncovers a runtime bug (parse table misroute, AOT warning, generator diagnostic gap, lexer edge case), the fix goes into the runtime/generator/library, not into the example's grammar. Working around a bug at the grammar level (e.g. inserting a terminal between two non-terminals to dodge a state-tracking quirk in `Parser.ParseInputAsync`) hides the problem from the *next* user who writes a grammar with that shape, defeating the point of having the example in the first place.

Concretely:
- If a natural-looking grammar fails to parse, **trace the parser** (`new Debug(parser, Console.Out.Write, Console.Error.Write)`) and find the misbehaving table cell, code path, or table-construction step. Fix it there.
- If `dotnet publish -c Release` emits an AOT trim warning on an example, fix the warning in the runtime (or annotate the API), not by turning AOT off in the example.
- If the source generator produces awkward emitted code that an example has to paper over, fix the emitter in `LALR.CC.SourceGenerators/`.
- Add a regression test alongside the fix so the case is locked in independently of the example.

The Wikipedia-style LaTeX example (`examples/Latex/`) is the current poster child: its `A -> cmdfrac A A` rule (two non-terminals adjacent in an RHS, separated only by whatever sits inside their internal `{ E }` reductions) was the first grammar to expose a long-standing latent bug where `Parser.cs` stashed `production.Left` (LHS symbol id) on a reduction's `Item.State` instead of the goto-target parser state — the next reduction's `lastState = Peek().State` then mis-routed the goto. The fix landed in `Parser.cs`/`Debug.cs` rather than rewriting the LaTeX grammar.

## Known tech debt

- **Productions still embed semantic actions.** `Production` carries a `Func<int, Item[], object>` rewriter, so grammar definitions and code are entangled. Future direction is a typed AST + visitor split with grammar definitions in YAML; don't double down on the rewriter pattern in new grammars.
- **Two parse paths to keep in lockstep.** `Parser.ParseInputAsync` and `Parser.ParseInput` are intentionally code-duplicated — extracting a shared body would force `ValueTask` boxing or generic gymnastics that erodes the savings. `BytesLexer` likewise duplicates `PipeBytesLexer`'s DFA-per-state compilation block (~25 lines). `SyncAsyncParityTests` keeps the two parse loops in lockstep on the same inputs; if you change one, change the other and re-run that test class. The in-tree consumers route through the sync path now; `Bootstrap` (stage 0) deliberately stays async so the stage-parity diff doubles as a sync ↔ async equivalence test.

## Environment

- Targets `net10.0` only. SDK version pinned by what's installed (currently 10.0.200).
- The repo lives on Windows ARM64 in development (this affects only the AOT publish RID auto-detection).
- Comments in source are kept even when terse; **don't remove comments unless they're outdated or wrong** (per global instructions).
