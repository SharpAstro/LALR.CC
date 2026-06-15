# Zig grammar spike

A **grammar spike**, not a full example: it hand-translates a C-shaped core of
[Zig](https://ziglang.org)'s official **PEG** grammar into an LALR(1) `zig.lalr.yaml`
and proves the LALR.CC source generator builds it **without conflicts** and that the
result **accepts real Zig**. It's the M0 step of a prospective Zig frontend for
[dotcc](https://github.com/sebgod/dotcc) (a C→.NET transpiler driven by LALR.CC);
dotcc itself only consumes the *finding* — that Zig's grammar is LALR(1)-tractable.

**Scope boundary.** This example stays here as the isolated LALR.CC-side validation
(a conflict regression for the unified value/type expression core). The *production*
Zig grammar and the frontend implementation (lowering to IR, the `@cImport`
composition, comptime/const-eval, etc.) live in the **dotcc** repo, not as further
slices here — so this directory is the minimal, self-contained proof, kept small on
purpose.

## Why a spike

Zig's grammar is distributed as a **PEG** (ordered choice + syntactic predicates),
and PEG → LALR(1) has no general automatic translation. The open question was whether
the *tame, C-shaped subset* transcribes cleanly — and in particular whether Zig's
**unified value/type expression grammar** survives LALR(1). In Zig, types are values,
so one expression cascade covers both, and these tokens each pull **double duty**:

| token | operand position (type / prefix) | after an operand (binary / postfix) |
|---|---|---|
| `*` | pointer type `*T` | multiply |
| `&` | address-of `&x` | bitwise-and |
| `[` | slice/array type `[]T` / `[N]T` | index `a[i]` |
| `!` | (n/a) | error-union `E!T` |
| `.` | enum literal `.Foo` | field `a.b` / `.*` / `.?` |

A PEG resolves this by ordered choice; the spike's question was whether **LALR parser
state** separates the two readings without conflicts. **It does** — no precedence
hacks beyond the standard precedence cascade and a `rightmost` dangling-else group.

## Result

`dotnet run --project examples/Zig/Examples.Zig.csproj`

- **Builds clean** → the generator produced LALR(1) tables with **zero** shift/reduce
  or reduce/reduce conflicts (a conflict aborts the build with
  `GrammarConflictException`).
- **Accepts** functions, top-level `const` / `pub fn`, pointer/optional types,
  error-union returns, calls, field chains, indexing, `.*` / `.?` postfix, if/else,
  while — run through the real `BytesLexer → Parser.ParseInput` pipeline with the
  generated `IdentityVisitor`.
- **Rejects faithfully** — `a < b < c` is a parse error (Zig's compare is
  *non-associative*), pinpointed at the second `<`; a stray operator as a statement
  is rejected too.
- **Zero `RewritingTokenStream` stages** — no typedef lexer-hack, no preprocessor, no
  keyword promotion. Lighter than a C grammar.

## Provenance / pin

`grammar.peg` here is the **frozen** authoritative source — the `{#syntax_block|peg#}`
block from `ziglang/zig` `doc/langref.html.in`, pinned to commit
**`3391ad7a`** (2026-06-03). The differential oracle is the installed
`zig 0.17.0-dev.667+0569f1f6a` (commit `0569f1f6a`, 2026-05-29). The only grammar
delta between the two is a one-line `ParamDecl` typo fix
(`!(IDENTIFIER_COLON)` → `!(IDENTIFIER COLON)`) inside a negative-lookahead predicate
that doesn't translate — i.e. immaterial. When upstream changes the PEG, diff the new
langref block against `grammar.peg` and port the delta into `zig.lalr.yaml`.

## Deferred (documented, not silent)

Slice 1 covers the C-shaped value/type core. Still to translate (all present in
`grammar.peg`):

- **control-flow as expressions** (`if`/`for`/`while`/`switch` exprs) and the
  `!ExprSuffix` / `!BlockExpr` **negative-lookahead predicates** that gate them — the
  one genuine PEG-ism, where LALR can't say "this alternative only if *not* followed by
  an operator." This is the next real test (the first place conflicts may appear);
- labeled blocks/loops (`label:`), payloads (`|x|`), switch prongs, for-ranges;
- container decls (`struct`/`enum`/`union`/`opaque`), error sets, init lists `.{…}`;
- `comptime`/`defer`/`errdefer`/`nosuspend`, `asm`, destructuring assignment, `anytype`;
- full `FnProto` modifiers (`export`/`extern`/`inline`/`callconv`/`align`/`linksection`);
- hex/oct/bin/underscored integers, char literals, multiline strings, builtins beyond
  `@name(args)`, `orelse`/`catch`, and the wrapping/saturating operators.

## Files

| file | role |
|---|---|
| `grammar.peg` | frozen authoritative Zig PEG (pinned `3391ad7a`) — the translation source of truth |
| `zig.lalr.yaml` | the hand LALR(1) translation (slice 1) — conflicts surface at build time |
| `Program.cs` | acceptance harness: parses real Zig snippets, asserts accept/reject |
| `Examples.Zig.csproj` | same shape as `examples/CMinus`, minus the preprocessor block |
