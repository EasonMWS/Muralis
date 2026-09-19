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

This first run used the flat-colour stand-in at a forced five icons. The four "gaps" above are therefore four
probe points across a five-cell stripe, not four real inter-icon gaps. The counts were corrected when the probe
geometry was made adaptive (see *The gate that could not fail* below); the deltas, which are the actual
measurement, are unaffected.

Per-pixel alpha rather than one constant for the window: with the first square drawn at alpha 128, it reads
`252,124,124` where the wallpaper underneath is `254` — the correct composite of 50 % red over that
background.

Interaction (`tools/interaction-proof.ps1`): exstyle `0x8080088` carries `WS_EX_TOOLWINDOW`,
`WS_EX_NOACTIVATE` and `WS_EX_LAYERED`; clicking the window's centre left the foreground window unchanged
(`ChatGPT` → `ChatGPT`).

Performance, one full surface rewrite and upload per frame with a travelling wave across the icons
(Release build, 3000 frames each, current geometry — 52 px icons, so the surface is 106 px tall):

| pins | surface | total p50 | total p95 | total p99 | raster p95 | upload p95 | max | GC over 3000 frames |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 3 | 188×106 | 0.059 ms | 0.110 ms | 0.172 ms | 0.078 ms | 0.033 ms | 11.82 ms | 0 |
| 5 | 300×106 | 0.090 ms | 0.156 ms | 0.251 ms | 0.121 ms | 0.031 ms | 11.93 ms | 0 |
| 10 | 580×106 | 0.152 ms | 0.276 ms | 0.397 ms | 0.224 ms | 0.044 ms | 13.58 ms | 0 |

Working set ~35 MB, private ~14 MB, flat across all sizes. The cost tracks the surface area, not the icon count,
and even at ten icons a frame costs 0.28 ms at the 95th percentile — well inside a 16.7 ms frame budget at 60 Hz.

The ~10–11 ms maximum is **not** explained and is reported rather than dismissed: one occurrence per run, in
every configuration including the smallest, which is the signature of a single scheduling or GC pause in the
harness rather than a cost of uploading. It has not been made to reproduce on demand, so it stays an open
question. If it were real, it would be a dropped frame at whatever rate it occurred.

### The spike, narrowed: stage, cause of one component, and what remains

The paragraph above was written before the frame was instrumented. It now is, per frame and per stage, and the
per-frame allocation and collection counters are taken too. The findings, in the order they were established:

**It is in the raster stage.** Every run puts the worst frame on the *same* frame index — 1571 of 3000 — and
essentially all of it is raster: `total 13.531 ms` = `raster 13.432` + `copy 0.012` + `upload 0.085` +
`clear 0.002`. Upload, the operation the earlier note suspected, is at most 0.37 ms anywhere in any run.

**One component of it was a cache eviction, and that is fixed.** The artwork cache held 96 sizes while a wave at
100 % walks 43 sizes *per icon*; with five icons the cache evicted mid-wave and the frame that needed the evicted
size paid for a resample inside its own frame. Prewarming every reachable size at startup moved that cost off the
pointer path entirely, and the effect on the distribution is large and unambiguous:

| reading | before prewarm | after prewarm |
| --- | --- | --- |
| p99 total | 4.234 ms | 0.213 ms |
| max raster | 13.432 ms | 12.146 ms |
| GC collections over 3000 frames | 6 | **0** |
| allocated bytes over 3000 frames | 19.1 MB | **0** |

**What remains is one frame, and it is not a garbage collection.** Zero collections and zero allocated bytes
across the whole run rules that out, as does the stage split. Three consecutive runs put the worst frame on
index 1571 every time, which a random external stall would not do.

**The best hypothesis, tested and partly supported: deferred OS accounting.** 1571 frames at 0.08 phase per
frame is 1562 ms, and Windows charges a thread for its scheduling quantum — classically one 15.625 ms timer tick
— the first time it blocks. A loop that never blocks never pays it, and 1562 ms is where a 15.625 ms period
would first be felt. The test is direct: run the same 3000 frames with a blocking `Thread.Sleep(1)` inserted at
frame 1562. The stall should then be charged to the sleep rather than to a frame. Result: the worst frame **stayed
at 1571** and **halved**, from 11.7 ms to 6.0 ms. That is consistent with the charge being a fixed cost the
sleep absorbed part of, and it does not prove the mechanism.

**Classification: S7 — scheduler or external stall, residual, narrowed to the raster stage.** Not S5 (GC is
excluded by measurement), not S4 (upload is 0.085 ms in the worst frame), not S2 (the cache is prewarmed and the
counters show no misses during the run), not S6 (no resize happens). The honest summary is that the maximum is
*not* explained, but it is now bounded to a single stage, a reproducible frame index, and a plausible mechanism
that has been tested once and only partly confirmed. One occurrence per 3000 frames, in a benchmark loop that
never yields, is not the same thing as a frame drop in an interactive dock — and that distinction is a hypothesis
too, not a measurement.

**Verdict: PASS.** This is the first route whose pixels actually show the desktop through the window's own
empty area.

### Milestone reached: the visible POC

The POC was then extended from flat squares to the real thing, and the milestone this work was aimed at is met:

| requirement | result |
| --- | --- |
| 5 real app icons | ChatGPT · ComfyUI-aki-v3 · ComfyUI · Visual Studio Code · Steam, loaded through the product's own `ShellIconProvider`; **5 of 5 drawn**, 37 of 45 sampled points carrying ink |
| real wallpaper between the icons | all four inter-icon gaps read **delta 0** against the same point with the window hidden |
| real wallpaper above the icons | the reserve band reads **delta 0** |
| hover magnification | the product's own `DockMotionEngine` drives it; peak reached **1.8000**, the profile's `MaxScale` exactly, and all five icons report a distinct hover |
| click launch | clicking icon 2 produced `launch=2 target=…\ComfyUI.lnk`; the window that came forward was the launched launcher's, **not** the dock's |
| non-activating | `WS_EX_TOOLWINDOW \| WS_EX_NOACTIVATE \| WS_EX_LAYERED`; the dock never owns the foreground |
| no plate | the empty surface is cleared to alpha 0 each frame, and no background is ever painted — and proved so over a magenta background, where a plate of any colour would be unmistakable |
| non-topmost | the backing proof also passes with `--no-topmost`, where the extended style is `0x08080080` and `WS_EX_TOPMOST` is clear, as the product requires |

With five icons the stripe is **300×106** physical pixels at 100 % (5×52 + 4×4 + 2×12), and it scales to
375×132 at 125 %, 450×159 at 150 %, 600×212 at 200 % and 900×318 at 300 %.

The five apps come from a `--paths` list rather than the product's settings file, because only three apps are
pinned there. The candidate draws whatever it is given, and with no `--paths` it draws exactly the pinned apps;
pinning two more apps made no sense for a proof that has to be repeatable.

The icons are drawn from the shell's own premultiplied BGRA (the icon reader's documented output), copied into
the DIB rather than blended — blending there would multiply the alpha twice and darken every icon edge.

### The Nexus proof: the motion engine, checked as arithmetic

Peak scale alone says the engine reached 1.8 and nothing about the shape of the wave, so `tools/nexus-proof.ps1`
drives the engine across the whole span — every icon centre and every gap between two of them — and checks the
properties that make the dock feel like a dock. The expected transformation is recomputed from the candidate's
own printed scales, so the check is self-consistent and nothing about the geometry is assumed.

| property | result |
| --- | --- |
| scale under the pointer | exactly **1.8** (`MaxScale`) on every icon centre, lift exactly **10.0** (`MaximumLift`) |
| the run keeps its pitch | every adjacent gap is the laid-out **4 DIP** at every pointer position, including the pair straddling the peak; largest error **2.5×10⁻⁵ DIP**, which is the printing precision |
| translation | exactly the accumulated half-widths walking outwards from the peak, in both directions |
| the peak icon | never moves — it is the one place the wave leaves alone |
| lift vs scale | the lift is the influence squared, so it dies away faster than the size does |
| beyond the influence radius | scale is exactly 1 and the lift exactly 0, so the far end of a long dock costs nothing |
| pointer away | all five return to scale 1, translate 0, lift 0 |

One subtlety worth recording, because it is a real behaviour rather than a defect: an icon beyond the influence
radius does **not** grow, but it does **move**. Preserving the gaps beside an enlarged icon means the whole run
slides, so "at rest" for a distant icon means unscaled and unlifted, not untranslated. The first version of this
proof asserted the stronger claim and failed a correct engine.

### Two findings worth keeping.

A proof harness that leaves the pointer resting on the dock measures the *hovering* dock, where the magnified
icons legitimately cover the gaps beside them. The first run of the final proof reported the gaps as opaque for
exactly that reason. The gate now parks the pointer away from the candidate before it samples, because the
resting state is the one the gate is about.

A near-white icon centre reads as "matches wallpaper" when the wallpaper behind it is also near-white. Checking
only the centre of each icon made that a coin flip: the ChatGPT mark is genuinely white at its middle, so it
read as "nothing drawn" while the icon was in fact drawn correctly. Each icon cell is now sampled on a 3×3 grid
and counted for *coverage* — the property that actually distinguishes an icon from an empty cell — with the
cover of the backing proof as an independent second reading. Eye-checking the capture is what caught this: the
numbers alone said two of three icons had been drawn.

**Not yet done, and not claimed.** Drag reorder, multi-monitor, accessibility, and running apps are out of scope
for this milestone. The input adapter polls the cursor rather than consuming the process-wide
`RawPointerBroker`; a product renderer would consume the broker, which is already renderer-independent.

### The gate that could not fail, and the one that can

The comparison above is the right test but it has a hole on this machine, and the hole had to be closed before
the result meant anything: **the wallpaper where the dock sits is white** (`249,249,249`). An opaque *white*
plate — the exact defect Operation Clear Dock exists to remove — would read identically to the wallpaper at
every probe point. A gate that passes whether or not the bug is present is not a gate.

`tools/backing-proof.ps1` closes it by removing the coincidence. A solid **magenta** window is placed directly
behind the dock, covering it with a margin. Magenta is a colour no dock plate would ever be, so a white, grey,
Mica or Acrylic plate is hundreds off it. Reading the composited screen again:

| probe | expected if transparent | measured | verdict |
| --- | --- | --- | --- |
| gap between icons 0 | magenta `255,0,255` | `255,0,255` | exact |
| gap 1 | magenta | `255,0,255` | exact |
| reserve band above the icons | magenta | `255,0,255` | exact |
| beside the dock, left | magenta (backing is real) | `255,0,255` | exact |
| beside the dock, right | magenta | `255,0,255` | exact |
| same gaps, dock hidden | magenta | `255,0,255` | exact |
| icon 0 / 1 / 2 over the backing | not the backing | 6/9, 7/9, 9/9 points differ | icons genuinely drawn |

The screenshot shows it directly: three real icons sitting on pure magenta, with the desktop wallpaper and the
taskbar visible *outside* the backing rectangle. The ChatGPT mark's interior shows magenta through its own
transparent pixels, which is per-pixel alpha doing its job rather than one constant alpha for the window.

**Exact geometry, and a harness bug worth recording.** The stripe was verified to scale exactly on100 / 125 / 150 / 200 / 300 %:

| scale | surface | icons read at | expected |
| --- | --- | --- | --- |
| 100 % | 188×106 | 52 px | 188×106, 52 px |
| 125 % | 235×132 | 65 px | 235×132, 65 px |
| 150 % | 282×159 | 78 px | 282×159, 78 px |
| 200 % | 376×212 | 104 px | 376×212, 104 px |
| 300 % | 564×318 | 156 px | 564×318, 156 px |

This machine has only a 100 % display, so the higher scales are forced through `--scale`, because a scale path
that has never executed is a scale path that does not work. The icons are re-read from the shell at each
scale's pixel size, so a 200 % dock gets genuinely sharper artwork rather than a stretched bitmap.

Both harnesses originally reported a false FAIL here, and the cause was the harness rather than the candidate:
it assumed **five** icons and computed its expected width from that, while the product's settings file pins
**three**. Every other number (height, icon pixel size, and the per-scale ratios) matched exactly at every
scale. The probe points are now derived from the window the candidate actually created — the pinned count is
read back out of the width — because a harness that guesses the geometry reports its own guess as a finding.
With three apps a fourth "gap" point lands inside a real icon and reads as an opaque plate that does not exist,
which is precisely the false alarm the earlier runs produced.

**Window semantics.** With `--no-topmost` the extended style reads `0x08080080`: `WS_EX_LAYERED`,
`WS_EX_TOOLWINDOW` and `WS_EX_NOACTIVATE` set, **`WS_EX_TOPMOST` clear** — which the product requires, since
`IsAlwaysOnTop = false`. In that mode the window sits in the normal band with the desktop below it, and the
foreground window is unchanged. The earlier proof ran topmost only for capture convenience; the flagged run
proves the same transparency without it.

**A second instance of the same trap, in the harness rather than the product.** Putting the magenta backing
behind a *non-topmost* dock took four attempts, and the reason is worth keeping because it is the A1/A2/B/C
lesson again in a new place:

- Leaving the backing in the normal band let it sink behind other windows, so the probe read the wallpaper. That
  looked exactly like a dock failing to be transparent, and it was not: with the dock hidden the same points
  were also not magenta, which is what gave the fault away.
- Promoting the backing to the topmost band put it *above* the non-topmost dock, so every "transparent" reading
  was the backing's own colour and no icon was ever visible. A perfect magenta result that proved nothing.
- `SetWindowPos(dock, hwndInsertAfter: backing, …)` **returned success and did not move the window.** Measured
  directly: the dock stayed two ranks below the backing. This is precisely the trap the failed routes hit — the
  API accepts the call and does nothing — and it is why the harness now *walks the z-order and checks the rank*
  instead of trusting the return value.
- `SetWindowPos(dock, HWND_TOP, …)` does move it, measurably: the dock went from rank 29 to 22 while the
  backing stayed at 27.

So the backing proof now asserts, before believing a single pixel, that the dock really is above the backing,
and both the topmost and non-topmost runs report their ranks. A harness that cannot tell "the dock is
transparent" from "the dock is hidden behind the thing I am measuring through" is not evidence.

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
