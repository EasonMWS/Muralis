---
name: muralis-design-system
description: Use when changing anything visual or stylistic - colours, theming, light/dark, spacing, radius, typography, materials/glass, icons, animation timings, a new page or control, or a visual bug like a wrong colour in dark mode. Also use before hardcoding a colour, adding a style, or building a new button/card instead of reusing a reference control.
---

# Muralis design system

## When to use

- Any colour, brush, theme, spacing, radius, typography or elevation decision.
- Building or restyling a page, card, button, dialog or any control.
- Glass / material surfaces, shadows, edge highlights.
- Adding animation or choosing a duration.
- A visual defect, especially one that only appears in dark mode.

## Core rules

**Consume the foundation; do not extend it.**
Feature views use semantic resources — `MuralisAccentPrimaryBrush`, `MuralisSurfaceMediumBrush`,
`MuralisRadiusS`, `MuralisSpace16` — and never invent local colours or tokens. The dock's XAML, for
example, is guarded against both hex literals and new `x:Key="Muralis…"` definitions.

**Semantic resources resolve through theme dictionaries.**
Colour *values* live in the dark and light dictionaries; the root dictionary exposes stable
semantic names that point at them. The two sets of keys must match exactly, so a token added for one
theme must be added for the other.

**Colours are consumed with `{ThemeResource}`, never `{StaticResource}`.**
A static colour reference is resolved once and then refuses to follow a theme change. Static is
correct only for genuinely theme-invariant values such as radius, spacing and elevation — and for
scalar non-colour tokens.

**Spacing tokens are scalars, not thicknesses.** Use the dedicated inset/thickness tokens for
`Margin` and `Padding`; assigning a scalar spacing token to a thickness property is a guard
violation. Page-level insets have their own token.

**Reuse the reference controls.** They exist so that a new page does not become a third
implementation of a button:

| Control | Variants / purpose |
| --- | --- |
| `MuralisButton` | Primary, Secondary, Ghost |
| `MuralisIconButton` | icon-only action |
| `MuralisCard` | Default, Interactive, Selected |
| `MuralisNavigationItem` | navigation entry |
| `WallpaperCard` | wallpaper tile |
| `DockIcon` | dock item, with an exposed motion target |
| `GlassSurface` | Low / Medium / High material level |

If a page needs something these cannot do, extend the reference control rather than forking it.

**Material levels differ in depth, not in blur.** Low/Medium/High vary opacity, border visibility,
shadow elevation and edge highlight. There is **no acrylic or blur material in this app** — the only
backdrop brush is the main window's Mica, and each glass level also sets a Z translation depth in
code because `Translation` is a rendering property that a style setter cannot carry. Do not apply
blur everywhere as a shortcut to "premium", and do not add an acrylic brush.

**In code, fetch tokens from application resources; do not build brushes.** The established pattern
is an application-resource lookup for a brush or style by its semantic key. Constructing a
`SolidColorBrush` in code bypasses theming and is guarded against in the theme service.

**Theming has two halves and both must be honoured.**
Colour values live in theme dictionaries keyed for light and dark. On top of that, every window root
is registered with the theme service so it gets an explicit, synchronized `RequestedTheme` — because
the brush objects are shared application-scoped instances, and letting a second window resolve them
under a different theme recolours the main window. Register a new window root; do not rely on the
default.

**Typography and icons are already decided.** One font family, six type styles
(Display / TitleLarge / Title / Body / BodySmall / Caption) with foregrounds tied to the text
brushes. Icons are icon-font glyphs via `FontIcon` with hex character entities — no `SymbolIcon`, no
shipped icon font, and no per-icon `FontFamily`. A glyph that varies by state is bound, with the
mapping in code.

**Motion durations are shared tokens, not local choices** — see `muralis-motion-engine` for which
token source owns which concern, so you do not pick the wrong one.

**Motion uses the shared durations** — Fast 120 ms, Standard 180 ms, Slow 280 ms — defined once as
tokens and mirrored in the motion code. Animate transforms and opacity, which the compositor can
handle; do not animate layout properties, which forces a layout pass per frame. Respect the
system's reduced-animation setting rather than animating regardless.

**The visual direction is premium, calm, cinematic, layered, restrained.**
Depth comes from layering, subtle edge highlights and one accent used sparingly — not from volume.
Specifically not: large flat saturated colour fields, RGB glow, oversized gradient buttons, or cards
that flood with colour. Accent belongs on a state indicator or a single accent edge, not across a
surface. Anime atmosphere comes from the wallpaper, theme and mood — not from decorating controls.

**Text comes from resources, in both languages.** No literal user-facing strings in XAML or code;
every key must exist in every supported language file with the same key set.

**Icon glyphs come from the icon font, not embedded bitmaps**, unless the image is itself the
content (a wallpaper, an app's own icon).

## Forbidden patterns

- A hex colour literal in a view, style, shared control or material dictionary.
- Defining a new `x:Key="Muralis…"` token outside the token layer.
- `{StaticResource}` for anything colour-valued in a page or view.
- Assigning a scalar spacing token to `Margin` or `Padding`.
- A second button/card/nav-item style invented per page instead of using or extending the reference
  control.
- Per-page brushes, or setting `Foreground`/`Background` to a hand-built brush instead of a token.
- Animating `Width`/`Height`/`Margin`/`Padding`, or animating a layout-affecting property for a
  hover or entrance.
- Hardcoded user-facing text; a new string key added for only one language.
- Blur/acrylic applied indiscriminately rather than choosing a material level.
- Saturating a surface with the accent colour, or introducing neon/glow styling.
- Copying a specific visual parameter from a one-off design pass into a shared style as if it were a
  token.

## Relevant architecture / files

- `src/Muralis.App/UI/Tokens/` — `Colors.xaml` (+ `.Dark.xaml`, `.Light.xaml`), `Typography.xaml`,
  `Spacing.xaml`, `Radius.xaml`, `Elevation.xaml`, `Motion.xaml`. The source of truth for tokens.
- `src/Muralis.App/UI/Materials/GlassSurface.cs`, `Materials.xaml` — material levels.
- `src/Muralis.App/UI/Controls/` — `Controls.xaml` and the reference controls.
- `src/Muralis.App/UI/Motion/InteractionMotion.cs` — shared motion helpers and durations.
- `src/Muralis.App/UI/Playground/DesignPlaygroundPage.xaml` — the living reference for tokens and
  control states; hidden from production navigation.
- `src/Muralis.App/Services/ThemeService.cs` — theme application; roots must be registered so every
  window follows.
- `tests/Muralis.Core.Tests/Architecture/DesignFoundationTests.cs` — the executable token rules.
- `docs/design-foundation.md` — foundation overview (trust it for tokens and control names).

## Required verification

1. `./tools/phase4d-verify.ps1` — the design foundation guards run with it
   (`muralis-build-verification`).
2. Check **both** themes. A colour that works in one theme proves nothing about the other; the
   matching-key-sets rule exists because a token missing from one dictionary fails only there.
3. Check a real surface, not just the playground: theme resources resolve per element tree, and a
   shared brush object can leak one theme into a window that did not set its own requested theme.
4. Confirm any new string exists in all language files.
5. Visual acceptance of appearance is a human judgement. State it as pending rather than asserting
   it (`muralis-verification`).

## Stop / escalation conditions

- The change needs a colour or token that does not exist → stop and add it to the token layer
  properly (both themes, semantic name), rather than inlining it at the use site.
- A theme bug turns out to be **one shared brush object leaking across windows** rather than a
  missing token → stop and treat it as a theme-architecture problem. Patching the visible surface
  leaves the same leak on every other surface.
- A page needs a control the foundation does not have → stop and decide whether it is a new
  reference control or a variant; do not fork a private style.
- You are about to hardcode a value "just for this layout" → stop; if it is genuinely
  one-off geometry it is not a token, but if it is colour, spacing or timing it belongs in the layer.
- Something looks wrong and the only fix you can see is a bigger visual effect → stop; the direction
  is restraint, so the answer is usually less, not more.
