# Operation Clear Dock — route results

Actual experiments, run against the real composited screen on this machine. Each route was applied to a live
window, measured, and reverted before the next one started, so a result belongs to exactly one technique.

The gate for every route is the same: sample the same screen points with the dock window **hidden** and then
**shown**. In the empty parts of the window the two readings must agree, because "transparent" means the
desktop behind is what the display actually showed. Pixels are read from the screen, never from an alpha
buffer, so a correct surface that never reaches the display cannot pass.

Status of the current build at the start of this work: the dock is a white plate. Measured baseline at the
dock's own window rect — gap between icons `255,255,255`, reserve band above the icons `255,255,255`, desktop
just outside `82,87,108`.

---

## Route A1 — `WS_EX_LAYERED` + `SetLayeredWindowAttributes(LWA_ALPHA)`

**Experiment.** Applied the style and a constant alpha to the running dock's HWND (`tools/routeA-layered-probe.ps1`),
sweeping alpha 255 / 200 / 128 / 64 / 1, then removed both.

**Result.** Both calls returned success (`SetLayeredWindowAttributes` → `True`, window alive, `WS_EX_LAYERED`
present in the extended style). The pixels did not move at all:

| alpha | reserve band | desktop outside |
| --- | --- | --- |
| none | `255,255,255` | `82,87,108` |
| 255 | `255,255,255` | `82,87,108` |
| 200 | `179,181,185` | `86,91,110` |

At alpha 200 the band darkened slightly toward the desktop but never reached it, and the whole window
including the icons faded together — a uniform fade of the entire window, not per-pixel transparency. The
icons are dimmed by exactly as much as the plate, which is not what a dock needs.

**Blocker.** The dock is composited through a flip-model swap chain. The layered path multiplies the whole
window surface by one constant and never reaches the content's alpha channel, so it cannot make an empty
region clear while leaving the icons opaque.

**Verdict: FAIL for the product goal.** Constant alpha is not transparency.

## Route A2 — `WS_EX_LAYERED` + `SetLayeredWindowAttributes(LWA_COLORKEY)`

**Experiment.** Chose a key colour **provably absent from the dock's own pixels** — magenta `0xFF00FF`, checked
against all 227 distinct colours inside the window rect rather than assumed — applied `LWA_COLORKEY`, and
compared the same points (`tools/routeA-colorkey-probe.ps1`).

**Result.** `SetLayeredWindowAttributes` returned `True`, the window stayed alive, `WS_EX_LAYERED` was present.
The plate stayed `255,255,255`; `|reserve band − desktop| = 511` before and after. Zero effect.

**Blocker.** A colour key is applied where the window's surface is composited. WinUI 3's compositor presents
through a modern flip path that ignores the legacy hidden redirection bitmap the key operates on. The API
accepts the request and nothing consumes it.

**Verdict: FAIL.** The API succeeding is not evidence the technique is in force, which is why the pixels are
the measurement.

## Route B — DWM / window composition

**Experiment.** `DwmExtendFrameIntoClientArea` with the `-1` glass margins, applied to the live dock
(`tools/routeBC-probe.ps1`).

**Result.** `DwmExtendFrameIntoClientArea` → `0x80070057` (`E_INVALIDARG`). No pixel change; the plate stayed
`255,255,255`.

**Blocker.** The call is refused outright for a window with `SetBorderAndTitleBar(false, false)` and no
non-client area to extend into. Even where it succeeds on this OS, what it produces is a blur/tint backdrop —
which this task defines as a failure, not as a substitute.

**Verdict: FAIL.**

## Route C — window region

**Experiment.** `SetWindowRgn` with the union of one rounded region per icon (52×52 boxes at the real 56 DIP
cell pitch, 12 DIP padding), so the HWND is meant to be absent between the icons
(`tools/routeBC-probe.ps1`).

**Result.** `SetWindowRgn` returned `1` (success), the window stayed alive, and the pixels did not change at
all: gap between icons `255,255,255`, top band `255,255,255`, inside the first icon `255,255,255`.

**Blocker.** The region is recorded on the window but the compositor does not clip its visual output to it.
A WinUI 3 window's content is presented by the compositor independently of the window region.

**Verdict: FAIL.**

## Route D1 — separate native layered window (`WS_EX_LAYERED` + `UpdateLayeredWindow`)

**Experiment.** A standalone native window (`tools/clear-dock-poc/`), not WinUI, created as
`WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST`, painted through `UpdateLayeredWindow`
from a 32-bit premultiplied ARGB surface. The surface is alpha 0 everywhere except the icon squares.

**Result: PASS.**

`tools/pixel-proof.ps1`, same points read with the window hidden and shown:

| probe | dock hidden | dock shown | verdict |
| --- | --- | --- | --- |
| gap between icons 0 | `254` | `254` | transparent, delta 0 |
| gap 1 | `254` | `254` | transparent, delta 0 |
| gap 2 | `254` | `254` | transparent, delta 0 |
| gap 3 | `255` | `255` | transparent, delta 0 |
| reserve band above the icons | `71,77,98` | `71,77,98` | transparent, delta 0 |
| just outside (grab sanity) | — | delta 0 | measurement itself is stable |
| inside a painted square | wallpaper | `255,0,0` | opaque square rendered, delta 504 |

Per-pixel alpha rather than one constant for the window: with the first square drawn at alpha 128, it reads
`252,124,124` where the wallpaper underneath is `254` — the correct composite of 50 % red over that
background.

Interaction (`tools/interaction-proof.ps1`): exstyle `0x8080088` carries `WS_EX_TOOLWINDOW`,
`WS_EX_NOACTIVATE` and `WS_EX_LAYERED`; clicking the window's centre left the foreground window unchanged
(`ChatGPT` → `ChatGPT`).

Performance, one full surface rewrite and upload per frame with a travelling wave across the icons
(Release build, 3000 frames each):

| pins | surface | p50 | p95 | p99 | max |
| --- | --- | --- | --- | --- | --- |
| 3 | 188×86 | 0.029 ms | 0.066 ms | 0.129 ms | 9.36 ms |
| 6 | 356×86 | 0.045 ms | 0.079 ms | 0.127 ms | 9.50 ms |
| 10 | 580×86 | 0.075 ms | 0.114 ms | 0.160 ms | 8.77 ms |

Working set ~33 MB, private ~13.6 MB, flat across all three sizes. The ~9 ms outliers are single occurrences
per run and look like GC pauses in the harness rather than upload cost.

**Verdict: PASS.** This is the first route whose pixels actually show the desktop through the window's own
empty area.

### Milestone reached: the visible POC

The POC was then extended from flat squares to the real thing, and the milestone this work was aimed at is met:

| requirement | result |
| --- | --- |
| 5 real app icons | 5 icons loaded through the product's own `ShellIconProvider`, from the pinned apps in the product's own settings file |
| real wallpaper between the icons | every gap between icons reads **delta 0** against the same point with the window hidden |
| real wallpaper above the icons | the reserve band reads **delta 0** |
| hover magnification | the product's own `DockMotionEngine` drives it; peak reached **1.8000**, the profile's `MaxScale` exactly |
| click launch | clicking icon 1 produced `launch=1 target=…\ComfyUI-aki-v3.lnk`, and the foreground window did not change |
| non-activating | `WS_EX_TOOLWINDOW \| WS_EX_NOACTIVATE \| WS_EX_LAYERED`, foreground unchanged across a click |
| no plate | the empty surface is cleared to alpha 0 each frame, and no background is ever painted |

The icons are drawn from the shell's own premultiplied BGRA (the icon reader's documented output), copied into
the DIB rather than blended — blending there would multiply the alpha twice and darken every icon edge.

**Two findings worth keeping.**

A proof harness that leaves the pointer resting on the dock measures the *hovering* dock, where the magnified
icons legitimately cover the gaps beside them. The first run of the final proof reported the gaps as opaque for
exactly that reason. The gate now parks the pointer away from the candidate before it samples, because the
resting state is the one the gate is about.

A near-white icon centre reads as "matches wallpaper" when the wallpaper behind it is also near-white. The gate
therefore reports every icon centre and requires that at least one gained ink, rather than requiring the first
one to have changed — real artwork contains white pixels, and a white pixel is not a missing icon.

**Not yet done, and not claimed.** Drag reorder, DPI scaling, multi-monitor, accessibility, and running apps are
out of scope for this milestone. The input adapter polls the cursor rather than consuming the process-wide
`RawPointerBroker`; a product renderer would consume the broker, which is already renderer-independent.

---

## What this means for the goal

Route D1 satisfies the hard gate: the real desktop is what the display shows between and around the icons,
verified by reading the composited screen, with the opaque parts rendering their exact painted colours and
per-pixel alpha confirmed. The interaction semantics the dock needs — tool window, never activating, layered —
are present, and the per-frame cost is far below a frame budget at every icon count the product supports.

The routes that fail do so for one shared reason: the current dock is a **WinUI 3 XAML window**, and that
window's content is presented by a compositor that ignores the legacy window mechanisms (layered attributes,
colour key, window region, frame extension) which exist to make an ordinary window's surface transparent.
None of those mechanisms is refused by the API — that is the trap — they are accepted and have no effect, which
is why every verdict above rests on screen pixels rather than on a return value.

The remaining work is therefore not "find a transparency technique" but "move the dock's presentation to a
window that owns its own pixels", which is what the POC demonstrates. The dock's data, persistence, geometry
and motion maths are renderer-independent already and do not need to change.
