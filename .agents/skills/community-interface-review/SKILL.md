---
name: community-interface-review
description: Use when an implemented or designed interface needs critique before it is called polished - reviewing a page, the Home hero, the Dock, a card, a dialog or a whole flow for hierarchy, spacing, typography, contrast, consistency, accessibility and visual noise. Use after implementing UI, before declaring it finished, and when asked "does this look good" or "what is wrong with this screen". Produces findings, not rewrites.
---

# Interface review

Methodology adapted for Muralis from the `review-ui-design` skill by ColourCloudSky
([review-ui-design-skill](https://github.com/ColourCloudSky/review-ui-design-skill), MIT). See
`.agents/skills/THIRD_PARTY.md`.

Its review discipline is framework-neutral and is kept here; its Figma tooling, Chinese-language
report template, and annotated-image generation are not applicable and have been replaced with the
project's own evidence channels.

## When to use

- A UI change is implemented and about to be called done.
- Someone asks whether a screen looks right, or what is wrong with it.
- A visual regression is suspected.
- Before a screenshot-based second review (`community-screenshot-critique`).

This is a **read-only critique**. It reports; it does not rewrite the UI.

## Core rules

**Purpose before polish.** Judge whether the screen achieves its product goal and communicates its
content first, then judge visual precision. A beautiful screen that hides the main task has failed.

**Respect the design system before your own taste.** For Muralis the authority order for any visual
question is:

1. the user's stated goal and business constraints;
2. the project's own design foundation and tokens (`muralis-design-system`) — this outranks any
   general visual opinion;
3. platform convention for a Windows desktop app;
4. accessibility requirements;
5. general design principles;
6. current style trends — **lowest**, and never a reason to change a token.

Popular style is not quality. Do not recommend a visual direction that contradicts the project's
established identity.

**Evidence priority, and no invented precision.** Judge from, in order: measured/verifiable
evidence, the design system's own rules, then what is plainly visible. **Never state an
unmeasurable pixel value, colour, or font property as fact.** If you cannot measure it, give a
relative relationship, a range, or the check that would settle it. Translate vague reactions into
diagnosable language — "doesn't look premium" becomes "the hierarchy gap is too small", "visual
weight is unbalanced", "spacing rhythm is irregular", "material and light source conflict".

**Four passes, in order:**

1. **Three-second impression** — what is the first focal point? Is the main task visible? Does the
   overall character match the product? Record this **before** analysis; do not reconstruct it later
   once you know what the design intended.
2. **Structure** — information hierarchy, grouping, reading order, density, key actions, and the
   flow across screens. The fastest test for a hierarchy problem is to blur your view (step back,
   squint, or view a small thumbnail): what still stands out is the real hierarchy, and if nothing
   stands out, there is no hierarchy.
3. **Detail** — form, colour, type, material, composition; then interaction and system consistency.
   Component inconsistency is the most common defect in interfaces assembled incrementally by
   developers rather than designed as a set — check the same control in several places.
4. **Counter-evidence** — for every candidate finding, ask: is this a mistake or a deliberate
   visual strategy? Is there a business, brand, platform or accessibility reason for it? Would the
   fix damage something that is currently working? Does the evidence actually support the claim?

The fourth pass is what separates a review from a list of opinions. **Do not manufacture findings
to fill the structure** — "insufficient evidence" is an allowed answer.

**Deduplicate by root cause.** The same spacing, colour, radius, icon or component problem is
reported once, with the affected locations listed. Do not split one token error into a dozen
findings.

**Severity and confidence are different axes. Keep them separate.**

- **Severity** — P0: blocks the core task, badly misleads, or is a clear accessibility failure.
  P1: materially damages hierarchy, readability, certainty of action, or cross-screen consistency.
  P2: does not impede use but hurts refinement, rhythm or brand expression.
- **Confidence** — *confirmed* (verifiable from a rule, a token, a measurement); *high* (plainly
  visible, little hidden information); *needs review* (affected by scaling, compression, a missing
  state, or missing intent).

A P0 you are unsure about is not the same as a P1 you are certain of. Say both.

**Separate externally-anchored findings from judgement.** When deciding between the top two severity
levels, ask what the finding rests on: if you can point to a specific external criterion — an
accessibility success criterion, a platform requirement, a documented rule — it is the higher
severity. If it is a usability or design-quality judgement, it belongs one level down. This keeps a
strong opinion from being reported with the same weight as a violated requirement.

**State your coverage gaps explicitly.** List what you could not assess and why — states not
captured, themes not seen, sizes not available, exact values not measurable. A reader needs to know
the boundary of the review as much as its contents.

**Every finding carries location, symptom, impact, action, and how to verify.** "Adjust it" and
"make it more premium" are not findings.

**Preserve what works.** State the strengths explicitly and check that your own suggestions would
not regress them. This is not flattery; it prevents the fix from costing more than the defect.

**Do not assert missing states you cannot see.** If hover, focus, pressed, loading, empty, error or
disabled were not visible, list them as unverified rather than absent.

**Lead with the conclusion and the top three.** Put the overall judgement, the strengths, and at
most the three most valuable changes at the front. The reader should be able to act after three
paragraphs.

**Do not inflate.** There is no quota. A good screen yields few findings. When there are many,
merge by root cause before reporting.

## Forbidden patterns

- Reporting taste as defect, or a defect as taste, without saying which it is.
- Stating an exact pixel value, hex colour or font metric you could not measure.
- Recommending a new colour, font, or visual language that conflicts with the design foundation.
- Turning the review into a rewrite. Findings only, unless rewriting was explicitly requested.
- Manufacturing findings to look thorough.
- Reporting the same root cause many times.
- Asserting a state is missing when it simply was not captured.
- Declaring a visual verdict from a file's existence — see `community-screenshot-critique`.
- Judging only the happy path; ignoring empty, loading, error and long-content cases.
- Ignoring accessibility because the screen "looks fine".
- Claiming a visual PASS that you are not able to perceive.

## Muralis-specific review targets

Check the product's own known risks, not just generic ones:

- **The Home hero should read as a flagship experience, not as a large settings card.** Look for
  settings-form affordances, toggles, or configuration rows that belong on the settings page.
- **The Dock must not read as a cheap RGB gaming bar.** Restraint is the identity; glow, neon, or
  saturated rails are regressions (`muralis-motion-engine`, `muralis-design-system`).
- **Feature summaries must not look like disabled controls.** A "coming soon" chip that resembles a
  greyed-out button misleads about interactivity.
- **Glass must not be everywhere.** Material levels are for depth and hierarchy
  (`muralis-design-system`); a screen where every surface is glass has no hierarchy left.
- **The wallpaper supplies the atmosphere.** The UI should not compete with it by glowing on its own.
- **Accent is spent sparingly** — a state indicator or a single accent edge, not whole surfaces.
- **Both themes must be judged.** A screen that only works in one theme is not finished.
- **Text must come from resources in every supported language**, and must survive expanded or
  truncated labels.

## Relevant architecture / files

- `muralis-design-system` — tokens, materials, reference controls, forbidden visual styling; the
  authority this review defers to.
- `docs/design-foundation.md` — the foundation as documented.
- `src/Muralis.App/UI/Tokens/` — the actual token values.
- `src/Muralis.App/UI/Playground/DesignPlaygroundPage.xaml` — the reference page for control states
  and material levels.
- `src/Muralis.App/Views/HomePage.xaml` — the flagship surface.
- `src/Muralis.App/UI/Dock/` — the Dock.
- `docs/screenshots/` — existing reference captures.
- `muralis-verification` — what counts as evidence, and the human-acceptance requirement.

## Required verification

- Every finding traceable to something you actually observed or measured.
- Every precise number either measured or labelled as a suggested range.
- A stated severity *and* confidence per finding.
- The strengths section written, and your suggestions checked against it.
- Unseen states listed as unverified rather than asserted missing.
- Both light and dark themes considered where the surface is themed.
- The final visual verdict: if you cannot perceive images, it is **HUMAN / VISION REVIEW PENDING**,
  never a PASS.

## Stop / escalation conditions

- The review is turning into a redesign → stop; that is a design task, and it needs the design
  system to change first, deliberately.
- A finding cannot be resolved without changing a token or the foundation → stop; escalate as a
  design-system change rather than patching the surface.
- Findings contradict the design foundation → the foundation wins; report the contradiction instead
  of recommending against it.
- You are asked to approve appearance and you cannot see images → stop and say so.
- The screen's problems are structural rather than visual (wrong information, missing state,
  unreachable action) → stop; that is an architecture or interaction issue, not a polish issue.
- You have more than a handful of P0/P1 findings → stop and check whether the real problem is one
  root cause, or whether the surface was built before its design was settled.
