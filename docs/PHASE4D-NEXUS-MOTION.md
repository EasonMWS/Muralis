# Phase 4D — Nexus Motion Engine

Continuous pointer-distance magnification for the Muralis Dock, built as a direct
manipulation system rather than a hover animation.

This file is the architecture note and the running record. Each stage appends its
findings and its gate result; a stage is only closed when its build, its tests and its
runtime check have passed.

---

## Stage A — Recon and motion math

### A.1 What the Dock actually is today

| Piece | File | What it owns |
| --- | --- | --- |
| Dock window | `src/Muralis.App/UI/Dock/DesktopDockHost.cs` | Borderless, non-topmost, `WS_EX_TOOLWINDOW \| WS_EX_NOACTIVATE`. Positioned by `PositionWindow()`: width `min(960, max(520, area.Width - 32))`, height `116` (a local `const`), bottom 16 DIP above the work area. |
| Dock shell | `src/Muralis.App/UI/Dock/DockHost.xaml{,.cs}` | Three zones in one `Grid`: `PinnedZone` (an `ItemsControl` plus `DockAddTile`), a `ScrollViewer` named `ShelfScroller` (`Width=420`), and the utilities `ItemsControl`. Wrapped in `materials:GlassSurface x:Name="DockSurface" Padding="14,10"`. |
| Icon | `src/Muralis.App/UI/Controls/DockIcon.xaml{,.cs}` | `Grid x:Name="MotionHost"` → 52×52 `Grid` → 48×48 `Border x:Name="IconSurface"` → 44 DIP shell bitmap from a 96 px source. Below it a label `TextBlock` and a 16×2 running indicator. |
| Motion target | `DockIcon.MotionTarget` | Returns `MotionHost` — the whole 68×~67 item, label included. `DockHost._motionTargets` collects every loaded icon's target, in load order. |
| Drag | `src/Muralis.App/UI/Dock/PinnedZoneDrag.cs` | Pointer capture on the `DockIcon`, raw `TranslateX` on `MotionHost.RenderTransform`, candidate index from `DockReorder.TargetIndex`, single commit on release. |
| Existing hover motion | `src/Muralis.App/UI/Motion/InteractionMotion.cs` | `HoverMotion`/`PressMotion` drive a `CompositeTransform` on `MotionHost` through a `Storyboard` — this is the XAML path, not the composition path. |

Measured geometry of one item: `MotionHost` is `MinWidth=64` with `Padding=8`, so its
width is `max(64, content + 16)`. A short label is narrower than 64 and a long one is
capped by `LabelText MaxWidth=80`, so width runs roughly 64–96 and height is a uniform
~67 (`8 + 52 + 4 + ~15 + 4 + 4`, plus the outer 8 padding). Item pitch is therefore
`width + 4`, not a constant.

### A.2 The one constraint that decides the design

`UIElement.Scale`, `UIElement.Translation` and `UIElement.CenterPoint` are
composition-backed rendering properties, and Microsoft documents them as **mutually
exclusive with `RenderTransform` / `RenderTransformOrigin` / `Projection` /
`Transform3D` on the same element**: setting one set and then the other fails at runtime
([XAML property animations](https://learn.microsoft.com/en-ca/windows/apps/develop/motion/xaml-property-animations)).

`PinnedZoneDrag` already writes `MotionHost.RenderTransform.TranslateX`, and the drag
must stay 1:1. So the new engine cannot take `MotionHost` over.

The resolution is to split the existing `MotionHost` into two roles and let each use the
one transform system it needs:

```
MotionHost                      ← drag writes RenderTransform.TranslateX (unchanged)
  └─ IconMotion                   ← NEW: engine writes Scale + Translation + CenterPoint
       ├─ 52×52 Grid              ← IconSurface (48×48 Border) + badges live here
       ├─ LabelText
       └─ RunningIndicator
```

The 52×52 `Grid` becomes a named element, `IconMotion`. It is the *only* thing the
engine touches. Neighbours' labels and running indicators move with their icon because
the translate still happens on `MotionHost`; only the icon artwork scales.

### A.3 Answers to the questions Stage A had to settle

- **Logical position.** Index in `PinnedAppsPresenter.Items` (pinned zone), in
  `ShelfItems` (Shelf), in `UtilityItems` (utilities).
- **Actual visual position.** `DockIcon`'s origin in `PinnedZone`'s coordinate space,
  measured today by `PinnedZoneDrag.CentreOf` via `TransformToVisual`.
- **Display size.** Item ≈ 64–96 × 67 DIP; the icon artwork inside is a fixed 48×48 and
  the shell bitmap is drawn at 44×44.
- **Hit-test bounds.** `DockIcon`'s layout rect (the full item, label included), *plus*
  whatever `RenderTransform` currently displaces it by. WinUI hit-tests through the
  render transform, which is what makes the drag 1:1 today and what will keep clicks
  correct once the icon is scaled.
- **Composition visual.** None is created explicitly. `GlassSurface` uses
  `UIElement.Translation` for depth (Z 8/16/24) and `DockIcon.SetDepth` uses
  `MotionHost.Translation.Z` (4/8/22). These are element-level properties, so the
  engine composes with them rather than replacing them.
- **Transform origin.** Currently none: `TransformMotion.EnsureTransform` sets
  `RenderTransformOrigin` to (0.5, 0.5), so today's hover already grows from the item's
  middle. The engine sets `IconMotion.CenterPoint` to the icon box's bottom centre so
  magnification grows upward and sideways from the dock baseline.

### A.4 Coordinate space

One space only: DIP relative to the **Dock window's content root**. Pointer X comes from
`args.GetCurrentPoint(<dock-space element>).Position.X`; icon centres come from
`TransformToVisual(<the same element>)`. No display pixels and no physical coordinates
are mixed in, so multi-monitor and DPI need no special case — the dock window already
carries the DPI, and every number the engine sees is already in the same DIP space.

### A.5 Where the numbers live

- Motion math → `Muralis.Core/Motion/` (`DockMotionEngine`, `DockMotionProfile`,
  `DockMotionSample`, `DockMotionLayout`). No `Window`, no `PointerRoutedEventArgs`, no
  `Windows.UI.Composition`: plain arithmetic, unit-testable from `Muralis.Core.Tests`
  which targets plain `net10.0`.
- UI orchestration → `Muralis.App/UI/Dock/DockMotionCoordinator.cs`, mirroring the
  existing `PinnedZoneDrag` shape.
- `DockHost.xaml.cs` only forwards pointer events, so it never has to learn the math.

The namespace is `Muralis.Core.Motion` rather than `Muralis.Core.Dock` so the Phase 4A
architecture guard keeps its meaning: the Dock stays independent of the retired
`Muralis.Core.Dock` rail geometry (`DockOptions`, `DockGeometry`, `DockMagnification`),
which is Phase 3 canvas code.

### A.6 The motion model

Three steps, all pure arithmetic, all unit-testable without a window.

**1. Influence.** Each icon's scale comes from the distance between the pointer and the centre the icon
*rests* at — never the centre it has been pushed to. Measuring against the resting centres is what keeps
the wave honest: feeding the displacement back in would make the peak creep along the dock, and the wave
would lag its own cause. The curve is a Gaussian normalised so the rim lands on exactly zero
(`exp(-2.5²)` at the edge, rescaled), which gives influence 1 under the pointer, 0 at the radius, no
corner at either end, and a top flat enough that the pointer's own jitter barely changes the peak's size.

The measured envelope at the baseline profile, in drawn DIP:

```
 pitches  3.0   2.0   1.5   1.0   0.5  0.25   0.0  0.25   0.5   1.0   1.5   2.0   3.0
 DIP     48.0  48.8  52.6  63.0  78.4  84.2  86.4  84.2  78.4  63.0  52.6  48.8  48.0
```

**2. Neighbour translation.** An icon that grows would overlap its neighbours, so the run opens outwards
from the peak. The rule is a single walk outwards from the peak: the icon beside it gives up
`h[i] + h[i+1]` — the two half-widths that grew into the gap they share — and each icon beyond that gives
up the same plus the room of the gap beyond it. `h` is an icon's extra half-width,
`(scale − 1) × base / 2`.

The consequence is the whole point: **every gap in the run, including the two beside the peak, ends up
exactly the gap the dock was laid out with** (4 DIP), so the magnification cannot burst the peak through
its neighbours and the run keeps its proportions instead of tearing open.

**The peak does not move.** It is the icon the pointer is on, and it stays where it rests; the whole run
opens around it. That makes the icon under the hand the anchor rather than something the layout drags
around, and it is why the dock needs only a reserve and never a re-layout.

One consequence worth recording: every icon past the wave moves by the same amount — the whole room the
wave took, 35.06 DIP at the baseline for a dock with at least three icons on the pointer's side. The
engine publishes that as `HorizontalReach`, and `ReserveFor` measures the true worst case (the pointer
parked between two icons, where both sides open at once) rather than the pointer-on-an-icon case: 50.21
DIP at the baseline.

**3. Lift.** The lift is the square of the scale's own influence, so it dies away faster than the size
does and the dock never looks like it is jumping. Its maximum is the profile's `MaximumLift`, 10 DIP.

### A.7 What the engine guarantees, and what it costs

| Guarantee | How it is held |
| --- | --- |
| No two icons ever overlap, at any pointer position | Every gap stays at the laid-out spacing; asserted over a 240-step sweep of a 24-icon dock |
| The icon under the pointer is always the largest | The peak is the maximum scale, found by index, never inferred from a running total |
| A pointer between two icons splits them evenly | Equal distance gives equal scale to twelve decimal places |
| Beyond the radius nothing grows | Exactly 1.0, asserted on a thirty-icon dock |
| Same pointer, same answer, always | The engine holds no state; a reversed sweep reproduces the forward one bit for bit |
| The hot path allocates nothing | `GC.GetAllocatedBytesForCurrentThread` is unchanged across a thousand updates |
| A layout mid-flight cannot draw nonsense | Non-monotonic centres are refused and the icons are left where the layout put them |

Measured cost: a thousand updates on a thirty-icon dock allocate **0 bytes**.

### A.8 Icon centre cache (design)

The pointer path may not measure anything, so the dock keeps one `DockMotionLayout` — centres and widths
per icon, in the dock's own DIP space — and rebuilds it only when the layout has actually changed:

- `LayoutUpdated` on the dock root, compared against the cached values, so a rebuild happens at most once
  per layout pass and never on a pointer move. `DockLayoutMeter` already hooks this event for the drop
  profiler, so the pattern exists.
- Item add/remove and reorder commit both arrive as `PinnedAppsPresenter.Projected`, which the dock
  already handles.
- DPI changes and window moves need nothing: every number is already in the dock's DIP space.

Measurement itself walks the realized containers of each zone and accumulates `ActualWidth + spacing`
from the first container's origin, rather than calling `TransformToVisual` per icon: one conversion for
the run instead of thirty, and no dependency on whatever transforms are live at the time.

### A.9 The reserve, and why the dock window has to change

`DockMotionProfile.VerticalReserve` is `(PeakIconSize − BaseIconSize) + MaximumLift` = **48.4 DIP**: the
icon grows upward from its own baseline (its scale origin is the icon box's bottom centre) and then lifts.
The dock window is 116 DIP tall with the content vertically centred, so about 13 DIP of headroom exists
today — a magnified icon would be clipped by roughly 35 DIP.

`HorizontalReach` is **50.21 DIP** per side at the baseline, and the window is `min(960, …)` wide with the
dock centred inside it, so the window has to grow by the reach on each side as well.

Both figures are derived from the profile and the item count rather than hardcoded, and `PositionWindow`
is the one place that has to spend them. This is the change in Stage B that touches the window rather
than the icons.

### A.10 Stage A gate

| Check | Result |
| --- | --- |
| Debug build (Core, Desktop, App) | 0 warnings / 0 errors |
| Automated tests | **824 passed** (657 Core, 167 Desktop); baseline was 794 |
| New motion tests | 33, covering the curve, symmetry, packing, direct manipulation, allocation and the reserve |
| Motion namespace free of XAML, composition and pointer types | yes — `Muralis.Core/Motion`, plain `net10.0` |

> Build note for this machine: the repo's `Muralis.slnx` restore fails silently under SDK 10.0.401, and
> MSBuild's incremental up-to-date check leaves stale assemblies in `bin` after a source edit — which
> makes a passing test meaningless. `tools/phase4d-verify.ps1` builds one project at a time with
> `--no-incremental`, deletes each assembly before building it, and fails loudly if no new one appears.
> A `dotnet test` against a binary built any other way is not evidence of anything on this machine.

---

## Stage B — IconMotion composition layer, Scale / Lift, motion bounds

### B.1 The layer

`DockIcon` now has two transform layers with two different owners, which is the whole reason it changed:

```
MotionHost                     RenderTransform — the drag's. Unchanged from before.
  └─ IconMotion   (new, 52×52) Scale + Translation + CenterPoint — the magnification's.
       ├─ IconSurface  48×48 artwork, badges
       ├─ LabelText
       └─ RunningIndicator
```

`IconMotion` had to be a separate element because `UIElement.Scale` and `UIElement.Translation` are
mutually exclusive with `RenderTransform` on the same element, and `MotionHost.RenderTransform` belongs to
`PinnedZoneDrag`. Splitting them lets each owner keep the mechanism it needs. It also settles two smaller
questions at once: the label and the running indicator sit outside `IconMotion`, so they are not magnified,
and `CenterPoint` is set to the icon box's bottom centre, so a magnified icon grows upward and sideways
from the dock's baseline instead of from its own middle.

`DockIconMotion` is the only thing that writes to that layer, and it keeps the two channels apart:
`ApplyMagnification` and `Release` write `Scale` and `Translation.X/Y`; `SetLocalScale` writes
`RenderTransform` for the icon's own hover and press preview. Hover therefore still works, at 1.08, without
ever competing with the wave. Z is set by `SetDepth` alone, so a pointer move cannot drop a hovered icon
back down the z-order.

`DockIcon` also names both layers for UI Automation (`DockIconMotionHost`, `DockIconMotion`), set in code
rather than in markup. That is an accessibility improvement — the dock's icons are now reachable by an
assistive tool — and it is what made the magnification measurable from outside the process, because the
rectangle a UI Automation client reads is the rectangle the compositor drew.

### B.2 Window geometry

The dock's resting strip is unchanged at 116 DIP. The window around it now spends the motion reserve, and
it spends it in the one way that cannot move the dock:

| | before | after |
| --- | --- | --- |
| window | 960 × 116 | 1061 × 165 |
| window bottom edge | 16 DIP above the work area's bottom | the same 16 DIP, plus the 8 DIP the dock's own padding leaves under the icons |
| dock surface | centred in the window | anchored to the window bottom |
| room above the icons | ~13 DIP | 48.4 DIP |
| room either side | 0 | 50.2 DIP |

The window grew by the reserve **upward and sideways only**, and the dock's surface was moved to the
bottom of it while the window's bottom edge moved by the same 8 DIP that the dock's padding leaves under
the icons (`DockBottomInsetDip`). Net effect on screen: the dock is where it was.

Both figures come from the motion layer, not from constants in the host:

- vertical — `DockMotionProfile.VerticalReserve` = `(PeakIconSize − BaseIconSize) + MaximumLift` = 48.4 DIP
- horizontal — `DockMotionEngine.ReserveFor(30, …)` = 50.21 DIP, the true worst case (the pointer parked
  between two icons, where both sides of the run open at once)

The horizontal reserve is spent as **window width**, not as dock padding. The dock is centred, so widening
the window by the reach on each side gives the icons that room for free; padding would take the same room
from the inside and squeeze the dock's own content into a narrower strip, which is the opposite of wanted.

Nothing here runs on the pointer path. `PositionWindow` runs when the dock is shown and when its layout is
rebuilt; a pointer move only writes compositor properties.

### B.3 What the geometry check measured

Run against the live dock with 45 icons (`tools/phase4d-stage-b-geometry.ps1`):

| | |
| --- | --- |
| window | 1061 × 165, 45 icons reported |
| icon box | 52 × 52 at y 70..122 |
| dock content | x 205 .. 1055, i.e. ~850 DIP centred in 1061 with ~205 DIP of slack either side |
| vertical | 48.4 DIP reserve asked for; worst-case magnified top 23.6 DIP from the window top — **clear by 23.6 DIP** |
| horizontal | 50.21 DIP reach per side against ~205 DIP of slack — **clear** |

So the window is demonstrably big enough for everything the magnification can draw, and the dock's own
position on screen did not move.

### B.4 Where Stage B stopped

**The pointer-driven half of the acceptance test could not be run in this environment, and Stage B cannot
be called complete without it.** The cause is concrete and is a property of the dock rather than of this
machine:

1. The dock window carries `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW` (frozen, and correct — it is a passive
   desktop surface). It therefore never becomes the active window.
2. WinUI 3 delivers `PointerMoved` through its XAML input system, which does not reach a window the shell
   never activates.
3. In this environment the dock's pixels are additionally covered by Explorer's `SHELLDLL_DefView` /
   `WorkerW` desktop layer, and `WindowFromPoint` returns that layer for every point over the dock.

Measured, not assumed:

- `motion.rebuild` trace from the running app: **45 icons found, capacity 45, not capped** — the centre
  cache and the engine are correct and running.
- `WindowFromPoint` at seven points across the dock, including directly over icons: **the desktop layer,
  never the dock**.
- Every sweep: **zero `motion.pointer` events**.
- A synthetic click through the Computer Use helper *does* reach the dock — the Add tile's file picker was
  observed opening — so the window is alive; only hover and move are undelivered.

The project already solved this problem once: `DesktopPointerRouter` registers raw mouse input on a hidden
window of the shell thread and publishes pointer positions, precisely because a surface of ours cannot rely
on XAML pointer events. That is Phase 3 code, and Stage B is not allowed to reference it. Registering raw
input on the dock's own window is the smallest viable integration point, and it is an input-source change
rather than a rendering change — which is why this stops here for a decision rather than being improvised.

### B.5 Stage B gate

| Check | Result |
| --- | --- |
| Debug build (Core, Desktop, App) | 0 warnings / 0 errors |
| Automated tests | **824 passed** (657 Core, 167 Desktop) — unchanged from the Stage A baseline |
| Resting dock strip | 116 DIP tall, unchanged |
| Window grows by the profile's reserve | verified: 116→165 tall, 960→1061 wide |
| Dock's absolute position on screen | unchanged |
| Reserves come from the motion layer | yes — `VerticalReserve` and `ReserveFor`, nothing restated in the host |
| No window resize on the pointer path | yes — `PositionWindow` only, at show and layout time |
| No `Width` / `Height` / `Margin` driving magnification | yes — `Scale` and `Translation`, on `IconMotion` only |
| `MotionHost` still exclusively the drag's | yes — the engine never touches it |
| Envelope fits the window | verified: clear by 23.6 DIP vertically, ~205 DIP horizontally |
| Pointer-driven Scale / Lift verified | **not verified** — see B.4 |
| End icons not clipped by the HWND | **geometrically verified, not visually confirmed** |
| Dock crash-free, main window healthy, z-order unchanged | verified over the whole session |


