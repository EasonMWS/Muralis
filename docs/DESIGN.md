# Muralis design language

What Muralis looks like, and why. This document describes the **intent and semantics** of the visual
language.

It is not the token inventory — that is [`design-foundation.md`](design-foundation.md) and the token
files themselves. It is not a record of which pages have been migrated, and it is not a status
report. Where this document and the code disagree, **the code is right**; report the discrepancy
rather than trusting this file.

Enforcement status is marked throughout: **[enforced]** means an architecture test fails if the rule
is broken; **[intent]** means it is a design decision that is not machine-checked.

---

## 1. Personality

Muralis is **premium, calm, cinematic, layered, restrained — and alive.**

- **Premium**, not luxurious. Quality shows in alignment, spacing rhythm and consistency, not in
  ornament.
- **Calm.** The interface does not compete for attention. It is a frame around the user's wallpaper,
  not a second thing demanding to be looked at.
- **Cinematic.** Atmosphere comes from the imagery and the depth of layered surfaces, not from
  saturated colour.
- **Layered.** Hierarchy is expressed by depth — what sits above what — rather than by shouting.
- **Restrained.** One accent, used sparingly. Nothing glows.
- **Alive**, not busy. Motion confirms and explains; it does not perform.

What it is deliberately **not**: playful, loud, neon, skeuomorphic, dense with chrome, or trendy for
its own sake. Its anime atmosphere comes from the wallpaper, the theme and the mood — never from
decorating controls with motifs.

## 2. Where the language lives

| Concern | Owner |
| --- | --- |
| Token names, values, merge order, theme dictionaries | [`design-foundation.md`](design-foundation.md), `src/Muralis.App/UI/Tokens/` |
| Visual intent, semantics, anti-patterns | this document |
| Normative rules an agent must follow | the `muralis-design-system` skill |
| How a surface is judged | the `community-interface-review` skill |

## 3. Colour semantics

Colour is organised by **role**, not by appearance. A value never means "blue"; it means "this is the
accent" or "this is a raised surface".

| Role family | What it is for |
| --- | --- |
| Background | The window backdrop: the deepest layer, the page itself. |
| Sidebar | The navigation column, distinct from the content backdrop. |
| Surface (low / medium / high, plus hover / pressed / selected) | Raised content. The level states how far above the backdrop an element sits. Selected is a state, not a decoration. |
| Text (primary / secondary / tertiary / disabled) | The text hierarchy. Level carries importance; `disabled` is a state. |
| On-image | Text or icons drawn over imagery, where the normal text roles would not be legible. |
| Border (subtle / normal / hover / active) | Structure and separation, and interaction feedback. |
| Edge highlight | The thin top edge that reads as a light source above a raised surface. It is what makes a surface feel physical. |
| Accent (primary / secondary / foreground) | **Emphasis only.** |
| Scrim | Dimming layer behind imagery or overlays, in several strengths. |
| Glass (low / medium / high, each with a top and bottom tone) | Translucent layered surfaces; see §8. |
| Theme tints (`SurfaceTint`, `GlowTint`, `WallpaperMood`) | Theme-level mood inputs, so a future theme package can shift the whole product centrally. |

**Rules**

- **[enforced]** A colour value lives in the light and dark dictionaries with matching key sets; a
  role added for one theme must be added for the other.
- **[enforced]** Colour is consumed as a brush through a theme resource, never as a static resource,
  in any themed surface.
- **[enforced]** Shared control and material dictionaries contain no raw colour literals.
- **[enforced]** The primary button uses its own role, not the accent brush directly — the accent as
  a flat background is not the same thing as a deliberate button surface.
- **[intent]** **Accent is spent sparingly.** It belongs on a state indicator and on a single accent
  edge. It is never a large fill, never a background for a whole surface, and never used to make a
  section feel more important than its content warrants.

## 4. Dark and light

Both themes are first-class. Neither is the "real" design with the other derived mechanically.

- Dark is the default resolution, which suits a wallpaper-driven product.
- Every role exists in both, with the same semantic meaning; only the values differ. Light is not
  "the dark theme lightened" — the relationships (what is raised, what recedes) are preserved while
  the values are chosen for that theme.
- **[enforced]** The two dictionaries must contain the same keys.
- **Consequence of the shared-brush structure:** colour brushes are shared, application-scoped
  objects. A window that does not set its own requested theme can resolve them under the system theme
  and visibly recolour another window. **[enforced]** Every window root is registered with the theme
  service so it carries an explicit, synchronised theme context. A new window must register; a
  surface that looks correct in isolation is not evidence that it is.
- **[intent]** Because brushes are shared, a surface that must not shift with theme — a preview of the
  desktop, for instance — should pin an explicit surface role rather than relying on a material.

## 5. Typography

One family, six styles, no exceptions.

| Style | Size / weight | Foreground |
| --- | --- | --- |
| Display | 32 SemiBold | text primary |
| TitleLarge | 24 SemiBold | text primary |
| Title | 18 SemiBold | text primary |
| Body | 14 Regular | text primary |
| BodySmall | 13 Regular | text secondary |
| Caption | 12 Regular | text tertiary |

**Rules**

- **[intent]** Size *and* foreground together carry the hierarchy. A caption is both smaller and
  quieter; using size alone flattens the hierarchy.
- **[intent]** Sizes come from the styles, never set ad hoc. If a surface needs a size that does not
  exist, the hierarchy is wrong, not the scale.
- **[intent]** Long-form text stays within a comfortable measure; the product's body styles are sized
  for interface copy rather than for reading paragraphs.
- **[intent]** Truncation must be deliberate — labels in constrained containers trim rather than wrap
  unpredictably. See §12 on text expansion.

## 6. Spacing and rhythm

- **Scalar scale** (`MuralisSpace*`): separation between elements, and internal gaps.
- **Thickness insets** (`MuralisInset*`, `MuralisPageInset`): padding and margins.
- **[enforced]** A scalar spacing token may never be assigned to a margin or padding — thicknesses
  are a different type for a reason, and the guard exists because the mistake is invisible in review.
- **[intent]** Rhythm means the same relationship gets the same spacing everywhere. Uneven spacing is
  the most common reason a screen reads as unfinished, and it is invisible in a code diff.

## 7. Radius

A small scale, each step signalling the element's class: extra-small through small for controls and
inputs; medium for surfaces and cards; large for prominent containers; and a pill for fully rounded
elements such as indicators and the drop marker.

**[intent]** Radius is not decoration. A radius that does not come from the scale, or that varies
between elements of the same class, reads as carelessness.

## 8. Elevation and material

- **Elevation** is a small scale of shadow depths, with matching shadow resources. Higher means
  closer to the viewer, and is reserved for things that genuinely float above their surroundings —
  menus, overlays, a dragged item.
- **Material levels** (Low / Medium / High) are translucent surfaces for layering. They differ in
  opacity, border visibility, shadow elevation and edge highlight — and each also carries a Z
  translation depth.
- **[intent]** **There is no blur and no acrylic in this product.** Depth comes from opacity,
  borders, elevation and the edge highlight. Do not introduce a blur material.
- **[intent]** Glass is for separation, not for covering everything. A screen where every surface is
  glass has no hierarchy left — the material has stopped meaning anything.
- **[intent]** Edge highlights are what sell the material. Removing them flattens the depth that the
  whole language depends on.

## 9. Cards, navigation, buttons

Reference controls exist so a new page does not become a third implementation of a button.
**[enforced]** they exist at known paths and the type names are stable.

- **Buttons** — three variants: **Primary** for the one main action on a surface, **Secondary** for
  alternatives, **Ghost** for low-emphasis and in-line actions. One primary action per view.
- **Icon button** — a square control for a single icon-only action.
- **Card** — Default (presentational, inert), Interactive (clickable), Selected (current). Only
  Interactive and Selected behave like controls.
- **Navigation item** — a sidebar entry with a selected state. Selection follows the actual route,
  never a remembered flag. **[enforced]**
- **Wallpaper card** — the content card for imagery: thumbnail, scrim, title and metadata, favourite
  and selection states.
- **Glass surface** — the material container described in §8.

**[intent]** A new page composes these. Extending a reference control and forking a private style are
not equivalent choices; the second one is how a design system dies.

## 10. Dock

The Dock is a **desktop-experience layer**, not a floating HUD.

- **[enforced]** It is a passive tool window: never always-on-top, never focusable, no taskbar or
  Alt+Tab entry. Ordinary windows are expected to cover it.
- **[intent]** Its default appearance is transparent — it lets the wallpaper through rather than
  laying a bar across the desktop. A glass background is an opt-in appearance, not the default.
- **[intent]** The dock is docked: its bottom edge does not move. When it needs more room for
  magnification, it grows upward and sideways.
- **[intent]** **It must never read as a cheap RGB gaming bar.** No glow, no neon, no saturated rail,
  no lighting effects on icons. It is a quiet, precise strip of applications.
- **[intent]** The labels and indicators are secondary to the icon artwork; the icons carry the
  surface.

## 11. Hero

The Home hero is the **flagship surface** — the entry point to the desktop experience.

- **[intent]** It is an experience invitation, not a settings card. Configuration belongs on the
  settings page; the hero offers actions, not switches.
- **[intent]** The hero states the current mode and offers at most a small number of verbs. It does
  not present a matrix of options.
- **[intent]** Capabilities that do not exist yet are labelled as such honestly, and must not be
  styled to look like disabled controls — an unavailable button that is really a label misleads about
  what is interactive.

## 12. Motion personality

Motion is **quick, purposeful and restrained** — and it follows the hand.

- **[enforced]** Anything tracking the pointer is direct and 1:1. No spring, no lerp, no smoothing, no
  fixed-rate ticker on the active path.
- **[intent]** Easing is reserved for things that are *not* the pointer: settling, releasing,
  entering, leaving. Duration tokens are shared and short (fast / standard / slow). Motion is felt as
  responsiveness, not as animation.
- **[intent]** Motion explains a change of state or confirms an interaction. It never decorates, and
  there is no ambient animation for its own sake.
- **[enforced]** Layout properties are never animated; motion is transform and opacity.
- **[enforced]** The system's reduced-motion preference is honoured.

## 13. Visual anti-patterns

The specific things this product does not do:

- Large flat saturated colour fields; accent as a background.
- Glow, neon, or RGB styling, anywhere — especially the Dock.
- Oversized or decorative gradients, and gradient used as a substitute for hierarchy.
- Glass or material applied to every surface until nothing is foreground.
- Cards nested inside cards inside cards.
- A row of pill chips as the answer to every grouping problem.
- Arbitrary radii, spacing or type sizes that bypass the scales.
- Even emphasis everywhere: two things competing to be primary is a hierarchy failure.
- Large empty space with no hierarchy — space is only generous when something is clearly primary.
- Decorative texture — noise overlays, mesh backgrounds, ornamental borders.
- The interface competing with the wallpaper by glowing on its own.
- Half-finished migration: a surface that has not adopted the foundation sits next to one that has,
  and the mismatch is visible as inconsistency.

## 14. Accessibility

- **Theme completeness.** Every role resolves in both themes; a surface that only works in one is
  unfinished.
- **Contrast.** The text hierarchy is designed to step down in emphasis, not to become unreadable.
  Reaching the tertiary level should still be legible; if a design needs less contrast than a role
  provides, the role is wrong, not the value.
- **Never colour alone.** Selection, error, running and favourite states must be distinguishable
  without relying on hue — by shape, icon, position or weight as well.
- **Reduced motion.** Honoured as a first-class preference, not an afterthought.
- **Target sizes.** Controls are generous enough to hit comfortably with a mouse and remain usable at
  high DPI.
- **Text expansion.** Every visible string is localised. A layout must survive a longer translation
  without clipping, overlapping, or losing the primary action — and must survive the narrowest
  supported window width.
- **Contrast is computed, not eyeballed.** Where a contrast claim matters, compute it from values
  rather than judging from a screenshot.

## 15. Where to look

- `src/Muralis.App/UI/Tokens/` — colours (and their theme dictionaries), typography, spacing, radius,
  elevation, motion.
- `src/Muralis.App/UI/Materials/Materials.xaml`, `GlassSurface.cs` — the material levels.
- `src/Muralis.App/UI/Controls/` — the reference controls and their variants.
- `src/Muralis.App/UI/Playground/DesignPlaygroundPage.xaml` — every token, material level and control
  state in one place; the fastest way to see the language.
- `src/Muralis.App/Views/HomePage.xaml` — the flagship surface.
- `src/Muralis.App/UI/Dock/DockHost.xaml` — the Dock.
- `tests/Muralis.Core.Tests/Architecture/DesignFoundationTests.cs` — which of the rules above are
  actually enforced, and how.
- `docs/screenshots/` — reference captures.

---

## What this document is not

- Not the token values or the merge order — that is `design-foundation.md`.
- Not a migration or phase status. Which pages have adopted the foundation changes constantly and does
  not belong here.
- Not a style guide for other products, and not an invitation to redesign. Changing this language is a
  deliberate design decision that starts with the token layer, not with this file.
