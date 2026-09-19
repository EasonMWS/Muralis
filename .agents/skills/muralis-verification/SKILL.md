---
name: muralis-verification
description: Use before reporting that any task is done, fixed, verified, working, or passing - especially for visual, interactive or rendering work (motion, glass, hero, Dock, wallpaper, theming). Use when deciding what evidence a claim needs, when tempted to call a build or a screenshot "verified", or when asked whether something actually works.
---

# Muralis verification standard

## When to use

- Before saying "done", "fixed", "verified", "works", or "tests pass".
- Any visual or interactive change: motion, glass/material, hero, Dock, wallpaper rendering, theming.
- When deciding whether more evidence is needed, or whether a result may be reported at all.
- When asked to accept, sign off, or approve a change.

## Core rules

**The pipeline, in order. Each stage is necessary and none implies the next:**

```
source change
  → build                      (see muralis-build-verification: the gate, 0 warnings)
  → automated tests            (all existing tests pass; guards are part of them)
  → runtime executable proof   (the real binary actually does the thing)
  → GUI / visual / hand-feel acceptance where applicable
```

**Four equalities that are false. Do not write or imply any of them:**

| Not evidence | Of |
| --- | --- |
| Build succeeded | the feature is complete |
| Unit tests passed | the GUI is correct |
| A screenshot exists | anyone — including you — has visually verified it |
| Synthetic pointer input | human hand-feel acceptance |

**Separate the three kinds of proof, and name which one you have.**

- *Automated proof* — a test asserts it. Strong, repeatable, and blind to anything the test does
  not model.
- *Runtime proof* — the built binary was executed and its behaviour observed through a channel you
  can quantify: window geometry, element bounds, process state, counters, logs, latency percentiles.
  This is what turns "the code should" into "it did".
- *Human acceptance* — a person looked at it or felt it. Only a human can supply this for
  hand-feel, aesthetics, and "does it look right".

**If you cannot perceive something, say so instead of implying you did.** A language model cannot
reliably read an image. State plainly that visual acceptance is pending for a human, and hand over
the artifact. "Screenshots saved for human review" is honest; "verified visually" is not.

**Do not report a number you cannot stand behind.** If instrumentation is noisy, if a harness has a
bug, if a measurement ran against the wrong target, or if you never reproduced it — say that. An
admitted gap is useful; a confident wrong number sends the next person down a false path.

**Prefer a measurement over an inference, and a self-check over an assumption.** When a derived
value drives a result, add the check that would catch it being wrong and let it refuse to report.
A harness that fails loudly is worth more than one that produces a plausible table.

**Evidence must come from the artifact under test.** Confirm the binary you ran is the one you
built. Confirm the process you inspected is the one you launched. Confirm the log you parsed
belongs to this run, not a previous one.

**Never leave the machine worse than you found it.** Restore user settings, close the app you
launched, and remove test artifacts you created. Verify the restore rather than assuming it.

**Report honestly, including what you did not do.** Structure a completion claim as: what changed ·
what was verified and how · what was *not* verified and why · what you are uncertain about.

## Forbidden patterns

- Claiming a visual, aesthetic or hand-feel result you did not obtain.
- Claiming tests pass without having run them after the change.
- Presenting an incremental build, or a test against a stale assembly, as verification.
- Softening a failure into a success ("mostly works", "should be fine") instead of reporting it.
- Reporting a measurement you know is unsound because the harness was broken, without saying so.
- Declaring something verified because a plausible number appeared.
- Quoting a previously recorded test count as the current one.
- Passing off synthetic or injected input as real user interaction without labelling it.
- Leaving the app running, or the user's settings altered, at the end of a verification run.
- Silently skipping a requested check and reporting the task complete.

## Relevant architecture / files

- `tools/phase4d-verify.ps1` — the build/test gate; the floor for any claim.
- `tools/perf-measure.ps1` — startup, memory, CPU/GPU measurement harness.
- `tools/p3-common.ps1` and the per-phase `tools/p*-verify.ps1` harnesses — real-machine acceptance.
- `docs/performance.md` — the measurement method and its honest caveats; a model for how to state
  both a result and its limits.
- `src/Muralis.Core/Diagnostics/DropProfile.cs` — the opt-in runtime profiler (environment-gated),
  the sanctioned way to get hot-path numbers.

## Required verification

Match the evidence to the claim:

| Claim | Minimum evidence |
| --- | --- |
| "It compiles" | the gate, 0 warnings, fresh assemblies |
| "Logic is correct" | tests covering the new behaviour, not just the old ones |
| "It runs and behaves" | the built binary executed, behaviour observed and quantified |
| "It looks/feels right" | a human's acceptance; state it as pending until you have it |
| "It is faster" | before/after numbers from the same conditions, with percentiles |

Then state explicitly which rows you actually satisfied.

## Stop / escalation conditions

- You are about to describe a visual result you did not perceive → stop and label it pending.
- A harness is producing inconsistent results → stop; fix or discard the harness before reporting
  anything from it. Do not report the run that happened to agree with you.
- The evidence contradicts the code you believe you wrote → stop and find out which is wrong;
  do not adjust the measurement.
- The only way to make the claim true is to weaken a check → stop; report the failure instead.
- The task asked for acceptance you are not equipped to give → stop and hand over the artifacts.
- Everything passes but you cannot say *why* the change would break if wrong → stop and add the
  test that would catch it.
