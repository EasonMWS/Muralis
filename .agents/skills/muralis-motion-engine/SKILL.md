---
name: muralis-motion-engine
description: Use when changing anything that follows the pointer - Dock magnification, Nexus Motion, pointer tracking, DockIcon transforms, hover/press interaction, drag motion, motion profiles or easing, settle/leave animation, or the Dock's pointer input source and its raw-input broker. Also use when a pointer-driven surface feels laggy, jittery, or double-scaled.
---

# Muralis motion engine

## When to use

- Dock magnification, wave/peak behaviour, lift, reserves, or `DockMotionProfile` numbers.
- Anything in `src/Muralis.Core/Motion/`, `src/Muralis.App/UI/Motion/`, or `DockMotionCoordinator`.
- Pointer input: how reports arrive, how they are queued and applied.
- Hover, press, selected, running states on a `DockIcon`.
- Drag behaviour (`PinnedZoneDrag`) and its relationship to magnification.
- A "the dock feels wrong" report: lag, stutter, jump, overshoot, or an icon growing twice.

## Core rules

**Active tracking is direct and 1:1. This is not negotiable.**
While the pointer is down or moving, the visual follows the pointer on the same report. Never
introduce a spring, lerp, smoothing, prediction, or a fixed-rate timer into the active path: a
dock that eases towards the cursor is a dock that is behind the cursor.

**Easing is only for states that are not the pointer**: leave, settle, release, and enter/exit
transitions. Knowing *when* to animate is part of the rule — animating the follow is not.

**Three visual owners, one transform channel each.**
`UIElement.Scale`/`Translation`/`CenterPoint` are composition-backed and **mutually exclusive with
`RenderTransform` on the same element**. `DockIcon.xaml` therefore splits into three hosts, and
each has exactly one writer:

| Element | Owns | Written only by |
| --- | --- | --- |
| `MotionHost` | drag `RenderTransform` (TranslateX) and z-order depth | `PinnedZoneDrag`, `DockIconMotion.SetDepth` |
| `NexusMotionHost` | `Scale`, `Translation`, `CenterPoint` | the Nexus engine, via `DockIconMotion` |
| `InteractionHost` | hover/press `RenderTransform` | the icon's own local preview |

`DockIcon.MotionTarget` is `NexusMotionHost` — the magnification layer, never the drag layer.
Never write a second channel onto an element another owner already uses. Two owners on one channel
is the single most likely way to break this file.

**The engine must not double-apply with the local hover preview.**
While Nexus motion is active the icon suppresses its own hover *scale* (guarded by
`_nexusMotionActive`) and applies depth only, so the peak stays the engine's answer near
`MaxScale` instead of the two multiplying. Keep that guard.

**Motion numbers come from two token sources, and you must pick the right one.**

| Concern | Source of truth |
| --- | --- |
| Interaction durations and hover/press scale for ordinary UI | `UI/Tokens/Motion.xaml` and its mirrored `MotionDurations` in `InteractionMotion.cs` (Fast 120 / Standard 180 / Slow 280 ms) |
| Dock magnification: base size, spacing, max scale, influence radius, lift, exit margin, spread | `DockMotionProfile` |

The duplication of the 120/180/280 values between `Motion.xaml` and `MotionDurations` is
deliberate and test-locked — do not "unify" it without deciding which one wins.

`DockMotionProfile` is the single source for the **wave**, and the pointer path must not invent a
constant of its own — not a radius, not a duration, not a threshold. Note that its `PressScale`,
`HoverScale` and `SettleDurationMilliseconds` currently have no consumer in `src`, and that the dock
icon's own local hover scale is a separate literal in `DockIcon.xaml.cs`. If you touch hover or
press scaling, that divergence is real: check which value actually applies on the path you are
changing instead of assuming the profile drives it.

**Raw input has one process-wide owner, and it is not the Dock.**
`RawPointerBroker` owns the single native registration; Dock motion and the desktop pointer router
are **consumers** that subscribe to it. Neither consumer may call the registration API directly,
and defaulting back to the per-consumer registration is a regression. The registration is passive
(`RIDEV_INPUTSINK`, mouse only, generic-desktop usage page) so reports arrive while the window is
in the background — which is the only reason a non-activating dock can follow the pointer at all.

**Reports are queued individually, never frame-coalesced.**
Raw reports go into a per-sample queue and are drained in order, so a fast sweep produces the
positions it passed through rather than one merged jump. Do not "optimise" this into a
latest-position variable.

**Pointer identity comes from geometry, not from the window under the cursor.**
Decide enter/leave from the Dock's own rectangles (`DockPointerMath.RestingRegion` /
`ExpandedRegion`). Asking Windows which window owns a pixel has been measured answering "the
desktop" while the pointer was visually over the dock.

**One coordinate space, stated explicitly.**
Screen pixels are converted to the Dock's own space once, using the window's client origin and
scale. Do not mix a UI-framework transform with a Win32 one and assume they agree — they are in
different spaces. Prefer a conversion that a test can check without a window: keep the arithmetic
in `Muralis.Core` with no XAML, no composition and no pointer types.

**Enter/leave is hysteretic.** A point inside the expanded region keeps the Dock expanded until it
leaves the region *plus* the exit margin, so a one-pixel boundary wobble cannot resize the window.

**Two motion namespaces coexist on purpose.**
`Muralis.Core.Motion` is the Nexus engine. `Muralis.Core.Dock` is the older rail geometry from the
retired Phase 3 dock (`DockFrame`, `DockGeometry`, `DockOptions`, `DockMagnification`,
`DockAutoHide`, `DockEntry`, `DockEdge`, `DockReorder`). They are deliberately separate. Do **not**
delete `Muralis.Core.Dock` wholesale — `DockReorder` is still live and used by the pinned-zone drag
for its target index. The rule is that the current dock's motion must not acquire a dependency on
the retired rail geometry, not that the namespace should vanish.

**Drag outranks motion.** While a drag owns the pointer, the magnification wave is not drawn and
the carried icon takes its transform from the drag. Do not run both.

## Forbidden patterns

- Spring, lerp, smoothing, prediction or a fixed-rate ticker in the active follow path.
- Writing `Scale`/`Translation`/`CenterPoint` and `RenderTransform` on the same element.
- Driving magnification through `Width`, `Height` or `Margin`, or animating layout at all.
- Resizing or repositioning the window from the pointer path — the Dock *reports* that it wants
  different bounds and the host decides. Keep `PointerInsideChanged` (or equivalent) as the only
  channel.
- Any layout traversal, `Measure`/`Arrange`, visual-tree walk, settings I/O, shell call, icon
  extraction or persistence per pointer report.
- Registering a raw input device class outside the process broker.
- Global hooks (`SetWindowsHookEx`, low-level mouse/keyboard) or click-through hacks
  (`WS_EX_TRANSPARENT`, `HTTRANSPARENT`) as a way to see the pointer.
- `RIDEV_NOLEGACY` / `RIDEV_CAPTUREMOUSE` — they change what the rest of Windows receives.
- A motion constant hardcoded at a call site.
- Judging the pointer with `WindowFromPoint`/`GetAncestor`.

## Relevant architecture / files

- `src/Muralis.Core/Motion/DockMotionProfile.cs` — every tunable in one place; read it first.
- `src/Muralis.Core/Motion/DockMotionEngine.cs` — scale/shift/lift maths, allocation-free.
- `src/Muralis.Core/Motion/DockPointerMath.cs` — coordinate conversion and region tests.
- `src/Muralis.App/UI/Motion/DockIconMotion.cs` — the only writer of the magnification layer.
- `src/Muralis.App/UI/Controls/DockIcon.xaml` / `.xaml.cs` — the three hosts and the guard.
- `src/Muralis.App/UI/Dock/DockMotionCoordinator.cs` — queue, regions, transitions, publishes.
- `src/Muralis.App/UI/Dock/DockMotionBounds.cs` — resting vs expanded window geometry.
- `src/Muralis.Desktop/Input/RawPointerBroker.cs`, `RawInputRegistry.cs` — the process owner.
- `tests/Muralis.Desktop.Tests/Architecture/Phase4DArchitectureTests.cs` — the executable rules.
- `tests/Muralis.Core.Tests/Motion/` — engine and pointer-math guarantees.
- `docs/PHASE4D-NEXUS-MOTION.md` — the motion model as designed.

## Required verification

1. `./tools/phase4d-verify.ps1` — all tests, 0 warnings (`muralis-build-verification`).
2. The architecture guards for motion and pointer ownership must still pass; if you added a new
   ownership rule, add its guard.
3. Run it. Motion is a runtime property: build and unit tests passing is not evidence that the
   follow is 1:1 or that nothing double-scales. See `muralis-verification` for what counts as
   runtime proof, and `muralis-performance` if the change is on the hot path.
4. Any change to tracking must be re-measured with real pointer movement at more than one speed —
   a slow sweep and a fast one — because a backlog and a smoothing filter look identical in a
   single slow test.

## Stop / escalation conditions

- The fix appears to require a fourth transform channel, or sharing an existing one → stop; the
  composition model is the constraint, not an obstacle to route around.
- You are adding a timer, a spring, or a smoothing constant to make motion "feel better" → stop and
  re-read the direct-tracking rule.
- A pointer-position problem is being solved by resizing the window, moving the Dock, or adding a
  transparent hit-test surface → stop; that is the wrong layer.
- Input reports stop arriving, or two components both want the device class → stop and treat it as
  an ownership problem in the broker, not a local patch.
- The engine's own numbers (`peak`, applied vs queued, latency percentiles) contradict what you
  believe the code does → stop and measure; do not adjust constants to hide it.
- Your measurements disagree with themselves run to run → stop and find out why before reporting
  either number. An unstable surface makes every reading meaningless.
