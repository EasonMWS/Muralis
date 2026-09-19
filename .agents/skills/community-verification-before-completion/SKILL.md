---
name: community-verification-before-completion
description: Use immediately before claiming work is complete, fixed, working, or passing - and before committing, opening a PR, or moving to the next task. Requires fresh verification evidence for the specific claim being made, and forbids success wording that no command or observation actually supports.
---

# Verification before completion

Adapted for Muralis from the `verification-before-completion` skill in
[obra/superpowers](https://github.com/obra/superpowers) (MIT). See `.agents/skills/THIRD_PARTY.md`.

## When to use

Before **any** positive statement about the state of work:

- any variation of "done", "fixed", "complete", "works", "passing";
- any expression of satisfaction ("great", "perfect", "that's it");
- committing, opening a PR, or ending a task;
- moving on to the next task;
- accepting a subagent's or another agent's report of success.

This applies to exact phrases, paraphrases, synonyms, and *implications*. Saying it in different
words does not put it outside the rule.

## Core rules

**The iron law: no completion claim without fresh verification evidence.**

If you have not run the verification in this turn, you cannot claim it passes.

**The gate function. Run it before every status claim:**

```
1. IDENTIFY  what command or observation would prove this specific claim?
2. RUN       the full thing, fresh and complete — not a partial check
3. READ      the whole output: exit code, failure count, what actually printed
4. VERIFY    does the output confirm the claim?
               no  → state the actual status, with the evidence you have
               yes → state the claim, with the evidence
5. ONLY THEN make the claim
```

Skipping a step is not abbreviated verification; it is an unsupported claim.

**Fresh means now.** A previous run, another agent's summary, "it passed before my last edit", and
"it should pass" are all not evidence. Re-run after the last change.

**Match the claim to its evidence.** A stronger claim needs stronger proof:

| Claim | Requires | Not sufficient |
| --- | --- | --- |
| Tests pass | the test command's output, 0 failures | a previous run, "should pass" |
| Build succeeds | the build's exit code and warning count | tests passing, "the log looked fine" |
| Bug fixed | the original symptom reproduced and now gone | the code changed, "looks right" |
| Regression test works | observed red then green | the test passing once |
| Subagent finished | the actual diff and its own verification | the subagent's report of success |
| Requirements met | a line-by-line check against the ask | tests passing |

**Distrust agent success reports, including your own.** A subagent reporting success is a claim, not
evidence: read the diff, run the verification, and report what you found. This applies equally to a
conclusion you reached earlier in the session.

**An unverified gap is worth more than a confident claim.** "Not verified, and here is why" is a
useful result. "Should be fine" is not.

**Cross-reference the project ladder rather than duplicating it.** What counts as evidence for this
repository — the build gate, the three kinds of proof, and the four false equalities ("build
succeeded" ≠ done, "tests passed" ≠ GUI correct, "screenshot exists" ≠ reviewed, "synthetic input" ≠
hand-feel) — is in `muralis-verification`. The build/test specifics are in
`muralis-build-verification`. This skill is the gate that makes you go and *use* them.

## Forbidden patterns

- "Should work", "probably", "seems to", "looks correct", "that should do it".
- Expressing satisfaction before verification.
- Committing or reporting while a verification is unrun or failing.
- Trusting a subagent's or your own earlier "success" without checking.
- Partial verification presented as complete ("the first suite passed").
- Claiming a visual or hand-feel result you did not obtain.
- Weakening, skipping, or editing a check so the claim becomes true.
- "Just this once" — there is no exemption.
- Re-wording a claim to slip past the rule.

## Rationalization prevention

| Excuse | Reality |
| --- | --- |
| "It should work now" | Run the verification |
| "I'm confident" | Confidence is not evidence |
| "Just this once" | No exceptions |
| "The linter passed" | A linter is not a compiler |
| "The subagent said it succeeded" | Verify independently |
| "I'm tired / out of time" | Neither changes the state of the work |
| "A partial check is enough" | It proves nothing about the rest |
| "Different words, so the rule doesn't apply" | Spirit over letter |

## Relevant architecture / files

- `muralis-verification` — the evidence ladder and what each kind of claim requires.
- `muralis-build-verification` — the gate, the toolchain traps, and why an incremental build is not
  evidence here.
- `tools/phase4d-verify.ps1` — the build/test command to run.
- `muralis-code-review` — the review pass that follows a completion claim.

## Required verification

For every claim you make, be able to answer:

1. What did I run or observe, in this turn?
2. What did it output — exit code, counts, and the specific lines that support the claim?
3. Is the artifact I tested the one I just built?
4. Which parts of the claim does this *not* cover, and have I said so?

State the claim and its evidence together, and name the gaps plainly.

## Stop / escalation conditions

- You cannot identify a command that would prove the claim → stop; you either have the wrong claim
  or the wrong understanding of the work.
- The verification fails → report the failure. Do not report partial progress as success.
- The only way to make the claim true is to relax a check → stop and report the failure instead.
- The claim concerns appearance, aesthetics or hand-feel → stop; that needs a human, and until then
  it is *pending* (`muralis-verification`).
- You are about to accept a subagent's success report → stop and verify it yourself first.
- You notice you have already written or implied a success claim without evidence → correct it
  immediately and explicitly; a retracted claim is recoverable, a false one is not.
