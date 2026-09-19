# Dock product screenshots — visual review (fresh reviewer, screenshot-first)

Reviewer did not write the code. Images were inspected before any source was read.
Measurements below are pixel data extracted from the PNGs, not impressions.

## Verdict: REVIEW BLOCKED — HUMAN / VISION REVIEW PENDING

I *did* view all seven images and their pixel data, so this is not a "cannot see images"
disclaimer. The block is different: **the captures cannot answer the question they were
taken to answer.** Every one of the 7 PNGs is fully opaque (alpha = 255 on 100% of pixels,
RGBA colour type 6), with a pure-white field filling the entire frame. There is no
wallpaper visible anywhere in any capture, so "does the dock float on the wallpaper?" is
unanswerable from this evidence. Two of the three "state" captures are byte-identical
duplicates, so there is no hover evidence and no dark-mode evidence at all.

## Top three findings

1. **P0 — no transparency in any capture.** All 7 files: 0% of pixels have alpha < 255;
   background is pure (255,255,255) across the whole interior. Nothing in these images
   shows wallpaper through, behind or around the icons.
2. **P0 — the "hover" and "dark" captures are the same frame, not a state.**
   `03` is byte-identical to `02`; `07` is byte-identical to `05` (SHA256 match).
   No magnification and no dark theme was actually captured.
3. **P1 — `01` (0 apps) is a 76x116 near-blank frame** (399 bytes, one flat white field),
   and its window is 76px wide where content-fit sizing predicts 24px. Either empty-state
   sizing reserves a phantom slot, or it is an unrelated dock in a mode this capture
   does not identify.

## Measured geometry (FACT)

Window sizes: 76x116 (0 apps), 188x116 (3), 356x116 (6), 580x116 (10).
Both 188 and 356 hit `pinned*52 + gaps*4 + padding 12*2` exactly; 76 does not.

- 3 apps: content x19..172, left gap 19 / right gap 15 (asymmetry 4px). **Visibly compact.**
- 6 apps: content x19..337, gaps 19/18 (symmetric).
- 10 apps: content x19..559, gaps 19/20 (symmetric). NOT crowded; comfortably spaced.

Pitch (centre-to-centre), 10-app set:
`56, 56, 56, 38, 74, 56, 58.5, 53.5, 56`

- Left cluster icons 1-4: exactly 56px, identical x-positions in the 3-, 6- and 10-app
  captures. Character is preserved across app counts (answering Q9: yes, light mode).
- Icons 7-10 form a second cluster at 53.5-58.5px pitch with a 74px group gap.
- `pitch 38 + pitch 74 = 112 = 2 x 56`: the phantom-slot signature. Icon 5 in the 6- and
  10-app captures is an 8x8 blue blob (`28,144,237`) spanning only y55..62, vertically
  centred ~20px above every neighbour, with doubled air on both sides.

Vertical band: icons sit at y55..98 in a 116px-tall window (39% dead space above, 13px
below). Baselines are **not** common: bottoms land at 98, 90, 98, 98, 62, 98, 98, 94, 94, 94.

## Q&A

1. Icons floating on wallpaper — **cannot tell.** Opaque white field, no wallpaper. (FACT)
2. Dock-wide background panel — **yes, an opaque white one fills 100% of every frame.** (FACT)
3. Resembles a Windows taskbar — **no.** No bar, border, labels, or tray; icon strip only. (FACT)
4. Common baseline — **no.** Bottoms vary 62/90/94/98. (FACT)
5. Spacing calm and consistent — **mostly, but not fully.** 56px is calm and identical
   across app counts; the 38/74 pair is a visible lump. (FACT)
6. 3-app dock compact — **yes**, 188x116, uniform 56px pitch. (FACT)
7. 10-app dock controlled — **yes**, 580px, symmetric margins, no crowding. (FACT)
8. Leftover "+" tiles / labels / separators / utilities — **none visible.** Rightmost
   glyphs are recognisable app icons, not UI chrome. (FACT)
9. Same character across app counts — **in light mode yes**; icons 1-4 do not move. Dark
   mode **cannot be assessed** (duplicate file). (FACT)
10. `02`==`03`: **identical.** `04`==`05`: **different** (SHA256 differs; ~1 byte apart).
    `04`==`07`: **different.** But `05`==`07`: **identical, so "dark" is a copy of "hover".**
    The 04-vs-05 difference is ~1 byte and is not a perceivable hover state.

## Re-capture recipe (SUGGESTION)

Capture the dock over a non-white wallpaper, and re-shoot hover with the pointer visibly
parked mid-dock. A single full-screen capture per state would settle Q1 and Q2 in one shot.

## Corroboration from source (read only after forming the above)

`DockHost.xaml` comment claims the transparent style "paints nothing at all - no background,
no border, no shadow - so the wallpaper shows through". These captures show the opposite:
a window-sized opaque white surface. Either the captures were taken in the Glass style, or
the intended transparent style is not the default in the shipped build.
`DockIcon.xaml` sets `MinWidth=52`, giving a 56px pitch with `Spacing=4` — consistent with
icons 1-4. Neither the 76px empty state, the 8x8 blob, nor the 38/74 pitch is explained by
the XAML read; they remain unexplained from the visual evidence alone.
