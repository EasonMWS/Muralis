---
name: muralis-code-review
description: Use when reviewing a change before it is accepted - one you made or one you were handed - and after finishing any complex or multi-file task. Use for adversarial self-review, for reviewing another agent's or a subagent's work, and before declaring a non-trivial change complete. Also use when a change touches transform ownership, lifetimes, threading, persistence or a hot path.
---

# Muralis code review

## When to use

- After completing a complex change, before reporting it done.
- Reviewing someone else's or a subagent's implementation.
- Any change to transform ownership, lifetimes, threading, persistence, or a hot path.
- When a change feels larger than the task that asked for it.

## Core rules

**"It compiles" is not a review.** A change can build, pass every existing test, and still be wrong
in every way that matters: it may duplicate an architecture, leak a subscription, break the
fail-open path, or quietly depend on something retired.

**Review for the failure modes that this codebase actually has.** These are ordered by how often
they have mattered here:

1. **Scope creep** — extra refactors, unrelated cleanups, a "while I was in there". Ask what the
   task asked for and what the diff actually does.
2. **Duplicate architecture** — a second hidden window, window class, device-class registration,
   shell-icon path, cache, or mode service. This project's guards exist mostly because of this one.
3. **Transform ownership** — one writer per transform channel. A second writer on `Scale`,
   `Translation`, `CenterPoint` or `RenderTransform` will not fail to compile; it will produce
   subtle, intermittent wrongness.
4. **Lifecycle and subscription leaks** — every event handler, native handle, window, timer,
   registration and `IDisposable` needs an owner that actually releases it, on a path that really
   runs, idempotently. Check the unsubscribe, not just the subscribe.
5. **Thread affinity** — which thread owns a window, a dispatcher, a shell call, a COM object. A
   call that must be on a specific thread and is not is a latent crash, not a style issue.
6. **The fallback path** — when the new code fails, what does the user get? A missing fallback is
   how a presentation layer takes the desktop down with it.
7. **Fail-open behaviour** — does an unreadable value, a missing capability or a failed mount resolve
   to the safe Windows state, or to a half-applied Muralis state?
8. **Persistence and migration** — old files still readable, newer-than-known refused rather than
   guessed, failed operations not destroying the original, and a default that does not clobber user
   data.
9. **Hidden exceptions** — a swallowed `catch`, an exception logged as information, a failure that
   leaves state half-changed. Silent failure is worse than a loud one.
10. **Hot-path regression** — new allocation, I/O, logging, layout or measurement on a per-frame or
    per-pointer path.
11. **Magic numbers** — a tolerance, threshold, duration or size that belongs in a token or a
    profile. Note which ones are honestly one-off geometry.
12. **Stale cache** — a cache keyed on something that changed, or invalidated on one path but not
    another.
13. **Accidental legacy dependency** — new code reaching into a retired path for convenience.
14. **Test blind spots** — what would still pass if this change were wrong? Tests that assert the
    implementation rather than the behaviour, or that would pass with the feature removed.

**Review the diff against its own claims.** For each thing the author says it does, find the line
that does it and the evidence that it works. A summary is not evidence.

**Do not trust the implementer — including yourself.** If you wrote the change, review it as though
someone else did. The author's mental model is exactly what a bug hides behind.

**Prefer an independent reviewer for complex changes.** When the harness supports subagents or
workflows, hand the diff to a fresh agent instructed to falsify it, rather than reviewing your own
work in the same context that produced it. Tell the reviewer to assume the change is wrong and find
where.

**Report what you actually found.** Distinguish a correctness problem from a preference, and say
which is which. A review that lists only style nits while missing a duplicated architecture has
failed.

## Forbidden patterns

- Reviewing only whether it compiles, or only the happy path.
- Accepting "the tests pass" as the whole review — check whether a test would catch the failure.
- Reviewing your own complex change without an adversarial pass, or marking your own work approved
  because you wrote it.
- Approving a change that adds a second owner of an existing mechanism.
- Approving a new hot-path cost as "probably negligible" without a number.
- Approving a swallowed exception, or one logged at a level that hides it.
- Passing a change that weakens or deletes a test, or that edits an architecture guard, without an
  explicit architecture-level justification.
- Reporting style preferences as defects, or burying a real defect among them.
- Rubber-stamping a subagent's report instead of checking its claims against the tree.

## Relevant architecture / files

- The architecture guards are the checklist made executable — read them alongside the diff:
  `tests/Muralis.Desktop.Tests/Architecture/ArchitectureGuardTests.cs`,
  `tests/Muralis.Desktop.Tests/Architecture/Phase4DArchitectureTests.cs`,
  `tests/Muralis.Core.Tests/Architecture/` (`DesignFoundationTests`, `Phase4AArchitectureTests`,
  `Phase4CArchitectureTests`, `DockFoundationCorrectnessTests`,
  `DesktopExperienceConsolidationTests`, `MuralisModeHeroGuardTests`).
- `tools/phase4d-verify.ps1` — the build/test gate the review runs.
- `.editorconfig` — the style baseline (nullable, no `!` without a comment, naming, line length);
  `CONTRIBUTING.md` for commit and PR expectations.
- The other skills in this pack are the domain checklists to review against:
  `muralis-architecture`, `muralis-motion-engine`, `muralis-windows-shell`,
  `muralis-design-system`, `muralis-performance`, `muralis-build-verification`,
  `muralis-verification`.

## Required verification

Produce a review that states, explicitly:

1. **What the change does**, and whether that matches what was asked (scope).
2. **Per checklist item above**, either what you checked or that it does not apply. Silence on an
   item is not a pass.
3. **The tests**: what covers the new behaviour, and what would still pass if the change were
   reverted.
4. **The guards**: which architecture guard would fail if this broke its rule; if none exists,
   that is itself a finding.
5. **The evidence**: build/test result, and for runtime behaviour what was actually executed.
6. **Findings separated** into correctness defects, risks, and preferences.
7. **An explicit verdict**: approve, approve with conditions, or stop.

## Stop / escalation conditions

- The review finds an **architecture-level** problem — duplicate ownership, a broken layering
  direction, a retired path newly depended on, or fail-open violated → stop feature work and fix
  correctness first. Do not layer more features on a broken foundation.
- A guard was weakened, deleted, or edited to accommodate the change → stop and treat it as an
  architecture decision requiring justification, not a review note.
- The change cannot be explained without reference to code the reviewer cannot see → stop and ask
  for the missing piece rather than approving on faith.
- Tests do not cover the new behaviour and the author proposes to add them later → stop; the tests
  are part of the change.
- The diff is too large to review meaningfully → stop and ask for it to be split; a review of an
  unreviewable diff is a false assurance.
- Runtime behaviour was claimed but not demonstrated → stop; downgrade the claim to what was shown,
  or obtain the evidence.
