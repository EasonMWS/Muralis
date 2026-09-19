---
name: community-design-language
description: Use when the project's visual language needs to be written down, explained, or checked for consistency - producing or updating docs/DESIGN.md, answering "what is our visual identity", deciding whether a new element fits the product, or onboarding a contributor to the design conventions. Also use before inventing any new visual pattern, to check whether one already exists.
---

# Design language extraction

## When to use

- `docs/DESIGN.md` needs creating, refreshing, or extending.
- Someone asks what Muralis's visual identity is, or whether something "fits".
- A new component, page or visual pattern is proposed and you need the conventions.
- Consistency across pages is in doubt.

## Core rules

**This skill extracts and documents. It does not design.** Its output describes the visual language
that already exists in the code. It is explicitly **not** a licence to invent colours, tokens, fonts,
spacing scales or a new direction. If the language seems wrong, that is a separate design task, and
it does not start here.

**Read the code before writing a word.** The tokens, materials and controls in the repository are the
fact. A design document that disagrees with `UI/Tokens/` is worse than no document. Sources to read,
in this order:

1. `src/Muralis.App/UI/Tokens/` — colours (and their dark/light dictionaries), typography, spacing,
   radius, elevation, motion. The authoritative values.
2. `src/Muralis.App/UI/Materials/` and `UI/Controls/` — materials and the reference controls.
3. `src/Muralis.App/UI/Playground/DesignPlaygroundPage.xaml` — the living reference for states.
4. `src/Muralis.App/Views/HomePage.xaml` and `UI/Dock/` — the surfaces that define the character.
5. `docs/design-foundation.md` — the existing foundation note (structure and intent).
6. `docs/screenshots/` — the captured appearance.
7. `muralis-design-system` — the rules and the forbidden styling.

**Distinguish the three layers, and keep them separate in the document:**

- **Foundation** — the token values and how they resolve. *What exists.* (`design-foundation.md`)
- **Design language** — the semantics and intent: why the surfaces, hierarchy and restraint are what
  they are. *What it means.* (`docs/DESIGN.md`, this skill's output)
- **Progress** — which page has been migrated. *What is done.* **Not** this document; that changes
  constantly and belongs elsewhere.

**Do not duplicate `design-foundation.md`.** It already owns the token inventory and merge order.
`DESIGN.md` should reference it and add the reasoning, the semantics and the anti-patterns — the parts
that are not visible from a list of names.

**Document what is true, and mark what is aspirational.** If a rule is enforced by a test, say so. If
it is intent without enforcement, say that too. Never present an intention as an implemented fact.

**No invented values.** Every colour, size, duration or radius in the document must be traceable to a
token, a test or a file. Use token *names*, not raw values, wherever the name is the thing a reader
should use.

## What `docs/DESIGN.md` contains

A document of roughly this shape — sections that do not apply can be omitted, but do not pad:

1. **Product visual personality** — the character in a few sentences, and what the product is *not*.
   For Muralis the established direction is premium, calm, cinematic, layered, restrained, alive.
2. **Where the language lives** — pointer to the token layer and the foundation doc, so the reader
   knows which document owns what.
3. **Colour semantics** — what each role means, not just what it looks like. Background, surface,
   text hierarchy, border, edge highlight, accent, scrim, glass. Which roles exist, what each is
   *for*, and where accent is permitted.
4. **Dark and light philosophy** — that both are first-class, that values live in matching theme
   dictionaries, and the shared-brush consequence (a window must have an explicit theme context).
5. **Typography hierarchy** — the six styles and, more importantly, when to use each; the single font
   family; how foreground relates to level.
6. **Spacing and rhythm** — the scalar scale versus the thickness insets, and which to use where.
7. **Radius system** — the scale and what each step signals.
8. **Surfaces and material** — the material levels, what each level is for, that depth comes from
   opacity/border/elevation/highlight rather than blur, and where glass is and is not appropriate.
9. **Cards, navigation, buttons** — the reference controls and their variants, with a line on each.
10. **Dock** — its role as a desktop-experience layer, its restraint, and what it must never look like.
11. **Hero** — the flagship surface's job: an experience entry point, not a settings form.
12. **Motion personality** — that motion is quick, restrained and purposeful; that follow is direct
    and easing is reserved for transitions; the shared durations.
13. **Visual anti-patterns** — the specific things this product does not do.
14. **Accessibility considerations** — theming completeness, contrast expectations, reduced motion,
    text expansion across languages.
15. **What this document is not** — explicitly: not the token values (foundation), not migration
    progress, not a style guide for other products.

## Forbidden patterns

- Inventing a colour, token, font, scale or direction and documenting it as if it existed.
- Replacing or restating `design-foundation.md` rather than referencing it.
- Copying web or mobile design-system idioms into a Windows desktop app's document.
- Recording migration or phase progress in the design language document.
- Documenting an intention as though it were implemented and enforced.
- Writing raw hex values or pixel numbers where a token name is the appropriate reference.
- Deriving the document from other design docs instead of from the tokens and the code.
- Presenting the document as authoritative over the code — the code is the fact.

## Relevant architecture / files

- Output: `docs/DESIGN.md`.
- `docs/design-foundation.md` — the implementation-level foundation this complements.
- `src/Muralis.App/UI/Tokens/*` — the values.
- `src/Muralis.App/UI/Materials/GlassSurface.cs`, `UI/Controls/*` — materials and controls.
- `src/Muralis.App/UI/Playground/DesignPlaygroundPage.xaml` — states reference.
- `tests/Muralis.Core.Tests/Architecture/DesignFoundationTests.cs` — which rules are actually enforced.
- `muralis-design-system` — the normative rules; `community-interface-review` — how to judge a surface
  against them.

## Required verification

1. Every value in the document traceable to a token, file, or test — no invented entries.
2. No contradiction with `UI/Tokens/` or with `muralis-design-system`. Where the code and a doc
   disagree, the code wins and the disagreement is reported rather than smoothed over.
3. Every claim about enforcement checked against the guard tests that exist.
4. Nothing in the document that will be false in a month (no counts, no progress, no "currently").
5. A reader who has never seen the project can use it to decide whether a new element fits.

## Stop / escalation conditions

- Documenting the language reveals the code and the intended direction genuinely disagree → stop and
  report the conflict; do not silently document one as the other.
- The task turns into "improve the design language" → stop; that is a deliberate design change, not
  documentation, and it needs the foundation changed first.
- There is no clear rule for something you must write about → say so in the document rather than
  inventing a convention that the code does not follow.
- A rule is enforced by a test in a way the document would contradict → stop; the test is the fact.
- You are tempted to record which pages have migrated → stop; that belongs in project status, not in
  the design language.
