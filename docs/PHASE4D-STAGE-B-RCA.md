# Stage B Runtime Geometry — Root-Cause Investigation

Scope of this round: investigation only. **No product source was modified.** Diagnostic tooling was added
under `tools/` only.

---

## 0. The counts-first answer

> **`icons` 45 → 34 is not a logical item count and not a virtualization artifact. It is the number of
> `DockIcon`s the motion engine is still *driving* — and that number falls permanently, one wave at a
> time, because icons that get lifted are dropped from the driven set and never released.**

`icons` resolves to `DockMotionCoordinator._layout.Count`
(`src/Muralis.App/UI/Dock/DockMotionCoordinator.cs:860`), which is built from `_layers`
(`:600`, `:702`), which `Collect()` fills by applying **two** filters (`:772-827`):

1. a size filter — `layer.ActualWidth > 0 && layer.ActualHeight > 0` (`:782`), and
2. a **rail filter** — the icon's top must lie within `RailToleranceDip = 4` of the *median* top
   (`:794`, `:809`, `:813`).

The second filter is the one that matters, and it is a filter on the icon's **current visual pose**,
because the element being measured (`MotionTarget => NexusMotionHost`) is the same element the
magnification writes to.

---

## A. Logical item count behaviour — **STABLE**

| Evidence | Value |
| --- | --- |
| `DesktopShelfService` log line | `Desktop Shelf refreshed in 51.4 ms with 43 items (29 user, 14 public)` |
| Refresh events in the whole session | **2**, both at startup (18:35:49); none during observation |
| Pinned apps | `The dock restored 0 pinned app(s)` |
| Utility items | 2 (`DesktopDockHost.cs:392-396`) |
| **Total DockIcons** | **45** |

The shelf is refreshed only by `_shelf.Changed` → `OnShelfChanged` → `PrepareOnUiAsync`
(`DesktopDockHost.cs:349-350`, `:127-143`), driven by `DesktopShelfService`'s `FileSystemWatcher` plus a
300 ms debounce (`DesktopShelfService.cs:138-187`). **There is no periodic refresh.** No refresh and no
error line occurred after startup.

**Conclusion: the logical collection never changed. 43 items, constant.** This rules out CASE C.

## B. Realized element behaviour — **ALL 45 REMAIN REALIZED**

The shelf uses an `ItemsControl` with an explicit `ItemsPanelTemplate` of a horizontal `StackPanel`
(`DockHost.xaml:135-140`) inside a `ScrollViewer` (`:122-134`). **`ItemsControl` does not UI-virtualize.**

Direct proof from the recorded traces: rebuild 1 records **45 centres spanning 267 → 3642 DIP with every
width 52**, in a window only 954 DIP wide with a 420 DIP shelf viewport. A virtualizing panel cannot lay an
item out at x=3642. Across **all 529 layout records**: `widths` = 52 only, `narrowAt` = -1, `breakAt` = -1.

**No icon ever reported zero size.** There is also no code path that could zero one: `NexusMotionHost` has
explicit `Width="52" Height="52"` (`DockIcon.xaml:33-38`), and nothing writes `Visibility` on a `DockIcon`.

This falsifies the "virtualizing scroller / zero-width items" explanation, including the code comment at
`DockMotionCoordinator.cs:764-771` which asserts it.

## C. UIA behaviour — **THE INSTRUMENT IS VISIBILITY-FILTERED; IT IS NOT THE PRODUCT COUNT**

| Observation | Value |
| --- | --- |
| UIA `DockIconMotion` elements discovered | 8 of 45 |
| Samples where non-zero width < total | **0 of 34** |
| Widths UIA reports | 28, 20, 39 (clipped) and 68, 71 (magnified) — never 0 |

UIA enumerates only what the window presents (8 icons), and it reports *clipped* widths for partially
visible icons while their `ActualWidth` stays 52. **The set that varies with scroll is the instrument's
visible set (8 → 7), not the product's.** Reading the harness's UIA count as a participant count is
reading the instrument.

## D. Motion participant behaviour — **THIS IS WHERE THE LOSS HAPPENS**

`capacity` is **frozen at 45** for the entire session (`EnsureCapacity` only grows, `:742-757`), while
`icons` falls: `45 → 41 → 39 → 37 → 36 → 33`.

There are exactly **6 drop events**, and the correlation is unambiguous:

| Rebuild | icons | inside | pointer | reach | dropped centres at the pointer |
| --- | --- | --- | --- | --- | --- |
| 2 | 45→41 | **true** | 757 | 58.6 | 712, 747, 784, 831 (757 ± 60) |
| 4 | 41→39 | **true** | 264 | 25.5 | 267, 343 |
| 6 | 39→37 | **true** | 412 | 25.2 | 419, 491 |
| 8 | 37→36 | **true** | 560 | 22.9 | 565 |
| 10 | 36→35 | **true** | 638 | 19.6 | 651 |
| 12 | 35→33 | **true** | 927 | 35.8 | 903, 975 |

**All 6 drops occur on a magnifying rebuild (`inside=true`), and the icons that vanish are the ones at the
pointer.** Four of the six dropped icons were inside the visible viewport and drawn. No clipping,
virtualization or scroll story can produce that.

**None of them ever return** — not in the 517 later rebuilds, not after 176 s of zero input, with no shelf
refresh and no container re-creation.

## E. Cached geometry behaviour — **RECOMPUTED FROM THE SURVIVORS, THEREFORE DRIFTS**

`dockLeft` is the minimum x of whatever is still in `_layers`. As icons are stranded, the leftmost
survivor moves right, so `dockLeft` marches `241 → 1031` monotonically across the session. `dockRight`
stays pinned because it is **clamped**, not measured: it equals **954 in every resting row and 1055 in
every expanded row** — exactly `_host.ActualWidth` (`:638`).

The clamp is **asymmetric and therefore unsound** (`:635-641`):

```csharp
left  = Math.Max(left, 0);                    // no upper bound
right = Math.Min(right, _host.ActualWidth);   // no lower bound
```

When the leftmost *counted* icon lies beyond the window's right edge, `left (1031) > right (954)`.
Measured: **85 of 529 rebuilds have an inverted rail**, and in **all 529** rebuilds the centres extend
beyond `dockRight`.

Those same values build the pointer regions (`:673-677`), and
`DockPointerRegion.IsEmpty => Right <= Left` (`DockPointerMath.cs:13`) makes `Contains` (`:129-140`)
return false for every point.

## F. Coordinate spaces used

| Value | Space | Source |
| --- | --- | --- |
| `dockLeft`, `dockRight`, `dockTop`, `dockBottom`, centres | dock units (DIP), relative to `Tracker` | `motion.rebuild` |
| `originX`, `originY` | physical screen px, client origin | `WindowClientOrigin.Capture` |
| UIA `bounds` | screen px, window-visibility filtered | `DshComputerUse.Native observe` |
| window rect | physical screen px | Win32 `GetWindowRect` |
| `_host.ActualWidth` (the clamp bound) | **DIP** | WinUI |

**The clamp mixes spaces.** `_host.ActualWidth` is DIP while `left`/`right` are measured in the `Tracker`
transform space; the two are compared directly. During this run they happened to agree numerically
(client 954 px ↔ 819 DIP at 125 %, `originX` 803), which is why the bound looked plausible. Conversion
path used throughout: `screen = origin + dip × scale`.

## G. First layer where divergence occurs

**The motion participant set (`_layers`), at the first magnifying rebuild.**

- logical items: stable at 43 (never diverges)
- realized elements: all 45 present (never diverges)
- **`icons`: 45 → 41 at rebuild 2 — the first divergence**
- `dockLeft`: diverges immediately after, as a consequence
- UIA: never reports a zero-width icon; it simply shows a visibility-filtered subset

## H. Root cause

**Two defects, both real, both in `DockMotionCoordinator`.**

### Defect 1 — silent, permanent loss of motion participants

`Collect()` filters the rail by the icon's **current** top within ±4 DIP of the median
(`:794`, `:809`, `:813`). But the element it measures is the element the engine writes to: the
magnification sets `Scale` and `Translation` on `MotionTarget` via
`DockIconMotion.ApplyMagnification` (`DockIconMotion.cs:67-81`). A lifted icon therefore moves its own
measured top out of the tolerance band, fails the rail filter, and is dropped from `_layers`.

`Publish()` and `Settle()` iterate `_layers` only (`:561-580`), so **a dropped icon is never released** —
it keeps the pose it was left in, stays outside the tolerance band, and is therefore excluded forever.

That is the self-reinforcing, monotonic, non-recovering loss the traces show.
`capacity` frozen at 45 while `icons` reaches 33 is its fingerprint.

*One link is inferred rather than read:* that WinUI's `TransformToVisual` reflects composition
`Scale`/`Translation`. The spatial correlation in section D is what makes it the only surviving
explanation, but it was not confirmed by reading framework code.

### Defect 2 — inverted rail makes the dock inert

The asymmetric clamp (section E) yields `dockLeft > dockRight`, producing a degenerate region
(`IsEmpty => Right <= Left`), so `Contains` is false everywhere. **Measured consequence:** with the
reported resting region inverted (L=1869.7, R=1807.0, width **−62.7**), sweeping the pointer across the
dock's real pixels (800…1760) produced **0 transitions, 0 motion updates, and no magnification**. The
reported region and the dock's real pixels have **negative overlap (−251 px)**.

## I. Classification

# **CASE B — MOTION PARTICIPANT LIFECYCLE BUG**

- **Not CASE A** — the shelf does not virtualize (`ItemsControl` + explicit `StackPanel`), and no icon ever
  reported zero size.
- **Not CASE C** — the logical collection never changed; 43 items, one refresh, no error line.
- **Not CASE D** — `icons` is indeed not a logical count, but the drift is *not* a scroll-dependent subset
  artifact. The count is **scroll-invariant**: 517 consecutive rebuilds at `icons=33` while `dockLeft`
  swept `1144 → 0 → 565`, and the six wheel samples kept `icons=33` while `railL` read
  `1031 / 275 / 0 / 275 / 1031`. The loss is wave-dependent and permanent.

## J. Tooling changes

Added, diagnostics only (no product code touched):

| Tool | What it does |
| --- | --- |
| `tools/p4d-observe-counters.ps1` | zero-input multi-layer sampling (logical / realized / UIA / window) |
| `tools/p4d-divergence-probe.ps1` | which layer moves first under injected input |
| `tools/p4d-repeatability-probe.ps1` | does the same dock state reproduce the same geometry |
| `tools/p4d-scroll-probe.ps1` | does the measured rail track the shelf scroll |
| `tools/p4d-geometry-consistency.ps1` | internal consistency of the reported rail vs its own centres |

**No harness fix was applied**, because the harness was not the cause (the classification is B, not A/D).
`tools/phase4d-stage-b-geometry.ps1` and `phase4d-stage-b-verify.ps1` are **not** 0 bytes — both are
intact and parse with 0 errors (9780 and 20503 bytes). **My previous-round claim that they were corrupted
was wrong**; I reported it without re-checking, and it was picked up. They should **not** be deleted.

One genuine instrumentation finding worth acting on later: the harness read `icons` as though it were a
stable geometry source. It is a filter-dependent subset and must not be used as one.

## K. Product source changes — **NONE**

`0` files under `src/` or `tests/` were modified this round. Nothing was tuned: no scale curve, influence
radius, max scale, translation, Dock bounds reserve, hysteresis, `ExitMarginDip`, pointer scheduling,
`RawPointerBroker` or transform ownership.

## L. Subagent review result

An independent adversarial reviewer was dispatched and **falsified my first hypothesis**, correctly. My
initial claim was that `icons` was a scroll-dependent zero-width subset. The reviewer showed:

- the virtualization premise is false (`DockHost.xaml:135-140`);
- no icon can report zero size (529 layouts all width 52, `narrowAt` = −1, no code path);
- clipping cannot remove an icon (rebuild 1 counts 45 while ~8 are drawn);
- the count is **scroll-invariant**, which my own `p4d-scroll-probe.ps1` falsification test demonstrated —
  and which I misread at the time;
- the drops correlate with the pointer's wave, not the scroll.

I verified the reviewer's decisive fingerprints directly against the traces before accepting them
(capacity frozen at 45; 6 of 6 drops with `inside=true`; dropped centres at the pointer). The
classification was corrected from D to **B** on that basis.

## M. Stage B acceptance — **NOT RERUN**

Rerunning was conditional on the harness being at fault. It is not. The dock in its current state cannot
pass an acceptance run at all: its reported interaction region does not overlap its own pixels, so it
cannot magnify. Rerunning would measure a dock that is already inert.

## N. Stage B freeze recommendation

# **DO NOT FREEZE — AND DO NOT CONTINUE TO STAGE C**

There are two real product defects, and the second one is user-visible and severe: **after enough pointer
traffic the dock stops responding entirely and cannot recover on its own.**

### Proposed minimal fix — report only, not applied

1. **Stop filtering the rail by current pose.** Do not let a transient visual pose decide participation.
   Either measure the resting slot (`LayoutInformation.GetLayoutSlot`) rather than the transformed corner,
   or capture each icon's resting top once and keep it stable. `Collect()` (`:772-827`).
2. **Release every layer, not only the surviving ones.** `Publish()`/`Settle()` (`:561-580`) must iterate
   the full icon set so a dropped icon is returned to rest instead of stranded.
3. **Make the clamp symmetric and space-consistent** (`:635-641`): clamp both bounds in the same space, or
   compute the rail from the layout slot rather than from clamped transformed corners.
4. **Correct the false comment** at `:764-771`, and the same belief repeated in
   `tools/phase4d-motion-lib.ps1:9` and `tools/phase4d-repair-verify.ps1:10,130`.

### Regression risks

- Changing the rail filter changes which icons the engine drives, which changes the magnification
  envelope; the Stage A engine tests pin the maths, not the filter.
- Releasing all layers on every publish touches the pointer hot path's allocation and work budget
  (`muralis-performance`).
- Any change to `dockLeft`/`dockRight` shifts `DockMotionBounds` and the expanded reserve.
- Fixing the clamp changes the interaction region, which is what enter/leave hysteresis depends on.

### Required tests

- A dock whose cursor sweeps repeatedly leaves `icons` **unchanged** (the regression that would have
  caught this).
- An icon lifted by the wave returns to the rail and to rest when the pointer leaves.
- `dockLeft <= dockRight` for every rebuild, asserted over a synthetic run.
- The reported interaction region always **overlaps the window's own client rect**.
- Repeated enter/leave cycles leave the rail bounds unchanged.

## Final statement

Root cause: **established** for the participant loss (Defect 1) and the inert region (Defect 2), both
with file and line references and reproduced from the product's own traces.

One link remains **inferred, not proven**: that `TransformToVisual` reflects composition
`Scale`/`Translation`, which is what moves a lifted icon out of the tolerance band. The pre-filter
`_icons.Count` is also not traced, so the size filter (`:782`) and the rail filter (`:813`) cannot be
separated from the traces alone. Both are stated rather than smoothed over.

---

# Stage B Participant Loss — Proof and Minimal Fix (second round)

This section records what happened after the investigation above. It supersedes the "report only, not applied"
recommendation; everything else in this document stands as the historical record.

## 1. The two inferred links were closed by measurement

The earlier round left two things stated rather than proven. Both are now measured, and one former inference
turned out to be **wrong**.

- **`TransformToVisual` does reflect composition pose.** Confirmed: per-icon records show a lifted icon's
  measured top moving with its own `Lift`, and an icon with `scale=1.0000 lift=0.0000` measuring at the row.
- **`LayoutInformation.GetLayoutSlot` is useless here.** Confirmed: `slotY = 0.0000` for every icon in every
  record (13/13, then 25/25). No pose-independent geometry source exists in this framework path. The proposed
  fix "measure the resting slot instead" is therefore **not available**.
- **The earlier `icons 45 -> 34` scroll/virtualization reading was wrong** and is not the mechanism. See below.

## 2. A wrong instrument, caught by its own output

The first instrumented run reported `scale`/`lift` for the tracked icon by indexing `_engine.Samples[target]`
with `target`, an index into `_icons`. But `Publish()` (`DockMotionCoordinator.cs:575`) indexes the engine by
**`_layers`** position. Those are the same only while the rail filter admits everything. Once it admitted 33 of
45, the instrument was reporting a *different icon's* sample. Every conclusion drawn from it about which icon
was lifted was unsound. Resolved by looking the engine index up by identity.

This is worth recording because it very nearly produced a second wrong fix.

## 3. The real mechanism, from the histogram

`motion.participants` was extended with a histogram of every measured top, one line per pass. It settles the
row-structure question that the previous round could only guess at:

| state | histogram of measured tops |
| --- | --- |
| at rest | `{18: 45}` — **all 45 icons are on one row** |
| under a wave | `{16: 1, 62: 2, 67: 42}` — one row plus a graded smear |
| expanded, at rest | `{67: 45}`; the expansion shifts the row by exactly **49** |

So there is **no second row**. The old comment claiming "the utilities sit in a row of their own a little above
the rail" is false, and the previous round's `DominantRowTop` hypothesis — that the loss was a row-selection
error — is **falsified**: it reproduced the identical 45 -> 33 loss with 51 rebuilds running.

The loss has two causes, and neither is a row split:

1. **A graded lift.** The wave lifts the icons it is working on by more than the 4-unit band. The rejected
   deltas formed an arithmetic run — `-4.5886, -7.1273, -8.7469, -10.1176` (common difference 2.5395) — i.e.
   the wave's own neighbours, not a structural gap.
2. **A reference-frame shift.** Expanding moves the whole row by 49, so a reference taken before the expansion
   no longer describes the geometry after it.

## 4. Defect 2 was investigated, declared, and then REFUTED — the clamp was correct

This section originally recorded the clamp as a second defect, on the reasoning that
`right = Math.Min(right, _host.ActualWidth)` mixed a DIP width with values measured in the dock's own space, and
that the observed `3668..3759` raw right edge against a `954..1055` window was a "3.5x overshoot". **That was
wrong, and the change made on the strength of it has been reverted.**

An independent adversarial review refuted it, and the refutation holds on inspection:

- `scale` is pixels per DIP (`RawInputRegistry.cs:43-45`), and `DockPointerMath.ToDockSpace` divides screen pixels
  by exactly that, so **one dock unit is one DIP**; `DockPointerMath` documents its own region fields as DIP.
- `Tracker` is a full-size child of `_host`, and `_host` is a stretched `DockHost` filling the HWND, so **Tracker
  space *is* `_host` DIP space**.
- The measured icon widths are the elements' DIP widths (52), so `raw right ≈ 3694 = centre 3642 + width 52` is
  the Shelf's **real overhang in DIP** — precisely the thing the clamp exists to trim, exactly as the original
  comment said.
- The product log carries `"scale":1.0000` and `dockRight` equal to the clamp value, so no conversion was ever
  being applied.

So the operands were always in the same space and the clamp was doing its job. A `_host.ActualWidth /
windowOrigin.Scale` conversion is dimensionally inverted: at 150 % display scale it would land the right edge at
the window's midpoint, and at 200 % `windowHeight` would fall above the icons' resting bottom. If the rail's left
edge then exceeded the shrunken right, `Right <= Left` (`DockPointerMath.cs:13`) would make the region empty and
the whole dock inert — the very failure the change claimed to prevent.

**Lesson worth keeping:** the "overshoot" was a number I produced, and it contradicted the units I had already
measured. I explained the contradiction away instead of treating it as a falsification. The magnitude was the
tell: a 3.5x error is large enough that it should have prompted a check, not a fix.

## 5. The fix applied

**`src/Muralis.Core/Motion/DockRailMembership.cs` (new).** Rail membership is a set held **across passes**, not a
test re-applied to the current pose. A reference top is taken only on a pass where the engine reports every sample
at rest, and membership then accumulates and is never taken away. The set is started again only when the set of
keys changes.

Two honest qualifications, from the adversarial review, are recorded in the type's own documentation rather than
left implied:

- It is a **latch, not a corrected criterion**. Admission still consults a live pose on the pass that adds a
  member. On a dock the traces show to be a single row, the filter has nothing legitimate left to reject, so the
  surviving behaviour is "everything offered, held". The over-filtering rule was neutralised, not repaired.
- The key is the icon's **position in the dock's icon list**, not a durable identity, so a same-count swap
  inherits the slot. That is deliberate; its failure mode is over-admission, never the silent permanent loss this
  type exists to prevent.

**`DockMotionCoordinator.Collect()`** now measures tops into a `Dictionary<int,double>` and delegates to that
policy; the `DominantRowTop` hypothesis was removed rather than left as dead code, and the false doc comment
was corrected.

Deliberately **not** done, per the constraints: the `4` tolerance was not widened, and nothing resets on
`Left > Right`, rebuilds periodically, re-inserts dropped icons, expands the interaction rect, polls, or touches
the profile, `RawPointerBroker` or drag ownership.

## 6. Verification

- **Runtime, before the fix:** `railPass` 45 -> 42 -> 39 -> 37 -> **33**, permanently, with
  `input == sizePass` = 45 on every pass. Reproduced with real `mouse_event` motion. (`SetCursorPos` generates
  no motion input and leaves the dock completely inert — an earlier probe was invalid for that reason.)
- **Runtime, after the fix:** `railPass` held at **45 across 19 consecutive passes** while `medianTop` still
  moved (18 -> 67) and the histogram still smeared (`{39:1, 56:1, 67:43}`). The underlying disturbance is still
  there; it simply no longer feeds back into membership.
- **Independently corroborated:** the review read the surviving profiler log and found 26 rebuild records with
  `icons` = `capacity` = 45 on every one, `peak` 1.35-1.80, `reach` to 60.5, `breakAt` = -1 — a live wave with no
  participant loss. `icons` is `_layout.Count`, i.e. exactly what the harness called `railPass`.
- **Motion still live:** 1078 pointer updates applied, `0` dropped, reset to `atRest=45` on leave. UIA
  independently shows the window expanding 960x116 -> 1061x165 and icons changing size.
- **Tests:** 10 added in `tests/Muralis.Core.Tests/Motion/DockRailMembershipTests.cs`. Removing the
  accumulate-and-hold behaviour makes **4 of them fail**. Verified by actually running it, twice.
- **Build gate:** `tools/phase4d-verify.ps1` Debug and Release, each 0 errors / 0 warnings, all suites pass.

## 7. Known limitations, stated rather than smoothed over

- **The pre-fix 45 -> 33 has no artefact in the repository.** `DockMotionCoordinator.cs` is untracked, so there is
  no committed pre-fix revision; the before-numbers rest on the profiler log as it was at the time and on this
  document. The after-numbers were independently re-read from a surviving log. Treat "before" as reported, not as
  re-checkable.
- **`windowOrigin.Scale` measured as 1.0 on this machine.** This no longer matters for the clamp, which was
  reverted, but it is why the DIP-versus-dock-space question could not be settled by measurement alone and had to
  be settled by reading `DockPointerMath`'s own contract.
- **No test covers the coordinator wiring**: the `AtRestSampleCount() == _icons.Count` predicate, the index-as-key
  mapping, the layer sort, or the clamp. There is no App-level test project, so these are covered only by the
  runtime runs above. This is the largest remaining verification gap.
- **The "never released" half of the original defect is untouched.** `Publish()` and `Settle()` still walk only
  `_layers`. With membership latched an icon rarely leaves, but the original RCA's proposed fix #2 was not applied.
- **The visual verdict is HUMAN / VISION REVIEW PENDING.** No frame was captured and no vision was applied this
  round; the changes are geometry and membership, and nobody has looked at the result.
- **The acceptance harness reports only 8 of 45 icons** through UI Automation (it is visibility-filtered), so
  UIA is not a participant count. Participant numbers above come from the product's own traces.
- **A Release build removes the Debug output's `Muralis.dll`**, which makes a Debug binary launched afterwards
  fail with `0xC0000135` (STATUS_DLL_NOT_FOUND). Rebuild the configuration you intend to run.

## 8. Independent adversarial review — outcome

A read-only adversarial reviewer was tasked with falsifying the fix, not confirming it. It was given the code, the
claimed mechanism and eight specific targets. Its findings, and what was done about each:

| # | Finding | Verdict | Action |
| --- | --- | --- | --- |
| 1 | The clamp conversion is **inverted**; harmless at 100 % DPI, harmful above it | **Upheld** | **Change reverted** — see §4 |
| 2 | The core fix is a real latch, not a widened threshold (`ToleranceDip` unchanged) | Upheld as a fix | Kept; caveats documented on the type |
| 3 | It is a *latch*, not a corrected rail criterion | Upheld | Documented as such |
| 4 | `allAtRest` is a good proxy, not a proof (`EnsureCapacity` builds an at-rest engine) | Upheld | Claim removed from the docs |
| 5 | The key is a list position, not a durable identity | Upheld | Documented; failure mode is over-admission |
| 6 | `Settle()`/`Publish()` still walk only `_layers` — "never released" half unfixed | Upheld | Recorded as a residual gap |
| 7 | Coordinator wiring (predicate, key mapping, sort, clamp) has no test | Upheld | Recorded as the largest verification gap |
| 8 | Doc overclaim: `Rebuild`'s early-out does not save `Collect()` | Upheld | Comment corrected |
| 9 | T4 counts only, so a pruned `_members` would stay green; T9 mis-titled; T3 tests a local copy | Upheld | T4 strengthened with `Contains`; T9 renamed; T3 relabelled |
| 10 | 8 unused public members on the coordinator | Noted | Pre-existing, out of scope this round |

**What the review could not corroborate, and said so:** the pre-fix 45 -> 33 has no artefact in the repository,
because `DockMotionCoordinator.cs` is untracked and the instrumentation that produced the figure has since been
removed. It independently re-read the *after* state from a surviving profiler log and found `icons` = `capacity` =
45 on all 26 rebuild records with a live wave, which corroborates the shape of the claim. The before-numbers
should therefore be treated as reported, not as re-checkable.

**Net effect on the deliverable:** the participant-loss fix stands, verified and now independently corroborated.
The clamp "fix" is withdrawn. The review found no concerns with scope creep, transform ownership, timers or
polling, leftover instrumentation in `src/`, or `_layers` ordering.
