---
name: community-screenshot-critique
description: Use after UI has been implemented and launched, to get a genuine second visual opinion from a reviewer that did not write the code - capture the running interface, hand the images to a fresh reviewer agent, and critique from the screenshot first. Use when the implementing agent is not an acceptable final visual judge, and whenever a visual change must be reviewed rather than assumed.
---

# Screenshot critique

## When to use

- UI was just implemented or changed, and someone must judge how it actually looks.
- A visual claim needs a second opinion from an agent that did not write the change.
- Before declaring a visual change complete.
- As the second half of the visual workflow: implement → launch → capture → fresh review → revise
  (see `docs/VISUAL-WORKFLOW.md`).

## Core rules

**The implementer is not the reviewer.** Whoever wrote the change knows what they intended, and that
knowledge is exactly what stops them seeing the result. The critique must come from an agent with no
memory of the implementation and no access to this conversation's reasoning.

**Screenshot first, code second.** The reviewer looks at the images *before* reading the source. Only
after recording what the image shows may it read code to explain a finding. Reading the
implementation first converts a review into a confirmation.

**Review the running application, not the markup.** Capture the real window from a real build. A
design that looks correct in XAML can still be wrong on screen (theme resolution, DPI, missing
resource, wrong state).

**A screenshot existing is not a review.** Saving a file proves nothing about how it looks. Never
report a visual PASS on the strength of a file's existence.

**If nobody can actually perceive the images, say so.** A text model that cannot reliably read images
must mark the verdict **HUMAN / VISION REVIEW PENDING** and hand over the artifacts. It must not
infer appearance from the code, from element bounds, or from having generated the screenshot. This is
the single most important rule in this skill.

**Separate visual fact from subjective suggestion.** Every finding is labelled as one or the other. A
fact is something plainly in the image or measurable from it. A suggestion is taste and should be
argued for, not asserted.

**Capture the states that matter, not just the pretty one.** For a Muralis surface this normally
means: default/resting, the interactive state under the pointer, the expanded or selected state,
light and dark, narrow and wide window, and any empty/loading/error state the surface has.

## The workflow

```
1. Build the change with the project gate            (muralis-build-verification)
2. Launch the real application
3. Drive it into the state to be judged              (Computer Use / real pointer input)
4. Capture the window to files, and verify each file exists and is non-empty
5. Dispatch a FRESH reviewer with the images and the question — no implementation detail
6. Reviewer records what the image shows, before reading any code
7. Reviewer produces findings, each labelled fact or suggestion, with severity and confidence
8. Implementer decides and fixes
9. Re-capture and re-review — a fix is not verified by the same eyes that made it
```

Step 5 is what distinguishes this from ordinary review. Give the reviewer the images and the intent,
not the diff. If the harness supports subagents, dispatch one; a fresh agent with only the images is
the point.

**For the review itself, use `community-interface-review`.** That skill owns the review discipline —
evidence priority, the four passes, counter-evidence, severity versus confidence, deduplication. This
skill owns the *workflow and the epistemics* of reviewing through screenshots. Do not duplicate its
checks here.

## Capturing honestly

- Use the project's own harness and Computer Use to drive the app; do not hand-build a mock.
- Capture the real window at a known size, and record that size, the theme, and the state you were in.
- Do not crop, scale or retouch the image before review; derive any zoomed inspection as a separate
  file and label it.
- Record what state the pointer and the app were in, so the finding can be reproduced.
- Verify every capture with an existence and size check, and report a failed capture as **unavailable**
  rather than describing what it would have shown.

## Forbidden patterns

- Reviewing your own implementation and calling it a visual review.
- Reading the implementation before recording what the screenshot shows.
- Announcing a visual PASS because screenshots were saved.
- Inferring appearance from code, element bounds, or geometry when no one can see the image.
- Describing what a screenshot "would show" when the capture failed.
- Retouching or cropping the evidence before review.
- Reporting suggestions as facts, or facts as preferences.
- Reviewing only the happy-path state and one theme.
- Treating a re-review by the same agent as independent verification.
- Accepting "looks good" with no specific observations behind it.

## Relevant architecture / files

- `community-interface-review` — the review discipline to apply to the captures.
- `muralis-verification` — the evidence ladder and the human-acceptance requirement.
- `muralis-design-system` — the standard the interface is judged against.
- `docs/VISUAL-WORKFLOW.md` — the end-to-end visual workflow.
- `docs/screenshots/` — existing reference captures for comparison.
- The project's Computer Use setup and the `tools/phase4d-*` harnesses for driving the app and
  capturing windows (`muralis-verification` names them).

## Required verification

Before reporting a critique as complete:

1. A fresh build was used, and the launched process is that build.
2. Each capture is confirmed present and non-empty, and its state/size/theme is recorded.
3. The reviewer demonstrably had no implementation context when it recorded its observations.
4. Every finding is labelled **fact** or **suggestion**, with severity and confidence.
5. The states not captured are listed as unverified.
6. The verdict is one of: PASS *(only if the reviewer genuinely perceived the images)*,
   **HUMAN / VISION REVIEW PENDING**, or FAIL with findings.

## Stop / escalation conditions

- No one involved can perceive the images → stop and mark human/vision review pending; do not
  substitute inference for sight.
- A capture fails or the app will not reach the state → stop and report the capture as unavailable.
- The reviewer cannot be made independent (no fresh agent, only the implementer available) → stop and
  label the result a self-review, which is weaker evidence, and say so.
- The findings are structural rather than visual → stop and escalate to
  `community-interface-review` / `muralis-architecture`.
- A finding requires a design-system change to resolve → stop and escalate
  (`muralis-design-system`).
- The same defect survives two review rounds → stop and diagnose it rather than re-reviewing a third
  time (`community-systematic-debugging`).
