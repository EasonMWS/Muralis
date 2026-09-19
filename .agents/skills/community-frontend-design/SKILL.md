---
name: community-frontend-design
description: Use when designing or building a UI surface - a new page, a card, the Home hero, a dialog, a settings section, or a redesign of an existing screen - and before writing any layout or style code. Use to decide hierarchy, focal point, composition, density and restraint first, so the implementation is designed rather than assembled.
---

# Frontend design

Methodology adapted for Muralis from the `frontend-design` skill in
[block/agent-skills](https://github.com/block/agent-skills) (Apache-2.0). See
`.agents/skills/THIRD_PARTY.md`.

**The community skill is web-oriented and its central instruction is to pick an extreme aesthetic
and avoid the obvious choice.** That instruction is deliberately inverted here: Muralis has an
established, token-enforced visual identity, and an agent's job is to execute it well, not to
re-invent it. Losing the framework coupling was necessary; losing the design *thinking* was not.

## When to use

- Starting any new UI surface, or substantially reworking one.
- Deciding layout, hierarchy, density, emphasis, or the focal point.
- Before writing XAML for something visual — design first, then implement.
- When a screen is functional but feels wrong and you cannot say why.

Not for: token values, theming mechanics, or style-guide questions. Those are
`muralis-design-system`. This skill is about designing the *thing*, within that system.

## Core rules

**Design before you build.** Before writing layout code, decide and be able to state:

- **Purpose** — what problem does this screen solve, and who uses it? What is the one task it exists
  for?
- **Focal point** — what should the eye land on first, and does the layout actually deliver that?
  There must be exactly one primary thing.
- **Hierarchy** — what is primary, secondary, tertiary? If two things are equally prominent, one of
  them is wrong.
- **Density** — how much content belongs on this surface at once, and what is deferred? Restrained
  density is the product's character; cramming is not premium.
- **Composition** — how do the elements group, and what is the reading order? Grouping should follow
  meaning, not convenience.
- **Interaction affordance** — can a user tell what is clickable, what is selected, and what is
  merely information? Ambiguity here is the most common real defect.
- **Constraints** — the platform (Windows desktop), the window sizes that must work, both themes, and
  every supported language.
- **Differentiation** — what makes this screen recognisably Muralis rather than a generic desktop
  app? Usually: restraint, depth through layering, and the wallpaper supplying the atmosphere.

**The design system outranks your taste.** Token names, not values. Reference controls, not new ones.
If you find yourself wanting a new colour, font or spacing scale, stop — that is a design-system
change (`muralis-design-system`), not a page decision.

**Match implementation complexity to the design.** A restrained design needs precision — careful
alignment, deliberate spacing rhythm, consistent sizing, correct hierarchy — not more effects. Doing
"premium" by adding glow, blur or gradient is the characteristic failure. Elegance comes from
executing a simple thing exactly.

**Depth comes from layering, not from decoration.** Material levels, edge highlights and elevation
exist for hierarchy. Use one level where a surface needs separating, and none where it does not.

**Motion is restraint.** Animate to explain a transition or confirm an interaction, never to decorate.
Follow the pointer directly; ease only states that are not the pointer (`muralis-motion-engine`).

**Design the states, not just the resting view.** Empty, loading, error, overfull, long-label,
truncated, disabled, focused, narrow window. A screen is not designed until its awkward states are.

**Both themes are first-class.** Design in one and translate to the other; never assume one is the
real one.

**Say what you chose and why.** A design decision that cannot be explained in a sentence is usually
decoration. Being able to justify the focal point, the grouping and the density is part of the work.

## Forbidden patterns

The generic AI-interface failure modes, and the Muralis-specific ones:

- **Cards inside cards inside cards** — nesting surfaces until nothing has precedence.
- **A row of pill chips** used as the answer to every grouping problem.
- **Large random gradients**, and gradient as a substitute for hierarchy.
- **Neon or glow everywhere**; RGB styling; the Dock reading as a gaming bar.
- **Glass on every surface** — when everything is glass, nothing is foreground.
- **Large empty space with no hierarchy** — space is only generous if something is clearly primary.
- **Every section equally prominent** — no focal point is a design failure, not neutrality.
- **Random rounded rectangles** — radii that do not come from the radius scale, or that vary without
  meaning.
- **The hero as a settings card** — configuration belongs on the settings page, not the flagship
  surface.
- **"Coming soon" styled like a disabled control** — it implies interactivity that does not exist.
- **Inventing a token, colour, font or spacing value** to solve a layout problem.
- **Copying a web or mobile layout idiom** into a desktop window because it looked good elsewhere.
- Choosing an "extreme" or unusual aesthetic direction for its own sake; novelty is not quality, and
  the identity is already decided.
- Judging your own design as finished without a review (`community-interface-review`,
  `community-screenshot-critique`).

## Relevant architecture / files

- `muralis-design-system` — the tokens, materials, reference controls and normative styling rules.
  Read it before designing; this skill assumes it.
- `docs/DESIGN.md` — the visual language and its semantics (`community-design-language`).
- `src/Muralis.App/UI/Tokens/` — the actual values.
- `src/Muralis.App/UI/Playground/DesignPlaygroundPage.xaml` — how the reference controls look in every
  state.
- `src/Muralis.App/Views/HomePage.xaml` — the flagship surface as built.
- `src/Muralis.App/UI/Dock/DockHost.xaml` — the Dock's layout and restraint.
- `src/Muralis.App/Controls/WallpaperCard.xaml` — a content card done with the foundation.

## Required verification

1. State the focal point and the hierarchy before implementing; check the built result delivers them.
2. `./tools/phase4d-verify.ps1` — the design guards run with it (`muralis-build-verification`).
3. Check both themes, and check the window at default, maximised, and a narrow width.
4. Check the awkward states, not just the populated one.
5. Confirm every visual value used is a token, and every control is a reference control.
6. A design is not verified by its author. Get a review (`community-interface-review`) and, for a
   visual change, a screenshot-based second opinion (`community-screenshot-critique`).
7. The final judgement of appearance is a human one — mark it pending rather than asserting it
   (`muralis-verification`).

## Stop / escalation conditions

- The design needs a token, colour or control that does not exist → stop and treat it as a
  design-system change, not a page-level workaround.
- You are about to add visual effects to make something feel more designed → stop; the problem is
  almost always hierarchy, spacing or grouping.
- You cannot state the focal point → stop; the screen is not designed yet.
- The screen is turning into a settings form, or settings are leaking onto a content surface → stop
  and decide what the surface is actually for.
- The design only works in one theme, or only at one window size → stop; it is not finished.
- Two elements are competing for primary emphasis → stop and resolve it before implementing.
- You are designing to look impressive rather than to serve the task → stop; the character is calm and
  restrained.
