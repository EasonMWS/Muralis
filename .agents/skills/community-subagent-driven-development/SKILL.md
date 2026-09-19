---
name: community-subagent-driven-development
description: Use when executing a multi-step plan or a complex multi-file change that benefits from delegating: one fresh implementer per task, review after each task, and a broad review at the end. Use for multi-file architecture work, Windows/shell work, motion work, visual redesigns, and any task where isolated context beats one long session. Also use when delegating to subagents and unsure how to structure it.
---

# Subagent-driven development

Adapted for Muralis and the DeepSeek Harness from the `subagent-driven-development` skill in
[obra/superpowers](https://github.com/obra/superpowers) (MIT). See `.agents/skills/THIRD_PARTY.md`.
Its prompt-file and ledger mechanics are replaced with the tools this harness actually provides.

## When to use

Only when the work genuinely benefits. The dispatch is for tasks that are **multi-step, mostly
independent, and large enough that one context would either overflow or lose the thread**:

- multi-file architecture or refactor work;
- Windows / shell / interop work;
- motion and pointer work;
- a visual redesign;
- anything with a plan that already has discrete tasks.

**Do not use it for small work.** A localisation string, a copy change, a one-file fix, or a
single-component tweak is done faster and better directly. Spinning up a coordinator and three
reviewers for a translation edit wastes time and adds failure modes. When in doubt, do it directly.

## Core rules

**Fresh context per task.** Each implementer gets exactly the instructions and context it needs —
never your session history. You construct the brief; it does not inherit your assumptions. This is
the whole point: an agent that knows what you already tried will reproduce your blind spots.

**Delegate the work, keep the coordination.** Your job is to define tasks, brief precisely, judge
results, and integrate. Their job is to implement one thing well.

**The plan is the authority, the brief is the argument.** Write each task brief from the plan, not
from memory. Include: what to change, where, the constraints that apply, the skills that apply, and
what "done" means. A vague brief produces a confident wrong result.

**Review after every task, not only at the end.** Two questions per task, kept separate:

1. **Spec compliance** — did it do what the task asked, and nothing else?
2. **Code quality** — is it correct, idiomatic for this codebase, and free of the failure modes in
   `muralis-code-review`?

Separating them matters: a change can satisfy the spec and be badly built, or be beautifully built
and solve the wrong problem.

**Never trust a subagent's success report.** It is a claim. Read the diff, run the verification
yourself, and report what you found (`community-verification-before-completion`). A subagent saying
"tests pass" is not tests passing.

**Give the reviewer the artifact, not the implementer's reasoning.** An independent reviewer should
judge the code, not be persuaded by the intent behind it. This is why the review is a separate
dispatch — see `muralis-code-review` for what to look for.

**Rulings, not stalls.** Inside the scope of an approved plan, decide: resolve a conflict, choose
between two readings, fix a small plan defect, record what you decided and why, and continue. An
agent that stops for every ambiguity hands the human a queue of questions instead of a result.

**Stop for these, and only these** (Muralis adds its own to the original's list):

- an irreversible or destructive operation;
- anything security-sensitive;
- a side effect outside the task's scope — a merge, a push, a publish, touching the user's real
  settings or state, installing anything;
- a plan so broken that every path forward is a guess;
- a change that would require an architecture decision — duplicate ownership, a new mode, a layering
  change, or anything `muralis-architecture` lists as stop-and-escalate;
- Stage or phase boundaries. **Do not advance to a new phase or stage on your own initiative**;
  that is a human decision, and this project waits for acceptance.

**Fix loops are bounded.** A finding goes back to the implementer with the specific defect. If a task
fails review repeatedly, do not keep re-dispatching: after a few rounds, stop and reconsider whether
the task was specified correctly or the approach is wrong (`community-systematic-debugging`).

**Integrate before the final review.** Collect the task results, resolve conflicts, and make sure the
whole thing is coherent. A final review of disconnected pieces is not a review of the change.

**Narrate minimally.** Report findings, decisions and results — not a running commentary of every
dispatch.

## The shape

```
Main agent (coordinator)
  ├─ Recon            optional: establish facts, read the code, no changes
  ├─ Implementer      one task, fresh context, precise brief
  ├─ Spec reviewer    did it do the task?  (can be the same dispatch as quality, must be distinct questions)
  ├─ Quality reviewer independent; adversarial
  ├─ Visual reviewer  only for UI work (community-screenshot-critique)
  └─ Integrate + fresh verification, then report
```

Recon is optional but cheap and often pays for itself on unfamiliar ground — a read-only agent that
reports the facts is far better than an implementer that guesses them.

**The visual slot has a specific rule:** for UI work, the reviewer must not be the implementer, and
it reviews the running interface from a screenshot before reading the code
(`community-screenshot-critique`). If no one can perceive the images, that step is *pending*, not
passed.

## Forbidden patterns

- Using this workflow for small or single-file tasks.
- Delegating without a written brief, hoping the subagent infers the goal.
- Letting a subagent inherit your session context when it should have been given a clean brief.
- Accepting "done" or "tests pass" from a subagent without checking the diff and running it yourself.
- Reviewing only spec compliance, or only code quality.
- Letting the implementer review its own work and calling it review.
- Skipping the review because the task "looked simple".
- Re-dispatching the same failing task indefinitely instead of reconsidering the task or approach.
- Advancing a phase or stage, merging, or publishing on your own initiative.
- Paralysing the run by asking about every small ambiguity.
- Reporting a completed plan without a fresh end-to-end verification
  (`community-verification-before-completion`).
- Reporting a visual result no one perceived.

## Relevant architecture / files

- `muralis-code-review` — what the quality review must check, and the review verdict format.
- `community-verification-before-completion` — the gate before any completion claim.
- `community-systematic-debugging` — when a task keeps failing review.
- `muralis-verification` — the evidence ladder, including human acceptance.
- `community-screenshot-critique` — the visual review slot.
- `muralis-build-verification` — the build/test gate each task must pass.
- `muralis-architecture` — the escalations that stop a run.

## Required verification

- Every task: its own build and test run, actually executed, not reported.
- Every task: spec compliance and code quality both assessed.
- The integrated change: one fresh full verification at the end, on the integrated result rather than
  on the pieces.
- The diff reviewed against the plan, to catch scope creep.
- Any decision you made as a ruling, recorded — what you decided, why, and what it costs if wrong.
- For UI work: the visual review state recorded honestly (reviewed, or pending).

## Stop / escalation conditions

- A subagent reports success you cannot reproduce → stop and investigate before integrating.
- A review finds an architecture-level problem → stop feature work and fix correctness first.
- The same task fails review repeatedly → stop; the brief or the approach is wrong.
- The plan turns out to be wrong rather than merely incomplete → stop and report it rather than
  inventing a new plan mid-run.
- You are about to do something irreversible, or outside the task's scope → stop and ask.
- The work has reached a stage or phase boundary → stop and hand back for human acceptance.
- The task was small all along → stop the machinery and just do it.
