# Visual workflow

The stable workflow for changing anything visual in Muralis. It exists because the agent that
implements a visual change is the worst judge of it, and because "it builds" says nothing about how
something looks.

This is a **process** document. It contains no page status and no design decisions — the design
language is [`DESIGN.md`](DESIGN.md), and the tokens are [`design-foundation.md`](design-foundation.md).

Skills referenced here are in `.agents/skills/`; each is a self-contained instruction set an agent
loads on demand.

---

## The loop

```
1. Design        decide hierarchy, focal point, density, states   →  community-frontend-design
2. Implement     compose existing tokens and reference controls   →  muralis-design-system
3. Build         the project gate, not an incremental build       →  muralis-build-verification
4. Launch        the real app from the built binary
5. Capture       drive it into the states that matter, screenshot them
6. Review        a FRESH reviewer, screenshot-first               →  community-interface-review
                                                                     community-screenshot-critique
7. Revise        implementer decides and fixes
8. Re-review     re-capture and review again — never self-approve
9. Accept        human acceptance for appearance and hand-feel    →  muralis-verification
```

## Why each step exists

**1. Design before code.** A screen assembled without deciding its focal point and hierarchy will be
internally consistent and still feel wrong. Deciding first is cheaper than discovering later.

**2. Compose, don't invent.** The token layer and the reference controls are the design system. A new
colour, font, spacing value or button style is a design-system change, not a page-level decision.

**3. Build with the gate.** This machine's incremental build can leave a stale assembly, so a screen
inspected from a stale binary can be a screen that no longer exists in source. Only the project gate
gives a trustworthy artifact.

**4–5. Look at the running thing.** The rendered result is the subject of the review. Theme
resolution, DPI, a missing resource and a wrong state are all invisible in XAML and obvious on screen.
Capture the states that matter: resting, interactive, selected/expanded, both themes, narrow and wide,
and any empty/loading/error state.

**6. A fresh reviewer, screenshot first.** The implementer knows what they intended, and that is
exactly what prevents them seeing what is there. The reviewer looks at the images **before** reading
the implementation, and records its first impression before any analysis. Both the review discipline
(`community-interface-review`) and the capture/review workflow (`community-screenshot-critique`)
apply.

**7–8. Fix, then look again.** A change verified only by the person who made it is not verified. Re-run
the same capture and review; do not assume the fix achieved what it intended.

**9. Human acceptance.** Appearance, polish and hand-feel are human judgements. Automated checks,
geometry and instrumentation can prove behaviour; they cannot prove that something looks right.

## The honest-reporting rules

These are the failure modes this workflow exists to prevent:

| Not evidence of | What people mistake it for |
| --- | --- |
| A build succeeding | the feature being finished |
| Unit tests passing | the interface being correct |
| A screenshot file existing | anyone having looked at it |
| Synthetic pointer input | human hand-feel acceptance |
| The implementer's own review | an independent review |
| Element bounds and geometry | how the screen looks |

**If the reviewing agent cannot perceive images**, the verdict is **HUMAN / VISION REVIEW PENDING**.
It is not a pass, and it is not a fail — it is a handover. The agent must say plainly that it could
not judge appearance rather than inferring it from code, bounds or intent.

Every review states what it did **not** cover: states not captured, themes not seen, window sizes not
tested, values not measurable.

## When to use the whole loop

Use the full loop for anything a user will look at: the hero, the Dock, a page, a card, a dialog,
theming, materials, motion.

Skip steps deliberately, and say which — a change that alters no visual output (a refactor, a data
fix) starts at step 3. A one-line copy change does not need a design pass or a fresh reviewer. The
loop is a tool, not a ritual.

## Not this document

- **Design decisions** — [`DESIGN.md`](DESIGN.md).
- **Token values and structure** — [`design-foundation.md`](design-foundation.md).
- **What counts as evidence for behaviour** — the `muralis-verification` skill.
- **Build and test commands** — the `muralis-build-verification` skill.
- **Which pages have been migrated** — project status, not here.
