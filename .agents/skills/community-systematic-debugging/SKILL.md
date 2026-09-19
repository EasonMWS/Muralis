---
name: community-systematic-debugging
description: Use when facing any bug, test failure, build break, crash, visual defect, or unexpected behaviour, BEFORE proposing or attempting a fix - especially under time pressure, when a "quick fix" looks obvious, or when a previous fix did not work. Also use when the same symptom keeps returning in new places.
---

# Systematic debugging

Adapted for Muralis from the `systematic-debugging` skill in
[obra/superpowers](https://github.com/obra/superpowers) (MIT). See `.agents/skills/THIRD_PARTY.md`.

## When to use

Any technical problem: a failing test, a crash, a wrong value, a visual defect, a performance
regression, a build or integration failure.

Use it **especially** when:

- there is time pressure (urgency is what makes guessing tempting),
- a "quick fix" looks obvious,
- you have already tried one or more fixes,
- a previous fix did not work,
- you do not actually understand the mechanism yet.

Do not skip it because the problem looks simple. Simple problems have root causes too.

## Core rules

**The iron law: no fixes without root-cause investigation first.** If Phase 1 is not complete, you
are not allowed to propose a fix.

**Four phases, in order. Each must finish before the next starts.**

### Phase 1 — Root cause investigation

1. **Read the error completely.** Do not skim past warnings. Stack traces usually name the exact
   frame. Note line numbers, file paths, error codes, and the *first* exception rather than the last.
2. **Reproduce it reliably.** Establish the exact steps. If it is intermittent, gather more data —
   do not guess. An intermittent bug you cannot reproduce is a bug you cannot verify a fix for.
3. **Check what changed.** Recent diff, recent commits, new dependencies, configuration, and
   environment differences. Most regressions are recent changes.
4. **Instrument the boundaries.** When several layers are involved, add diagnostics at each boundary
   — what enters, what leaves, what state holds — run once to find *which* layer breaks, and only
   then investigate inside that layer. Reading code across five layers and theorising is what this
   step exists to prevent.
5. **Trace the bad value backwards.** Where did the wrong value originate? What called this with it?
   Keep going up until you reach the source. Fix at the source, not at the symptom.

### Phase 2 — Pattern analysis

1. **Find a working example** in the same codebase: what works that is *similar* to what is broken?
2. **Compare against the reference implementation completely** if you are following a pattern. Do not
   read part of it and adapt the rest from memory.
3. **List every difference**, however small. Do not decide in advance that one "cannot matter".
4. **Understand the dependencies** — settings, configuration, environment, assumptions.

### Phase 3 — Hypothesis and testing

1. **State one hypothesis explicitly:** "X is the root cause because Y." Write it down. Be specific.
2. **Test it minimally** — the smallest possible change, one variable at a time.
3. **Verify before continuing.** Worked → Phase 4. Did not work → form a *new* hypothesis. Do not
   stack another fix on top.
4. **When you do not know, say so.** "I don't understand X yet" is a legitimate and useful output.
   Pretending to know wastes the investigation.

### Phase 4 — Implementation

1. **Create a failing test case first** — the simplest reproduction, automated if the project has a
   framework, a one-off script if not. It must exist before the fix.
2. **Implement a single fix** addressing the identified root cause. One change. No "while I'm here"
   improvements, no bundled refactoring.
3. **Verify the fix fresh:** the test passes now, no other test broke, the original symptom is
   actually gone. Then see `community-verification-before-completion`.
4. **Count your fixes.** If a fix does not work: under three attempts, return to Phase 1 and
   re-analyse with the new information. **At three or more, stop and question the architecture.**
   Do not attempt a fourth fix without that discussion.

**Three or more failed fixes means the architecture is wrong, not the hypothesis.** The signals:
each fix reveals new shared state or coupling in a different place; fixes require restructuring to
implement; each fix creates new symptoms elsewhere. At that point stop and ask whether the approach
itself is sound, rather than continuing to fix symptoms. Report it as an architecture problem.

**Watch for the rationalizations.** "Quick fix now, investigate later", "just try changing X and
see", "add several changes then run the tests", "skip the test, I'll verify manually", "it's
probably X", "I don't fully understand but this might work", "one more fix attempt" — every one of
these means stop and return to Phase 1.

## Forbidden patterns

- Proposing or applying a fix before root cause is understood.
- Fixing the symptom where it surfaces rather than the source where it originates.
- Multiple simultaneous changes — you cannot then tell which one mattered.
- Changing the test to match the buggy behaviour, or deleting/weakening a guard, to make a failure
  go away.
- Editing behaviour to fit a stale artifact (see `muralis-build-verification` for the build-side
  version of this trap).
- Claiming a fix works without a fresh reproduction.
- Silencing an exception or a warning instead of resolving it.
- Continuing past a third failed fix.
- Reporting "probably fixed", "should work now", or "looks correct".

## Muralis-specific debugging notes

These are the layers where a bug is most often *not* where it appears. Check the boundary before
assuming the layer:

- **Build vs. runtime.** This machine's incremental build can leave a stale assembly, so the running
  binary may not contain your change at all. Before debugging behaviour, confirm the artifact is
  fresh (`muralis-build-verification`). This has produced false conclusions before.
- **Composition vs. layout.** Three transform hosts own different channels, and `Scale`/
  `Translation`/`CenterPoint` are mutually exclusive with `RenderTransform` on one element. A visual
  value that "does not apply" is often an ownership conflict, not a bad number
  (`muralis-motion-engine`).
- **Coordinate spaces.** Screen pixels, the window's client space, the XAML content space and a
  control's own space are different. A pointer or geometry bug is frequently a space mismatch. Do
  not assume two APIs that both return a point agree on its origin.
- **Theme resolution.** Colours come from shared theme-dictionary brushes, and a window that has not
  had `RequestedTheme` set can resolve them under the wrong theme. A wrong colour in one window is
  often that, not a wrong token (`muralis-design-system`).
- **Thread affinity.** A window belongs to the thread that created it, and its messages go to that
  thread's queue. A component that "receives nothing" may be registered on a thread that is not
  pumping.
- **Environment vs. product.** The desktop layer, DPI, z-order and input ownership are stateful and
  shared with other applications. Establish whether the environment or the code is at fault before
  changing code.
- **Measurement vs. product.** If instrumentation disagrees with itself run to run, suspect the
  measurement first. A harness that perturbs the system it measures cannot report on it.

## Relevant architecture / files

- `tools/phase4d-verify.ps1` — establish a trustworthy build before believing any runtime symptom.
- `%LOCALAPPDATA%\Muralis\logs\` — the application log, including the startup timeline.
- `src/Muralis.Core/Diagnostics/DropProfile.cs` — the opt-in runtime profiler for hot-path questions.
- The architecture guards in `tests/*/Architecture/` — when a change "should work" but behaves
  otherwise, check whether a guard is telling you a boundary was crossed.
- The other skills in this pack are the layer-specific references named above.

## Required verification

- Root cause stated in one sentence before the fix is written.
- A failing test (or explicit reproduction) that exists *before* the fix.
- After the fix: fresh test run, no new failures, original symptom confirmed gone.
- A fresh build of the artifact you tested.
- For a visual or interactive defect: runtime evidence, and human acceptance where the judgement is
  about appearance rather than behaviour (`muralis-verification`).

## Stop / escalation conditions

- Three fixes have failed → stop and treat it as an architecture problem.
- You cannot reproduce it → stop and gather data rather than guessing.
- The investigation shows the problem is environmental or external → say so, document what you
  investigated, and add handling or monitoring rather than pretending it was a code bug.
- The only available fix is a symptom fix → stop; an unexplained workaround will outlive its reason.
- Fixing it properly requires an architecture change → stop and escalate
  (`muralis-architecture`, `muralis-code-review`).
- You catch yourself about to say "should work now" → stop; that is the signal to verify instead.
