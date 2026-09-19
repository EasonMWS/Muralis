---
name: muralis-build-verification
description: Use before claiming any code change is built, tested, or verified in this repository - building, running tests, producing a binary for runtime testing, or reporting a build/test result. Also use when a build "succeeds" but the change seems absent, when a test passes suspiciously fast, or when setting up the toolchain on this machine.
---

# Muralis build and verification pipeline

## When to use

- Any build or test command in this repository.
- Before reporting "builds clean", "tests pass", or "this binary contains my change".
- When a build reports success but behaves like the old code.
- Setting up `.NET` on a fresh machine or shell.

## Core rules

**A plain incremental build is not evidence.** This machine's MSBuild up-to-date check reports a
project as current when it is not, which leaves a stale assembly in `bin` after a source edit. A
test run against a stale assembly passes for the wrong reason. Never present an incremental build
as proof that a change was compiled.

**Use the repository's verification gate for anything that will be reported as verified:**

```
./tools/phase4d-verify.ps1                     # Debug (default)
./tools/phase4d-verify.ps1 -Configuration Release
./tools/phase4d-verify.ps1 -Filter <expression>
```

It exists specifically to defeat the two machine-level traps below, and it fails loudly rather
than reporting a misleading success. Prefer it over hand-rolled `dotnet` invocations.

**The gate's contract — what it guarantees, and what to reproduce if you build by hand:**

1. Deletes the assembly it is about to produce, then builds with `--no-incremental`, so a full
   compile actually happens.
2. Requires a **freshly produced assembly** afterwards; a missing one is an error.
3. Requires **0 warnings** and reports the count it parsed.
4. Builds each **project individually** — never through the solution.
5. Runs **both** test assemblies and fails on any failure.
6. Builds both configurations when asked; Release is a separate check, not an afterthought.

Read the script for the exact flags rather than re-deriving them:
`src/*` build with `-c <Configuration> -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -m:1
--no-incremental -v minimal --nologo`.

**Never assert a specific test count.** The suite grows; a hardcoded number in a report or a
document goes stale and then misleads. Say *"all existing tests pass"* and report the counts the
run actually printed. Numbers in README/CHANGELOG are historical snapshots, not targets.

**The toolchain lives in a non-default location.** The SDK is at `C:\Users\Eason\.dotnet` and is
**not** the `dotnet` on `PATH` — that one has runtimes only and cannot build. Set both variables
before invoking `dotnet` by hand:

```powershell
$env:DOTNET_ROOT = 'C:\Users\Eason\.dotnet'
$env:PATH = "C:\Users\Eason\.dotnet;$env:PATH"
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'   # keeps "Build succeeded" parseable
$env:MSBUILDDISABLENODEREUSE = '1'      # avoids node reuse holding output files
```

If a build fails with an SDK-not-found or "not recognized" error, check these first.

**`Muralis.slnx` restore is unreliable under this SDK** and reports its failure as "0 errors", so a
solution-level build can silently do nothing. Build the projects individually:

- `src/Muralis.Core/Muralis.Core.csproj`
- `src/Muralis.Desktop/Muralis.Desktop.csproj`
- `src/Muralis.App/Muralis.App.csproj` (output assembly is `Muralis.dll`)
- `tests/Muralis.Core.Tests`, `tests/Muralis.Desktop.Tests`

**The app must not be running while you build it.** A live `Muralis` process holds `Muralis.dll`
and the build fails with an access-denied on the assembly. Stop it first.

**Expected build shape.** `Muralis.Core` targets plain `net10.0`; `Muralis.Desktop` and
`Muralis.App` target `net10.0-windows10.0.26100.0` with `Platform=x64`, `RuntimeIdentifier=win-x64`
and `Platforms=x64;ARM64`. Core tests run from the framework-specific output, the other two from
the RID-specific one.

**Debug and Release are separate evidence.** A Debug build says nothing about Release behaviour;
trimming, inlining and optimisation differ. Verify the configuration you intend to ship.

## Forbidden patterns

- Reporting a bare `dotnet build` or an incremental build as verification.
- Reporting tests that ran against an assembly you did not just rebuild.
- Writing a specific test count, or a specific warning count, into a document as a permanent rule.
- Building through `Muralis.slnx` and trusting "0 errors".
- Editing `tools/phase4d-verify.ps1` to make a failure go away. If it fails, it found something —
  investigate the cause, and if the gate itself is wrong, fix it deliberately and say so.
- Deleting `bin`/`obj` with `Remove-Item -Include` (it silently matches nothing). Delete the
  assembly or use explicit directory recursion.
- Claiming a runtime or GUI result from a build result. See `muralis-verification`.

## Relevant architecture / files

- `tools/phase4d-verify.ps1` — the gate; read it before building by hand.
- `Directory.Build.props`, `Directory.Packages.props`, `Muralis.slnx`.
- `docs/performance.md` — the one place historical measurements live.

## Required verification

1. Run the gate; it is the minimum bar for any claim that code is built and tested.
2. Confirm the run printed a fresh assembly path and a timestamp for each project.
3. Confirm the warning count it reports is 0.
4. Confirm **both** test suites ran and reported their counts.
5. If you also need Release, run it as a second invocation rather than assuming Debug implies it.
6. For a runtime claim, take the binary path the gate printed and use *that* binary — do not reach
   for a `bin` folder by habit, and do not test a previously built one.

## Stop / escalation conditions

- The gate fails on a warning you believe is unrelated → stop and fix or justify it; the zero-warning
  bar is deliberate.
- The gate passes but the new behaviour is absent → stop; you are almost certainly testing a stale
  binary. Re-check the timestamp of the assembly you are actually running.
- A build error is about the SDK, restore, or a locked file rather than your code → stop; fix the
  environment (variables, running processes) instead of editing code to work around it.
- You are tempted to weaken or skip the gate to get a green result → stop. That converts a known
  unknown into a false claim.
- Tests pass but you changed behaviour and no test changed → stop and ask whether the change is
  actually covered, then add the test or state the gap explicitly.
