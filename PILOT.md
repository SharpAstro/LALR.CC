# PILOT.md

What a plane can and cannot do in this repo. The node quotes this page whole into the planner, worker and reviewer
briefs, so it stays short. `CLAUDE.md` holds the architecture and conventions; this page holds only what a flight
needs to plan, build, check and land.

## What a plane can and cannot do

- A plane is a git worktree at `<repo>/.claude/worktrees/<code>` on `flight/<code>`. Work and commit only there. Do not
  push, open a PR, merge or tag; the controller lands the flight. Leave nothing uncommitted, and put scratch output
  outside the plane.
- Every build and test goes through `pilot run -- <one plain command>`; the node queues jobs per repo, and a job gets
  the node's environment, not the plane's. Never wait with a job that sleeps or polls: use
  `pilot leg wait --for-file <path>` and end the turn.
- A release is not a flight's: the maintainer bumps `<Version>` in `LALR.CC/LALR.CC.csproj` in its own
  "Release X.Y.Z" commit and pushes the `vX.Y.Z` tag, from which CI packs and publishes to NuGet. A flight never
  touches `<Version>`; its report says whether the change wants a minor (a feature) or a patch (a fix) release.
- A consumer's change (sebgod/dotcc pins this package) is its own flight in that repo:
  `pilot follow-up sebgod/dotcc --title ...`, never `gh issue create`.

## Build and check

Run from the repo root. CI builds and tests `-c Release` on Linux; so does a leg.

```bash
dotnet build LALR.CC.sln -c Release
dotnet test LALR.CC.Tests/LALR.CC.Tests.csproj -c Release --no-build
```

- Tests are xUnit v3 on Microsoft.Testing.Platform (no VSTest: `--logger` and `--blame-*` fail the run). The suite
  is small and fast, so a leg runs all of it. To narrow while iterating:
  `dotnet run --project LALR.CC.Tests/LALR.CC.Tests.csproj -c Release --no-build -- --filter-method "*Name*"`.
- A change to the YAML schema, the generator or the lexer keeps stage 0 and stage 1 in parity: CI diffs the
  `Accept` lines of `dotnet run --project Bootstrap/Bootstrap.csproj --no-build -c Release` against
  `Bootstrap.Stage1`'s. Run both and compare them in the report.
- The library is AOT-compatible: a change to `LALR.CC/` adds no reflection, and `dotnet publish
  Bootstrap/Bootstrap.csproj -c Release` stays warning-free (on Windows the publish needs `vswhere` on PATH; if the
  node's PATH lacks it, name the unrun check in the report).
- The source generator is netstandard2.0 and must keep `YamlDotNet` build-time only (`PrivateAssets="all"`). File IO
  in it is banned (RS1035) except the deliberate, suppressed diagnostic dump in `ParserTableBuilder`.
- A grammar with an unresolved conflict throws `GrammarConflictException`; fix the grammar or the table builder,
  never catch it. A bug an example exposes is fixed in the runtime or generator with a regression test, never by
  reshaping the example's grammar.
- Warnings are not errors (`TreatWarningsAsErrors` is false), but a leg adds no new ones. Public API carries
  `/// <summary>` docs, since the package ships its XML docs.

## How a flight merges

- master requires linear history (no merge commits), and a multi-commit PR lands by **rebase merge**, each commit
  kept. Take master with `git rebase origin/master`, never a merge. A rebase that hits a conflict in someone else's
  change is reported, not resolved by guessing.
- Every commit lands on master as it is, so keep a flight's commits few, each one whole, and each one building.

## Writing

No em-dashes, and no `--` in their place: a full stop, a comma, a semicolon or parentheses. This holds for prose,
comments and commit messages. In C#, no null-forgiving `!`: check for null, or pattern-match. Keep existing comments
unless they are outdated or wrong.
